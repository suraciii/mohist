import { rm, stat } from "node:fs/promises"
import { isAbsolute, join, relative, resolve } from "node:path"
import type { ActionContext, ActionResult, JsonObject, JsonValue, AddTaskInput, RenderedWorkItem, WorkItemResult } from "../core/types.js"
import { stringInput } from "../core/json.js"
import { stringAt } from "../core/json-path.js"
import { renderTemplate, unresolvedReferences, wholeStringUnresolvedReferences } from "../core/template.js"
import { ensureDir, runCommand } from "../system/process.js"
import { runnerVariables, WorkspaceManager } from "./workspace.js"
import type { ActionRegistry } from "../actions/registry.js"
import { acpAgentAction } from "../actions/acp-agent.js"
import { git as defaultGit } from "../actions/git.js"
import type { ServerConnection } from "../server/connection.js"
import type { AcpSessionManager, SharedAcpConnection } from "./acp-connection.js"
import {
  actionProducedArtifacts,
  captureArtifacts,
  summarizeCaptureFailures,
  uploadCapturedArtifacts,
} from "./artifact-capture.js"
import { extractSetVars } from "./set-vars.js"
import { captureOutputs } from "./output-capture.js"
import {
  buildCleanupWith,
  isAgentBackedTask,
  resolveMaxCleanupAttempts,
  type WorktreeSnapshot,
} from "./worktree-cleanup.js"

const DEFAULT_STALE_INDEX_LOCK_MS = 60_000

export type CleanupAgentAction = (context: ActionContext) => Promise<ActionResult>

type GitRunner = (workDir: string, args: string[], signal: AbortSignal) => Promise<{
  success: boolean
  stdout: string
  stderr: string
  exitCode: number
  combinedOutput: string
}>
type LockHolderProbe = (workDir: string, lockPath: string, signal: AbortSignal) => Promise<{ held: boolean; detail?: string }>

let cleanupAgentAction: CleanupAgentAction = acpAgentAction
let git: GitRunner = defaultGit
let lockHolderProbe: LockHolderProbe = defaultLockHolderProbe

export function setCleanupAgentActionForTest(handler: CleanupAgentAction | null) {
  cleanupAgentAction = handler ?? acpAgentAction
}

export function setExecutorGitRunnerForTest(runner: GitRunner | null) {
  git = runner ?? defaultGit
}

export function setExecutorLockHolderProbeForTest(probe: LockHolderProbe | null) {
  lockHolderProbe = probe ?? defaultLockHolderProbe
}

export interface DirtyWorktreeEvidence {
  kind: "dirty-worktree"
  staged: string[]
  unstaged: string[]
  untracked: string[]
  cleanupAttempts: number
}

export interface BranchStabilityEvidence {
  kind: "branch-stability"
  boundary: "start" | "end"
  expectedBranch: string
  observedBranch: string
  observedRef?: string | null
}

export interface BranchInvariantViolationEvidence {
  kind: "branch-invariant-violation"
  boundary: "start" | "end"
  expectedBranch: string
  observedBranch: string
  observedRef?: string | null
  detail?: string
}

export class WorkExecutor {
  constructor(
    private readonly actions: ActionRegistry,
    private readonly workspaceManager: WorkspaceManager,
    private readonly connection: ServerConnection,
    private readonly sessionManager: AcpSessionManager,
    private acpConnection: SharedAcpConnection | null,
    private readonly fallbackWorkDir = process.cwd(),
  ) {}

  updateAcpConnection(acp: SharedAcpConnection | null) {
    this.acpConnection = acp
  }

  async execute(work: RenderedWorkItem, signal: AbortSignal): Promise<WorkItemResult> {
    let resolvedWorkspace: ResolvedWorkspace
    if (work.ownerKind !== "agent-job") {
      const precheck = await this.prepareWorkspace(work, signal)
      if (precheck.kind === "failure") return precheck.result
      resolvedWorkspace = precheck.workspace
    } else {
      resolvedWorkspace = this.workspaceFromVariables(work)
    }

    if (work.workType === "checks") return await this.executeChecks(work, resolvedWorkspace, signal)
    return await this.executeOne(work, resolvedWorkspace, signal)
  }

  private async prepareWorkspace(work: RenderedWorkItem, signal: AbortSignal): Promise<{ kind: "ok", workspace: ResolvedWorkspace } | { kind: "failure", result: WorkItemResult }> {
    try {
      const info = await this.workspaceManager.prepare(work, signal)
      return { kind: "ok", workspace: infoToResolved(info) }
    } catch (error) {
      return { kind: "failure", result: workspaceSetupFailure(work, error) }
    }
  }

  private async executeOne(work: RenderedWorkItem, resolvedWorkspace: ResolvedWorkspace, signal: AbortSignal): Promise<WorkItemResult> {
    const action = this.actions.resolve(work.uses)
    if (!action) return failure(work, `No action found for '${work.uses}'`)

    try {
      const variables = await this.variables(work, resolvedWorkspace, signal)
      const unresolved = wholeStringUnresolvedReferences(work.with, variables)
      if (unresolved.length > 0) {
        return failure(work, formatUnresolvedError(work, unresolved))
      }
      const renderedWith = renderTemplate(work.with, variables)
      const workspaceRoot = this.workspaceRoot(variables)
      const workDir = await this.resolveWorkDir(renderedWith, workspaceRoot)
      const expectedBranch = expectedWorkspaceBranch(variables)
      const startCheck = await this.checkBranchStability(work, workDir, expectedBranch, "start", signal)
      if (startCheck.kind === "violation") {
        return startCheck.result
      }
      const result = await action({ ...baseContext(work, variables, signal, this.sessionManager, this.acpConnection, this.connection), with: renderedWith, workDir })
      const normalized = normalize(work, result)
      const recoveryResult = tryRecovery(work, normalized)
      if (recoveryResult) return recoveryResult
      if (normalized.status !== "completed") {
        return attachBranchStabilityEvidence(normalized, startCheck.evidence)
      }
      const endCheck = await this.checkBranchStability(work, workDir, expectedBranch, "end", signal)
      if (endCheck.kind === "violation") {
        return endCheck.result
      }
      const evidenceStack: BranchStabilityEvidence[] = [startCheck.evidence, endCheck.evidence]
      const worktreeResult = await this.enforceCleanWorktree(work, workDir, normalized, renderedWith, variables, signal)
      if (worktreeResult.status !== "completed") {
        return attachBranchStabilityEvidence(worktreeResult, evidenceStack)
      }
      const withEvidence = attachBranchStabilityEvidence(worktreeResult, evidenceStack)
      const finalResult = await this.captureAndUploadArtifacts(work, workspaceRoot, workDir, withEvidence, result, variables, signal)
      const withCapturedOutputs = this.captureDeclaredOutputs(work, finalResult, result)
      return await this.applySetVars(work, withCapturedOutputs, signal)
    } catch (error) {
      if (error instanceof WorktreeProbeError) {
        return worktreeProbeFailure(work, error)
      }
      return failure(work, errorMessage(error))
    }
  }

  /**
   * Task boundary invariant: the workflow workspace must remain on
   * `workspace.branch` for the entire lifetime of a task. The start
   * check runs before the action is invoked; the end check runs after
   * a successful action but before `enforceCleanWorktree` so a
   * wrong-branch state is reported as a branch-invariant violation
   * (runner/action bug) rather than as a generic dirty-worktree
   * failure. The two checks are intentionally not exhaustive: the
   * action itself may temporarily move refs, and that is the
   * integration's contract; we only assert the boundary.
   */
  private async checkBranchStability(
    work: RenderedWorkItem,
    workDir: string,
    expectedBranch: string | null,
    boundary: "start" | "end",
    signal: AbortSignal,
  ): Promise<
    | { kind: "ok"; evidence: BranchStabilityEvidence }
    | { kind: "violation"; result: WorkItemResult }
  > {
    const observed = await readCurrentBranch(workDir, signal)
    if (expectedBranch === null) {
      const evidence: BranchStabilityEvidence = {
        kind: "branch-stability",
        boundary,
        expectedBranch: "",
        observedBranch: observed.branch ?? "",
        observedRef: observed.ref,
      }
      return { kind: "ok", evidence }
    }
    // A non-git worktree has no branch context to check, matching
    // the clean-worktree probe's "treat as satisfied" semantics for
    // plain tmpdirs and test fixtures. The evidence records the
    // empty observed branch so downstream consumers can tell the
    // boundary was trivially satisfied. A detached HEAD at a real
    // git worktree, by contrast, IS a violation: the run branch is
    // always a real branch ref, so a detached HEAD must not be
    // silently tolerated.
    if (observed.nonGit) {
      const evidence: BranchStabilityEvidence = {
        kind: "branch-stability",
        boundary,
        expectedBranch,
        observedBranch: "",
        observedRef: null,
      }
      return { kind: "ok", evidence }
    }
    const evidence: BranchStabilityEvidence = {
      kind: "branch-stability",
      boundary,
      expectedBranch,
      observedBranch: observed.branch ?? "",
      observedRef: observed.ref,
    }
    if (observed.error) {
      return {
        kind: "violation",
        result: branchInvariantViolationFailure(work, {
          kind: "branch-invariant-violation",
          boundary,
          expectedBranch,
          observedBranch: observed.branch ?? "",
          observedRef: observed.ref,
          detail: `git rev-parse --abbrev-ref HEAD probe failed: ${observed.error}`,
        }),
      }
    }
    if (observed.detached) {
      return {
        kind: "violation",
        result: branchInvariantViolationFailure(work, {
          kind: "branch-invariant-violation",
          boundary,
          expectedBranch,
          observedBranch: "",
          observedRef: observed.ref,
        }),
      }
    }
    if (observed.branch !== expectedBranch) {
      return {
        kind: "violation",
        result: branchInvariantViolationFailure(work, {
          kind: "branch-invariant-violation",
          boundary,
          expectedBranch,
          observedBranch: observed.branch ?? "",
          observedRef: observed.ref,
        }),
      }
    }
    return { kind: "ok", evidence }
  }

  /**
   * Task completion invariant: the worktree must be clean before
   * `executeOne` reports completion. For agent-backed tasks the
   * executor runs a bounded cleanup loop that sends a constrained
   * follow-up prompt to the same agent session, instructing the
   * agent to commit task-related changes or revert unrelated ones.
   * Deterministic actions that leave a dirty worktree fail
   * immediately with structured evidence.
   */
  private async enforceCleanWorktree(
    work: RenderedWorkItem,
    workDir: string,
    result: WorkItemResult,
    renderedWith: JsonObject | null,
    variables: JsonObject,
    signal: AbortSignal,
  ): Promise<WorkItemResult> {
    const agentBacked = isAgentBackedTask(work)
    const maxCleanupAttempts = resolveMaxCleanupAttempts(variables)

    let attempts = 0
    let snapshot = await readWorktreeSnapshot(workDir, signal)
    while (!snapshot.isClean) {
      if (!agentBacked) {
        return dirtyWorktreeFailure(result, snapshot, attempts)
      }
      if (attempts >= maxCleanupAttempts) {
        return dirtyWorktreeFailure(result, snapshot, attempts)
      }
      const lockRecovery = await recoverStaleIndexLock(workDir, variables, signal)
      if (lockRecovery.status === "blocked") {
        return gitIndexLockFailure(result, snapshot, attempts, lockRecovery)
      }
      attempts += 1
      const cleanupResult = await this.runAgentCleanupAttempt(work, workDir, renderedWith, variables, snapshot, attempts, signal)
      if (cleanupResult !== "ok") {
        return cleanupResult
      }
      snapshot = await readWorktreeSnapshot(workDir, signal)
    }

    if (attempts === 0) return result
    return mergeCleanupCount(result, attempts)
  }

  private async runAgentCleanupAttempt(
    work: RenderedWorkItem,
    workDir: string,
    renderedWith: JsonObject | null,
    variables: JsonObject,
    snapshot: WorktreeSnapshot,
    attempt: number,
    signal: AbortSignal,
  ): Promise<WorkItemResult | "ok"> {
    const cleanupContext: ActionContext = {
      ...baseContext(work, variables, signal, this.sessionManager, this.acpConnection, this.connection),
      workDir,
      workType: "task",
      with: buildCleanupWith(work, renderedWith, snapshot, attempt),
    }

    let result: ActionResult
    try {
      result = await cleanupAgentAction(cleanupContext)
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error)
      return dirtyWorktreeFailure(mergeCleanupCount({ status: "completed" }, attempt - 1), snapshot, attempt, `Cleanup attempt ${attempt} threw: ${message}`)
    }
    if (result.status !== "success" && result.status !== "completed") {
      return dirtyWorktreeFailure(mergeCleanupCount({ status: "completed" }, attempt - 1), snapshot, attempt, `Cleanup attempt ${attempt} failed: ${result.message ?? result.status}`)
    }
    return "ok"
  }

  private async executeChecks(work: RenderedWorkItem, resolvedWorkspace: ResolvedWorkspace, signal: AbortSignal): Promise<WorkItemResult> {
    const variables = await this.variables(work, resolvedWorkspace, signal)
    const checks = Array.isArray(work.with?.checks) ? work.with.checks.filter(isCheck) : []
    if (checks.length === 0) return failure(work, "No checks found in dispatch")

    const results = await Promise.all(checks.map(async (check) => {
      const action = this.actions.resolve(check.uses)
      if (!action) return { name: check.name, status: "fail", message: `No action found for '${check.uses}'` }
      try {
        const unresolved = wholeStringUnresolvedReferences(check.with ?? null, variables)
        if (unresolved.length > 0) {
          return { name: check.name, status: "fail", message: formatCheckUnresolvedError(unresolved) }
        }
        const renderedWith = renderTemplate(check.with ?? null, variables)
        const workDir = await this.resolveWorkDir(renderedWith, this.workspaceRoot(variables))
        const result = await action({ ...baseContext(work, variables, signal, this.sessionManager, this.acpConnection, this.connection), workType: "check", title: check.title, uses: check.uses, with: renderedWith, workDir })
        return { name: check.name, status: toCheckStatus(result.status), message: result.message, output: result.output }
      } catch (error) {
        return { name: check.name, status: "fail", message: error instanceof Error ? error.message : String(error) }
      }
    }))

    const verdict = results.every((result) => result.status === "pass") ? "pass" : "fail"
    const output = JSON.stringify(results)
    if (verdict === "fail") {
      const failedChecks = results.filter((r) => r.status === "fail")
      const checkDetails = failedChecks.map((c) => {
        const isMarkerCheck = checks.find((ch) => ch.name === c.name)?.uses === "core/marker"
        if (isMarkerCheck) {
          const checkConfig = checks.find((ch) => ch.name === c.name)
          const expectedMarker = checkConfig?.with?.expect ?? checkConfig?.with?.contains ?? "PASS"
          return `${c.name}: expected verdict marker '${expectedMarker}' but it was not found in the artifact`
        }
        return `${c.name}: ${c.message}`
      }).join("; ")
      return { status: "fail", message: `Check verdict failure: ${checkDetails}`, output }
    }
    return { status: "pass", output }
  }

  private async variables(work: RenderedWorkItem, resolvedWorkspace: ResolvedWorkspace, signal: AbortSignal): Promise<JsonObject> {
    const workspace = resolvedWorkspaceToVariables(resolvedWorkspace)
    const userVariables = work.variables ?? {}
    const userRunner = userVariables.runner
    const mergedRunner: JsonObject = { ...runnerVariables() }
    if (userRunner && typeof userRunner === "object" && !Array.isArray(userRunner)) {
      Object.assign(mergedRunner, userRunner as JsonObject)
    }
    return { ...userVariables, runner: mergedRunner, workspace }
  }

  // Read the workspace triple (path, branch, changeDir) directly from
  // the dispatch's variables. Used by agent-job dispatches whose
  // workspace is caller-owned and must NOT be re-cloned or verified by
  // the runner (issue #126 standalone-workspace contract).
  private workspaceFromVariables(work: RenderedWorkItem): ResolvedWorkspace {
    const variables = work.variables ?? {}
    const ws = variables["workspace"]
    if (!ws || typeof ws !== "object" || Array.isArray(ws)) {
      return { path: "", branch: null, changeDir: null }
    }
    const obj = ws as JsonObject
    return {
      path: typeof obj["path"] === "string" ? (obj["path"] as string) : "",
      branch: typeof obj["branch"] === "string" ? (obj["branch"] as string) : null,
      changeDir: typeof obj["changeDir"] === "string" ? (obj["changeDir"] as string) : null,
    }
  }

  private workspaceRoot(variables: JsonObject) {
    return stringAt(variables, ["workspace", "path"]) ?? join(this.fallbackWorkDir, "default")
  }

  private async resolveWorkDir(withInput: JsonObject | null, workspaceRoot: string) {
    const requested = stringInput(withInput, "working-directory")
    const root = resolve(workspaceRoot)
    const workDir = requested ? resolveWorkspacePath(root, requested) : root
    await ensureDir(workDir)
    return workDir
  }

  /**
   * Capture the task's declared `artifacts.files` plus any
   * action-produced dynamic artifacts from the runner workspace, upload
   * each to the server, and attach the resulting upload ids to the task
   * result. A failure to capture or upload any declared artifact fails
   * the task through the normal task failure path; dynamic artifact
   * failures are reported on the message but do not fail the task.
   */
  private async captureAndUploadArtifacts(
    work: RenderedWorkItem,
    workspaceRoot: string,
    workDir: string,
    result: WorkItemResult,
    actionResult: import("../core/types.js").ActionResult,
    variables: JsonObject,
    signal: AbortSignal,
  ): Promise<WorkItemResult> {
    // Render the declared artifacts object so template variables
    // (e.g. `${{ openspecChangeDir }}/review.md` from the default
    // workflow) resolve to workspace-relative paths before the
    // capture layer hands them to the filesystem. Without this
    // substitution the runner would read from a literal
    // `${{ openspecChangeDir }}` directory and fail every declared
    // artifact capture with ENOENT.
    //
    // Artifact `path` strings must resolve every embedded reference;
    // unlike `with.prompt` they are real workspace paths, not
    // documentation, so an embedded `${{ unknown }}` left in place
    // is a bug rather than a tolerated literal. We use
    // `unresolvedReferences` (which catches both whole-string and
    // embedded) to surface the failure before the capture layer
    // would otherwise encounter an ENOENT.
    let renderedArtifacts: JsonObject | null = null
    if (work.artifacts) {
      try {
        const unresolved = unresolvedReferences(work.artifacts, variables)
        if (unresolved.length > 0) {
          return {
            ...result,
            status: "failed",
            message: `${result.message ? result.message + "; " : ""}artifact declaration references undefined variable(s): ${unresolved.map((p) => "'${{ " + p + " }}'").join(", ")}. Add the variable to workflow.variables or a parent stage.`.slice(0, 4000),
          }
        }
        renderedArtifacts = renderTemplate(work.artifacts, variables) as JsonObject | null
      } catch (error) {
        return {
          ...result,
          status: "failed",
          message: `${result.message ? result.message + "; " : ""}artifact template render failed: ${error instanceof Error ? error.message : String(error)}`.slice(0, 4000),
        }
      }
    }

    const dynamicInputs = actionProducedArtifacts(actionResult)
    let captureOutcome
    try {
      const declaredOutcome = await captureArtifacts({ work, workDir: workspaceRoot, renderedArtifacts })
      const dynamicOutcome = dynamicInputs.length === 0
        ? { captures: [], failures: [] }
        : await captureArtifacts({ work: { ...work, artifacts: null }, workDir, dynamicArtifacts: dynamicInputs })
      captureOutcome = {
        captures: [...declaredOutcome.captures, ...dynamicOutcome.captures],
        failures: [...declaredOutcome.failures, ...dynamicOutcome.failures],
      }
    } catch (error) {
      return {
        ...result,
        status: "failed",
        message: `${result.message ? result.message + "; " : ""}artifact capture failed: ${error instanceof Error ? error.message : String(error)}`.slice(0, 4000),
      }
    }
    if (captureOutcome.captures.length === 0) {
      const captureWarnings = captureOutcome.failures.length > 0
        ? `${result.message ? result.message + "; " : ""}artifact capture warnings: ${summarizeCaptureFailures(captureOutcome.failures)}`.slice(0, 4000)
        : result.message
      return { ...result, message: captureWarnings }
    }
    let uploads
    try {
      const ownerKind = work.ownerKind === "agent-job" ? "agent-job" : "workflow"
      const ownerId = ownerKind === "agent-job" ? work.agentJobId : work.workflowRunId
      if (!ownerId) {
        return {
          ...result,
          status: "failed",
          message: `${result.message ? result.message + "; " : ""}artifact upload failed: missing ${ownerKind === "agent-job" ? "agentJobId" : "workflowRunId"}`.slice(0, 4000),
        }
      }
      uploads = await uploadCapturedArtifacts(this.connection, ownerId, work.workId, captureOutcome.captures, signal, ownerKind)
    } catch (error) {
      return {
        ...result,
        status: "failed",
        message: `${result.message ? result.message + "; " : ""}artifact upload failed: ${error instanceof Error ? error.message : String(error)}`.slice(0, 4000),
      }
    }
    const allFailures = [...captureOutcome.failures, ...uploads.failures]
    const message = allFailures.length > 0
      ? `${result.message ? result.message + "; " : ""}artifact warnings: ${summarizeCaptureFailures(allFailures)}`.slice(0, 4000)
      : result.message
    return {
      ...result,
      message,
      artifactUploadIds: uploads.uploads.map((upload) => upload.uploadId),
    }
  }

  private async applySetVars(work: RenderedWorkItem, result: WorkItemResult, signal: AbortSignal): Promise<WorkItemResult> {
    if (result.status !== "completed") return result
    if (!work.setVars || Object.keys(work.setVars).length === 0) return result

    const extraction = extractSetVars(work.setVars, result.output)
    if (extraction.error) {
      return { ...result, status: "failed", message: `setVars: ${extraction.error}` }
    }
    if (extraction.vars) {
      try {
        await this.connection.patchRunVars(work.workflowRunId, extraction.vars, signal)
      } catch (error) {
        return {
          ...result,
          status: "failed",
          message: `setVars patch failed: ${error instanceof Error ? error.message : String(error)}`.slice(0, 4000),
        }
      }
    }
    return result
  }

  private captureDeclaredOutputs(work: RenderedWorkItem, result: WorkItemResult, actionResult: ActionResult): WorkItemResult {
    if (result.status !== "completed") return result
    const capturedOutputs = captureOutputs(work.outputs, actionResult)
    return capturedOutputs ? { ...result, capturedOutputs } : result
  }
}

type ResolvedWorkspace = { path: string, branch: string | null, changeDir: string | null }

function infoToResolved(info: { path: string, branch?: string | null, changeDir?: string | null }): ResolvedWorkspace {
  return { path: info.path, branch: info.branch ?? null, changeDir: info.changeDir ?? null }
}

function resolvedWorkspaceToVariables(workspace: ResolvedWorkspace): JsonObject {
  return { path: workspace.path, branch: workspace.branch, changeDir: workspace.changeDir }
}

function baseContext(work: RenderedWorkItem, variables: JsonObject, signal: AbortSignal, sessionManager: AcpSessionManager, acpConnection: SharedAcpConnection | null, connection: ServerConnection): Omit<ActionContext, "with" | "workDir"> {
  return { workflowRunId: work.workflowRunId, workId: work.workId, workType: work.workType, stage: work.stage, title: work.title, uses: work.uses, variables, signal, projectId: work.projectId, issueNumber: work.issueNumber, acpSessionManager: sessionManager, acpConnection, serverConnection: connection }
}

// Build a failure result for when the runner could not prepare the
// workflow workspace (clone failed, base branch missing, checkout could
// not be restored). The `kind` is the structured `output.kind` so the
// CLI / API / UI can render it distinctly from ordinary task failures.
function workspaceSetupFailure(work: RenderedWorkItem, error: unknown): WorkItemResult {
  const message = error instanceof Error ? error.message : String(error)
  return {
    status: work.workType === "check" || work.workType === "checks" ? "fail" : "failed",
    message: `could not prepare workflow workspace (workspace-setup): ${message}`.slice(0, 4000),
    output: JSON.stringify({ kind: "workspace-setup" }),
  }
}

function normalize(work: RenderedWorkItem, result: WorkItemResult): WorkItemResult {
  const status = result.status.toLowerCase()
  if (work.workType === "check") {
    if (["pass", "passed", "success", "succeeded", "completed"].includes(status)) return { ...result, status: "pass" }
    if (status === "pending") return { ...result, status: "pending" }
    return { ...result, status: "fail" }
  }
  if (["completed", "success", "succeeded", "pass", "passed"].includes(status)) return { ...result, status: "completed" }
  return { ...result, status: "failed" }
}

function failure(work: RenderedWorkItem, message: string): WorkItemResult {
  return { status: work.workType === "check" || work.workType === "checks" ? "fail" : "failed", message }
}

function toCheckStatus(status: string) {
  const normalized = status.toLowerCase()
  if (["pass", "passed", "success", "succeeded", "completed"].includes(normalized)) return "pass"
  if (normalized === "pending") return "pending"
  return "fail"
}

function isCheck(value: unknown): value is { name?: string; title?: string; uses: string; with?: JsonObject | null } {
  return typeof value === "object" && value !== null && "uses" in value && typeof (value as { uses?: unknown }).uses === "string"
}

function resolveWorkspacePath(workspaceRoot: string, requested: string) {
  const resolved = isAbsolute(requested) ? resolve(requested) : resolve(workspaceRoot, requested)
  const rel = relative(workspaceRoot, resolved)
  if (rel.startsWith("..") || isAbsolute(rel)) {
    throw new Error(`working-directory '${requested}' escapes workspace.path`)
  }
  return resolved
}

function formatUnresolvedError(work: RenderedWorkItem, unresolved: string[]): string {
  const label = work.title?.trim() || work.uses || work.workId
  const refs = unresolved.map((p) => "'${{ " + p + " }}'").join(", ")
  return "Task " + work.workId + " (" + label + ") references undefined variable(s): " + refs + ". " +
    "Add the variable to workflow.variables, define it in a parent stage, or escape the literal with \\${{ ... }}."
}

function formatCheckUnresolvedError(unresolved: string[]): string {
  const refs = unresolved.map((p) => "'${{ " + p + " }}'").join(", ")
  return "check references undefined variable(s): " + refs + ". " +
    "Add the variable to workflow.variables, define it in a parent stage, or escape the literal with \\${{ ... }}."
}

function resolveStaleIndexLockMs(variables: JsonObject): number {
  const candidate = variables["runner"]
  if (candidate && typeof candidate === "object" && !Array.isArray(candidate)) {
    const cleanup = (candidate as JsonObject)["cleanup"]
    if (cleanup && typeof cleanup === "object" && !Array.isArray(cleanup)) {
      const value = (cleanup as JsonObject)["staleIndexLockMs"]
      if (typeof value === "number" && Number.isFinite(value) && value >= 0) return Math.floor(value)
      if (typeof value === "string") {
        const parsed = Number(value)
        if (Number.isFinite(parsed) && parsed >= 0) return Math.floor(parsed)
      }
    }
  }
  return DEFAULT_STALE_INDEX_LOCK_MS
}

type GitIndexLockRecovery =
  | { status: "ok"; cleared?: boolean; lockPath?: string; ageMs?: number }
  | { status: "blocked"; reason: string; lockPath?: string; ageMs?: number }

async function recoverStaleIndexLock(workDir: string, variables: JsonObject, signal: AbortSignal): Promise<GitIndexLockRecovery> {
  const lockPathResult = await git(workDir, ["rev-parse", "--git-path", "index.lock"], signal)
  if (!lockPathResult.success) {
    return {
      status: "blocked",
      reason: `git index lock path probe failed: ${lockPathResult.combinedOutput || `exit ${lockPathResult.exitCode}`}`,
    }
  }

  const rawLockPath = lockPathResult.stdout.trim()
  if (!rawLockPath) {
    return { status: "blocked", reason: "git index lock path probe returned an empty path" }
  }

  const lockPath = isAbsolute(rawLockPath) ? rawLockPath : resolve(workDir, rawLockPath)
  let info: Awaited<ReturnType<typeof stat>>
  try {
    info = await stat(lockPath)
  } catch (error) {
    if (isNotFoundError(error)) return { status: "ok", lockPath }
    return { status: "blocked", reason: `git index lock stat failed: ${errorMessage(error)}`, lockPath }
  }

  const ageMs = Math.max(0, Date.now() - info.mtimeMs)
  const staleMs = resolveStaleIndexLockMs(variables)
  if (ageMs < staleMs) {
    return {
      status: "blocked",
      reason: `git index lock is fresh (${Math.floor(ageMs)}ms old, stale threshold ${staleMs}ms)`,
      lockPath,
      ageMs,
    }
  }

  const holder = await lockHolderProbe(workDir, lockPath, signal)
  if (holder.held) {
    return {
      status: "blocked",
      reason: `git index lock is still held${holder.detail ? `: ${holder.detail}` : ""}`,
      lockPath,
      ageMs,
    }
  }

  try {
    await rm(lockPath, { force: true })
  } catch (error) {
    return { status: "blocked", reason: `failed to remove stale git index lock: ${errorMessage(error)}`, lockPath, ageMs }
  }

  return { status: "ok", cleared: true, lockPath, ageMs }
}

async function defaultLockHolderProbe(workDir: string, lockPath: string, signal: AbortSignal): Promise<{ held: boolean; detail?: string }> {
  try {
    const result = await runCommand("lsof", [lockPath], workDir, signal)
    if (result.exitCode === 0) {
      return { held: true, detail: result.stdout.trim().split(/\r?\n/).slice(0, 3).join("; ") }
    }
  } catch (error) {
    // lsof is best-effort: if it is not installed or cannot run in the
    // host environment, the age threshold still prevents deleting fresh
    // locks while allowing stale lock recovery to make progress.
    return { held: false, detail: errorMessage(error) }
  }
  return { held: false }
}

async function readWorktreeSnapshot(workDir: string, signal: AbortSignal): Promise<WorktreeSnapshot> {
  const inside = await git(workDir, ["rev-parse", "--is-inside-work-tree"], signal)
  if (!inside.success) {
    // Only treat the worktree as "not a git repo" (and therefore as
    // clean-by-default) when the failure is Git's standard "not a git
    // repository" message. Other failures (missing git binary, permission
    // errors, corrupted worktree) must surface to the caller so the task
    // fails with structured evidence rather than silently succeeding on
    // a never-evaluated invariant.
    const lowerStderr = (inside.stderr ?? "").toLowerCase()
    const isPlainDir = lowerStderr.includes("not a git repository")
    if (!isPlainDir) {
      throw new WorktreeProbeError(
        `git worktree probe failed: ${inside.combinedOutput || `exit ${inside.exitCode}`}`,
        inside.exitCode,
      )
    }
    return { staged: [], unstaged: [], untracked: [], isClean: true }
  }
  const staged = await git(workDir, ["diff", "--cached", "--name-only"], signal)
  const unstaged = await git(workDir, ["diff", "--name-only"], signal)
  const untracked = await git(workDir, ["ls-files", "--others", "--exclude-standard"], signal)

  if (!staged.success || !unstaged.success || !untracked.success) {
    throw new WorktreeProbeError(
      `git worktree status check failed: ` +
      `staged(exit=${staged.exitCode}), ` +
      `unstaged(exit=${unstaged.exitCode}), ` +
      `untracked(exit=${untracked.exitCode})`,
      null,
    )
  }

  const stagedList = parseFileList(staged.stdout)
  const unstagedList = parseFileList(unstaged.stdout)
  const untrackedList = parseFileList(untracked.stdout)
  return {
    staged: stagedList,
    unstaged: unstagedList,
    untracked: untrackedList,
    isClean: stagedList.length === 0 && unstagedList.length === 0 && untrackedList.length === 0,
  }
}

class WorktreeProbeError extends Error {
  constructor(message: string, public readonly exitCode: number | null) {
    super(message)
    this.name = "WorktreeProbeError"
  }
}

function parseFileList(stdout: string): string[] {
  return [...new Set(stdout.split(/\r?\n/).map((line) => line.trim()).filter(Boolean))]
}

function dirtyWorktreeFailure(
  result: WorkItemResult,
  snapshot: WorktreeSnapshot,
  cleanupAttempts: number,
  detail?: string,
): WorkItemResult {
  const evidence: DirtyWorktreeEvidence = {
    kind: "dirty-worktree",
    staged: [...snapshot.staged],
    unstaged: [...snapshot.unstaged],
    untracked: [...snapshot.untracked],
    cleanupAttempts,
  }
  const baseMessage = result.message?.trim() || "Task completed by action but worktree remained dirty"
  const summary = formatDirtyWorktreeSummary(evidence)
  const message = detail
    ? `${baseMessage}; ${detail}; ${summary}`.slice(0, 4000)
    : `${baseMessage}; ${summary}`.slice(0, 4000)
  const existingOutput = result.output ? safeParseJson(result.output) : null
  const output = JSON.stringify({
    ...(existingOutput ?? {}),
    kind: "dirty-worktree",
    staged: evidence.staged,
    unstaged: evidence.unstaged,
    untracked: evidence.untracked,
    cleanupAttempts: evidence.cleanupAttempts,
  })
  return {
    ...result,
    status: "failed",
    message,
    output,
    cleanupAttempts,
  }
}

function gitIndexLockFailure(
  result: WorkItemResult,
  snapshot: WorktreeSnapshot,
  cleanupAttempts: number,
  recovery: Extract<GitIndexLockRecovery, { status: "blocked" }>,
): WorkItemResult {
  const existingOutput = result.output ? safeParseJson(result.output) : null
  const message = `${result.message?.trim() || "Task completed by action but Git index is locked"}; git index lock blocked cleanup: ${recovery.reason}`.slice(0, 4000)
  return {
    ...result,
    status: "failed",
    message,
    output: JSON.stringify({
      ...(existingOutput ?? {}),
      kind: "git-index-lock",
      lockPath: recovery.lockPath,
      lockAgeMs: recovery.ageMs,
      reason: recovery.reason,
      staged: snapshot.staged,
      unstaged: snapshot.unstaged,
      untracked: snapshot.untracked,
      cleanupAttempts,
    }),
    cleanupAttempts,
  }
}

function formatDirtyWorktreeSummary(evidence: DirtyWorktreeEvidence): string {
  const parts: string[] = []
  parts.push(`worktree dirty after ${evidence.cleanupAttempts} cleanup attempt(s)`)
  parts.push(`staged=[${evidence.staged.join(", ")}]`)
  parts.push(`unstaged=[${evidence.unstaged.join(", ")}]`)
  parts.push(`untracked=[${evidence.untracked.join(", ")}]`)
  return parts.join("; ")
}

function worktreeProbeFailure(work: RenderedWorkItem, error: WorktreeProbeError): WorkItemResult {
  const evidence: DirtyWorktreeEvidence = {
    kind: "dirty-worktree",
    staged: [],
    unstaged: [],
    untracked: [],
    cleanupAttempts: 0,
    ...({
      probeError: error.message,
      probeExitCode: error.exitCode,
    } as unknown as Pick<DirtyWorktreeEvidence, never>),
  } as DirtyWorktreeEvidence
  const label = work.title?.trim() || work.uses || work.workId
  const message = `git worktree probe failed for ${label}: ${error.message}`.slice(0, 4000)
  return {
    status: "failed",
    message,
    output: JSON.stringify(evidence),
    cleanupAttempts: 0,
  }
}

function safeParseJson(value: string): JsonObject | null {
  try {
    const parsed = JSON.parse(value) as unknown
    return parsed && typeof parsed === "object" && !Array.isArray(parsed) ? (parsed as JsonObject) : null
  } catch {
    return null
  }
}

function mergeCleanupCount(result: WorkItemResult, attempts: number): WorkItemResult {
  return { ...result, cleanupAttempts: attempts }
}

function errorMessage(error: unknown): string {
  if (error instanceof Error) return error.message
  if (error && typeof error === "object" && "name" in error && "message" in error) {
    return String((error as { message: unknown }).message)
  }
  return String(error)
}

function isNotFoundError(error: unknown): boolean {
  return Boolean(error && typeof error === "object" && "code" in error && (error as { code?: unknown }).code === "ENOENT")
}

function expectedWorkspaceBranch(variables: JsonObject): string | null {
  const workspace = variables["workspace"]
  if (!workspace || typeof workspace !== "object" || Array.isArray(workspace)) return null
  const branch = (workspace as JsonObject)["branch"]
  return typeof branch === "string" && branch.length > 0 ? branch : null
}

interface CurrentBranchResult {
  branch: string | null
  ref: string | null
  detached: boolean
  nonGit: boolean
  error: string | null
}

async function readCurrentBranch(workDir: string, signal: AbortSignal): Promise<CurrentBranchResult> {
  const probe = await git(workDir, ["rev-parse", "--abbrev-ref", "HEAD"], signal)
  if (!probe.success) {
    const stderr = (probe.stderr ?? "").toLowerCase()
    // A plain (non-git) worktree is the same edge case as the clean
    // worktree probe: there is no branch context, so the branch check
    // is satisfied trivially rather than reported as a failure.
    if (stderr.includes("not a git repository")) {
      return { branch: null, ref: null, detached: false, nonGit: true, error: null }
    }
    return { branch: null, ref: null, detached: false, nonGit: false, error: probe.combinedOutput || `exit ${probe.exitCode}` }
  }
  const branch = probe.stdout.trim()
  // `git rev-parse --abbrev-ref HEAD` returns "HEAD" for a detached
  // HEAD. The run branch is always a real branch ref, so a detached
  // HEAD is itself a boundary violation; surface it as a null branch
  // (the caller compares to the expected branch and reports the
  // violation) but record the ref for evidence.
  if (branch === "HEAD") {
    const refProbe = await git(workDir, ["rev-parse", "HEAD"], signal)
    return { branch: null, ref: refProbe.success ? refProbe.stdout.trim() : null, detached: true, nonGit: false, error: null }
  }
  return { branch, ref: branch, detached: false, nonGit: false, error: null }
}

function branchInvariantViolationFailure(
  work: RenderedWorkItem,
  evidence: BranchInvariantViolationEvidence,
): WorkItemResult {
  const label = work.title?.trim() || work.uses || work.workId
  const observed = evidence.observedBranch || `(detached at ${evidence.observedRef ?? "unknown"})`
  const detail = evidence.detail ? `; ${evidence.detail}` : ""
  const message = `branch-invariant violation at ${evidence.boundary} boundary for ${label}: ` +
    `expected branch '${evidence.expectedBranch}', observed '${observed}'${detail}`.slice(0, 4000)
  return {
    status: "failed",
    message,
    output: JSON.stringify(evidence),
  }
}

function attachBranchStabilityEvidence(
  result: WorkItemResult,
  evidence: BranchStabilityEvidence | BranchStabilityEvidence[],
): WorkItemResult {
  const stack = Array.isArray(evidence) ? evidence : [evidence]
  if (stack.length === 0) return result
  const existingOutput = result.output ? safeParseJson(result.output) : null
  const evidenceList = Array.isArray((existingOutput ?? {})["branchStability"])
    ? ((existingOutput as JsonObject)["branchStability"] as JsonValue[])
    : []
  const merged: JsonObject = {
    ...(existingOutput ?? {}),
    branchStability: [...evidenceList, ...stack.map(branchStabilityToJson)],
  }
  return { ...result, output: JSON.stringify(merged) }
}

function branchStabilityToJson(evidence: BranchStabilityEvidence): JsonObject {
  const value: JsonObject = {
    kind: evidence.kind,
    boundary: evidence.boundary,
    expectedBranch: evidence.expectedBranch,
    observedBranch: evidence.observedBranch,
  }
  if (evidence.observedRef !== undefined) value["observedRef"] = evidence.observedRef
  return value
}

// ---------------------------------------------------------------------------
// Task recovery — runner-side matching of action output against task-level
// `recovery` config. When a handler matches and budget remains, the failure
// is converted to `completed` + `addTasks` for the server to insert.
// -----------------------------------------------------------------------

interface RecoveryHandler {
  when: string
  tasks: AddTaskInput[]
  retrySelf: boolean
}

interface RecoveryConfig {
  budget: number
  handlers: RecoveryHandler[]
}

function tryRecovery(
  work: RenderedWorkItem,
  result: WorkItemResult,
): WorkItemResult | null {
  const recovery = readRecoveryConfig(work.recovery)
  if (!recovery) return null

  const output = safeParseJson(result.output ?? "")
  if (!output) return null

  const handler = recovery.handlers.find((h) => matchesWhen(h.when, output))
  if (!handler) return null

  if (recovery.budget <= 0) return null

  const addTasks: AddTaskInput[] = [...handler.tasks]

  if (handler.retrySelf) {
    const retryId = work.workId.includes(".")
      ? work.workId.substring(0, work.workId.lastIndexOf("."))
      : work.workId
    const nextRecovery = decrementRecoveryBudget(work.recovery, recovery.budget)
    addTasks.push({
      id: retryId,
      title: work.title ?? work.workId,
      uses: work.uses ?? null,
      with: work.with,
      recovery: nextRecovery,
    })
  }

  const label = work.title?.trim() || work.uses || work.workId
  return {
    status: "completed",
    message: `${label} failed (${handler.when}); recovery scheduled`,
    output: result.output,
    addTasks,
  }
}

function matchesWhen(when: string, output: JsonObject): boolean {
  const eq = when.indexOf("=")
  if (eq === -1) return false
  const field = when.slice(0, eq).trim()
  const expected = when.slice(eq + 1).trim()
  return String(output[field]) === expected
}

function readRecoveryConfig(recovery: JsonObject | null | undefined): RecoveryConfig | null {
  if (!recovery) return null
  const rawBudget = recovery["budget"]
  const budget = typeof rawBudget === "number" && Number.isFinite(rawBudget) ? Math.floor(rawBudget) : 0
  const rawHandlers = recovery["handlers"]
  if (!Array.isArray(rawHandlers)) return null
  const handlers: RecoveryHandler[] = []
  for (const raw of rawHandlers) {
    if (!raw || typeof raw !== "object" || Array.isArray(raw)) continue
    const h = raw as JsonObject
    const when = typeof h["when"] === "string" ? h["when"] : null
    if (!when) continue
    handlers.push({
      when,
      tasks: readAddTasks(h["tasks"]),
      retrySelf: h["retrySelf"] === true,
    })
  }
  return { budget, handlers }
}

function readAddTasks(raw: unknown): AddTaskInput[] {
  if (!Array.isArray(raw)) return []
  const tasks: AddTaskInput[] = []
  for (const entry of raw) {
    if (!entry || typeof entry !== "object" || Array.isArray(entry)) continue
    const t = entry as JsonObject
    const id = typeof t["id"] === "string" ? t["id"] : null
    if (!id) continue
    const withValue = t["with"]
    tasks.push({
      id,
      title: typeof t["title"] === "string" ? t["title"] : id,
      uses: typeof t["uses"] === "string" ? t["uses"] : null,
      with: withValue && typeof withValue === "object" && !Array.isArray(withValue) ? (withValue as JsonObject) : null,
    })
  }
  return tasks
}

function decrementRecoveryBudget(recovery: JsonObject | null | undefined, currentBudget: number): JsonObject | null {
  if (!recovery) return null
  return {
    ...recovery,
    budget: currentBudget - 1,
  }
}
