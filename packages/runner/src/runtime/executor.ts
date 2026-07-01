import { isAbsolute, join, relative, resolve } from "node:path"
import type { ActionContext, ActionResult, JsonObject, JsonValue, AddTaskInput, RenderedWorkItem, WorkItemResult } from "../core/types.js"
import { stringInput } from "../core/json.js"
import { stringAt } from "../core/json-path.js"
import { renderTemplate, unresolvedReferences, wholeStringUnresolvedReferences } from "../core/template.js"
import { ensureDir } from "../system/process.js"
import { runnerVariables, WorkspaceManager } from "./workspace.js"
import type { ActionRegistry } from "../actions/registry.js"
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
  attachBranchStabilityEvidence,
  checkBranchStability,
  expectedWorkspaceBranch,
  type BranchStabilityEvidence,
} from "./branch-stability.js"
import { cleanupAgentAction, enforceCleanWorktree, type ContextParts } from "./worktree-enforcement.js"

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
      const startCheck = await checkBranchStability(work, workDir, expectedBranch, "start", signal)
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
      const endCheck = await checkBranchStability(work, workDir, expectedBranch, "end", signal)
      if (endCheck.kind === "violation") {
        return endCheck.result
      }
      const evidenceStack: BranchStabilityEvidence[] = [startCheck.evidence, endCheck.evidence]
      const contextParts: ContextParts = {
        sessionManager: this.sessionManager,
        acpConnection: this.acpConnection,
        connection: this.connection,
      }
      const worktreeResult = await enforceCleanWorktree(work, workDir, normalized, renderedWith, variables, signal, cleanupAgentAction, contextParts)
      if (worktreeResult.status !== "completed") {
        return attachBranchStabilityEvidence(worktreeResult, evidenceStack)
      }
      const withEvidence = attachBranchStabilityEvidence(worktreeResult, evidenceStack)
      const finalResult = await this.captureAndUploadArtifacts(work, workspaceRoot, workDir, withEvidence, result, variables, signal)
      const withCapturedOutputs = this.captureDeclaredOutputs(work, finalResult, result)
      return await this.applySetVars(work, withCapturedOutputs, signal)
    } catch (error) {
      return failure(work, errorMessage(error))
    }
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
  return {
    workflowRunId: work.workflowRunId,
    workId: work.workId,
    workType: work.workType,
    stage: work.stage,
    title: work.title,
    uses: work.uses,
    variables,
    signal,
    recovery: work.recovery,
    projectId: work.projectId,
    issueNumber: work.issueNumber,
    ownerKind: work.ownerKind,
    agentSessionId: work.agentSessionId,
    acpSessionManager: sessionManager,
    acpConnection,
    serverConnection: connection,
    writeVars: async (vars) => connection.patchRunVars(work.workflowRunId, vars, signal),
  }
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

function safeParseJson(value: string): JsonObject | null {
  try {
    const parsed = JSON.parse(value) as unknown
    return parsed && typeof parsed === "object" && !Array.isArray(parsed) ? (parsed as JsonObject) : null
  } catch {
    return null
  }
}

function errorMessage(error: unknown): string {
  if (error instanceof Error) return error.message
  if (error && typeof error === "object" && "name" in error && "message" in error) {
    return String((error as { message: unknown }).message)
  }
  return String(error)
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
    const recoveryValue = t["recovery"]
    tasks.push({
      id,
      title: typeof t["title"] === "string" ? t["title"] : id,
      uses: typeof t["uses"] === "string" ? t["uses"] : null,
      with: withValue && typeof withValue === "object" && !Array.isArray(withValue) ? (withValue as JsonObject) : null,
      recovery: recoveryValue && typeof recoveryValue === "object" && !Array.isArray(recoveryValue) ? (recoveryValue as JsonObject) : null,
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
