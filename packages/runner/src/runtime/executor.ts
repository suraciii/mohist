import { rm, stat } from "node:fs/promises"
import { isAbsolute, join, relative, resolve } from "node:path"
import type { ActionContext, ActionResult, JsonObject, WorkItem, WorkItemResult } from "../core/types.js"
import { stringInput } from "../core/json.js"
import { renderTemplate, unresolvedReferences, wholeStringUnresolvedReferences } from "../core/template.js"
import { ensureDir, runCommand } from "../system/process.js"
import { runnerVariables, WorkspaceManager } from "./workspace.js"
import type { ActionRegistry } from "../actions/registry.js"
import { acpAgentAction } from "../actions/acp-agent.js"
import { git as defaultGit } from "../actions/git.js"
import type { ServerConnection } from "../server/connection.js"
import type { AcpSessionManager, SharedAcpConnection } from "./acp-connection.js"
import { captureOutputs } from "./output-capture.js"
import {
  actionProducedArtifacts,
  captureArtifacts,
  captureRequiresFailures,
  summarizeCaptureFailures,
  uploadCapturedArtifacts,
} from "./artifact-capture.js"

export const AGENT_BACKED_USES = "mohist/acp-agent"
const DEFAULT_MAX_CLEANUP_ATTEMPTS = 3
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

export function isAgentBackedTask(work: WorkItem): boolean {
  return typeof work.uses === "string" && work.uses.trim().toLowerCase() === AGENT_BACKED_USES
}

export interface DirtyWorktreeEvidence {
  kind: "dirty-worktree"
  staged: string[]
  unstaged: string[]
  untracked: string[]
  cleanupAttempts: number
}

export interface WorktreeSnapshot {
  staged: string[]
  unstaged: string[]
  untracked: string[]
  isClean: boolean
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

  async execute(work: WorkItem, signal: AbortSignal): Promise<WorkItemResult> {
    if (work.workType === "checks") return await this.executeChecks(work, signal)
    return await this.executeOne(work, signal)
  }

  private async executeOne(work: WorkItem, signal: AbortSignal): Promise<WorkItemResult> {
    const action = this.actions.resolve(work.uses)
    if (!action) return failure(work, `No action found for '${work.uses}'`)

    try {
      const variables = await this.variables(work, signal)
      const unresolved = wholeStringUnresolvedReferences(work.with, variables)
      if (unresolved.length > 0) {
        return failure(work, formatUnresolvedError(work, unresolved))
      }
      const renderedWith = renderTemplate(work.with, variables)
      const workspaceRoot = this.workspaceRoot(variables)
      const workDir = await this.resolveWorkDir(renderedWith, workspaceRoot)
      const result = await action({ ...baseContext(work, variables, signal, this.sessionManager, this.acpConnection, this.connection), with: renderedWith, workDir })
      const normalized = normalize(work, result)
      if (normalized.status !== "completed") {
        return normalized
      }
      const worktreeResult = await this.enforceCleanWorktree(work, workDir, normalized, renderedWith, variables, signal)
      if (worktreeResult.status !== "completed") {
        return worktreeResult
      }
      const finalResult = await this.captureAndUploadArtifacts(work, workspaceRoot, workDir, worktreeResult, result, variables, signal)
      if (finalResult.status === "completed") {
        const capturedOutputs = captureOutputs(work.outputs, result)
        if (capturedOutputs) {
          return { ...finalResult, capturedOutputs }
        }
      }
      return finalResult
    } catch (error) {
      if (error instanceof WorktreeProbeError) {
        return worktreeProbeFailure(work, error)
      }
      return failure(work, errorMessage(error))
    }
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
    work: WorkItem,
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
    work: WorkItem,
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

  private async executeChecks(work: WorkItem, signal: AbortSignal): Promise<WorkItemResult> {
    const variables = await this.variables(work, signal)
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

  private async variables(work: WorkItem, signal: AbortSignal): Promise<JsonObject> {
    const workspace = await this.workspaceManager.ensure(work, signal)
    const userVariables = work.variables ?? {}
    const userRunner = userVariables.runner
    const mergedRunner: JsonObject = { ...runnerVariables() }
    if (userRunner && typeof userRunner === "object" && !Array.isArray(userRunner)) {
      Object.assign(mergedRunner, userRunner as JsonObject)
    }
    return { ...userVariables, runner: mergedRunner, workspace: { path: workspace.path, branch: workspace.branch ?? null, changeDir: workspace.changeDir ?? null } }
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
    work: WorkItem,
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
    const declaredFailures = captureRequiresFailures(captureOutcome)
    if (declaredFailures.length > 0) {
      return {
        ...result,
        status: "failed",
        message: `${result.message ? result.message + "; " : ""}required declared artifacts could not be captured: ${summarizeCaptureFailures(declaredFailures)}`.slice(0, 4000),
      }
    }
    if (captureOutcome.captures.length === 0) {
      return result
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
    const uploadFailures = uploads.failures
    const requiredUploadFailures = uploadFailures.filter((failure) => failure.source === "declared")
    if (requiredUploadFailures.length > 0) {
      return {
        ...result,
        status: "failed",
        message: `${result.message ? result.message + "; " : ""}required declared artifacts could not be uploaded: ${summarizeCaptureFailures(requiredUploadFailures)}`.slice(0, 4000),
      }
    }
    const message = uploadFailures.length > 0
      ? `${result.message ? result.message + "; " : ""}some dynamic artifacts failed to upload: ${summarizeCaptureFailures(uploadFailures)}`
      : result.message
    return {
      ...result,
      message: message ?? result.message,
      artifactUploadIds: uploads.uploads.map((upload) => upload.uploadId),
    }
  }
}

function baseContext(work: WorkItem, variables: JsonObject, signal: AbortSignal, sessionManager: AcpSessionManager, acpConnection: SharedAcpConnection | null, connection: ServerConnection): Omit<ActionContext, "with" | "workDir"> {
  return { workflowRunId: work.workflowRunId, workId: work.workId, workType: work.workType, stage: work.stage, title: work.title, uses: work.uses, variables, signal, projectId: work.projectId, issueNumber: work.issueNumber, acpSessionManager: sessionManager, acpConnection, serverConnection: connection }
}

function normalize(work: WorkItem, result: WorkItemResult): WorkItemResult {
  const status = result.status.toLowerCase()
  if (work.workType === "check") {
    if (["pass", "passed", "success", "succeeded", "completed"].includes(status)) return { ...result, status: "pass" }
    if (status === "pending") return { ...result, status: "pending" }
    return { ...result, status: "fail" }
  }
  if (["completed", "success", "succeeded", "pass", "passed"].includes(status)) return { ...result, status: "completed" }
  return { ...result, status: "failed" }
}

function failure(work: WorkItem, message: string): WorkItemResult {
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

function stringAt(value: JsonObject, path: string[]) {
  const found = path.reduce<unknown>((current, part) => {
    if (typeof current !== "object" || current === null || Array.isArray(current)) return undefined
    return (current as JsonObject)[part]
  }, value)
  return typeof found === "string" ? found : undefined
}

function resolveWorkspacePath(workspaceRoot: string, requested: string) {
  const resolved = isAbsolute(requested) ? resolve(requested) : resolve(workspaceRoot, requested)
  const rel = relative(workspaceRoot, resolved)
  if (rel.startsWith("..") || isAbsolute(rel)) {
    throw new Error(`working-directory '${requested}' escapes workspace.path`)
  }
  return resolved
}

function formatUnresolvedError(work: WorkItem, unresolved: string[]): string {
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

function resolveMaxCleanupAttempts(variables: JsonObject): number {
  const candidate = variables["runner"]
  if (candidate && typeof candidate === "object" && !Array.isArray(candidate)) {
    const cleanup = (candidate as JsonObject)["cleanup"]
    if (cleanup && typeof cleanup === "object" && !Array.isArray(cleanup)) {
      const value = (cleanup as JsonObject)["maxAttempts"]
      if (typeof value === "number" && Number.isFinite(value) && value >= 0) return Math.floor(value)
      if (typeof value === "string") {
        const parsed = Number(value)
        if (Number.isFinite(parsed) && parsed >= 0) return Math.floor(parsed)
      }
    }
  }
  return DEFAULT_MAX_CLEANUP_ATTEMPTS
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

function buildCleanupWith(work: WorkItem, renderedWith: JsonObject | null, snapshot: WorktreeSnapshot, attempt: number): JsonObject {
  const existingWith = renderedWith ?? {}
  const existingSession = stringInput(existingWith as JsonObject, "session")
  const basePrompt = stringInput(existingWith as JsonObject, "prompt")
  const originalTitle = work.title?.trim() || work.uses || work.workId
  const cleanupWith: JsonObject = { ...existingWith }
  cleanupWith["prompt"] = buildCleanupPrompt({
    basePrompt,
    title: originalTitle,
    workId: work.workId,
    attempt,
    snapshot,
  })
  if (existingSession) cleanupWith["session"] = existingSession
  return cleanupWith
}

function buildCleanupPrompt(input: {
  basePrompt: string | undefined
  title: string
  workId: string
  attempt: number
  snapshot: WorktreeSnapshot
}): string {
  const staged = input.snapshot.staged
  const unstaged = input.snapshot.unstaged
  const untracked = input.snapshot.untracked
  const sections: string[] = []

  sections.push(`## Cleanup Follow-up (attempt ${input.attempt}) for ${input.title} (${input.workId})`)
  sections.push("")
  sections.push("The previous run of this task reported success but left uncommitted changes in the worktree. The task cannot be marked completed until the worktree is clean.")
  sections.push("")
  sections.push("### Hard constraints")
  sections.push("- Do NOT start any new task work. The original task is already considered done by the runner.")
  sections.push("- Do NOT push to any remote. Do not run `git push`, do not open a pull request, do not update a remote branch.")
  sections.push("- Do NOT modify files outside the scope of cleaning up the worktree. The only allowed operations are: `git add`, `git commit`, `git checkout -- <file>`, `git restore <file>`, and `git clean` (with care).")
  sections.push("- Do NOT close or replace the current agent session. The runner will continue this same session.")
  sections.push("")
  sections.push("### Current worktree state")
  sections.push(formatFileSection("Staged (added to index)", staged))
  sections.push(formatFileSection("Unstaged (modified in working tree)", unstaged))
  sections.push(formatFileSection("Untracked (not in index or working tree)", untracked))
  sections.push("")
  sections.push("### What to do")
  sections.push("1. For every file above, decide whether it is part of the original task output that should be kept, or unrelated noise that should be reverted.")
  sections.push("2. Commit task-related changes (keep) with `git add <file-or-dir> && git commit -m \"<short message>\"`. Use a clear message that names the task. Commit task-related changes or revert unrelated ones — the runner needs the worktree to be clean before the task can complete.")
  sections.push("3. Revert unrelated changes (discard) with `git checkout -- <file>` or `git restore <file>`. Remove untracked noise with `git clean -fd <path>` only when you are sure it is safe.")
  sections.push("4. End the run with `git status --porcelain` showing no output. The runner will re-check cleanliness after you return.")
  sections.push("5. In your final summary, report either:")
  sections.push("   - the commit SHA(s) you created (e.g. `Committed abc1234` or `Committed abc1234, def5678`)")
  sections.push("   - or `no-change` if you determined the worktree was already clean and made no commit.")
  sections.push("")
  if (input.basePrompt?.trim()) {
    sections.push("### Original task prompt (for context only — do not re-execute)")
    sections.push("> The original task asked for: " + input.basePrompt.trim().split("\n")[0])
    sections.push("")
  }
  sections.push(`Cleanup attempt counter: ${input.attempt}. The runner will retry up to its configured bound and then fail the task with structured dirty-worktree evidence.`)
  return sections.join("\n")
}

function formatFileSection(label: string, files: string[]): string {
  if (files.length === 0) return `- ${label}: (none)`
  return [`- ${label}:`, ...files.map((file) => `  - ${file}`)].join("\n")
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

function worktreeProbeFailure(work: WorkItem, error: WorktreeProbeError): WorkItemResult {
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
