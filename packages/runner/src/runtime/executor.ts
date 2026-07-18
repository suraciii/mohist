import { isAbsolute, join, relative, resolve } from "node:path"
import type { ActionContext, ActionResult, JsonObject, RenderedWorkItem, WorkItemResult } from "../core/types.js"
import { isObject, stringInput } from "../core/json.js"
import { errorMessage } from "../core/errors.js"
import { stringAt } from "../core/json-path.js"
import { renderTemplate, wholeStringUnresolvedReferences } from "../core/template.js"
import { ensureDir } from "../system/process.js"
import { runnerVariables, WorkspaceManager, WorkspaceNetworkTimeoutError } from "./workspace.js"
import type { ActionRegistry } from "../actions/registry.js"
import type { ServerConnection } from "../server/connection.js"
import type { AcpSessionManager, SharedAcpConnection } from "./acp-connection.js"
import { captureAndUploadArtifactsForWork } from "./artifact-side-effects.js"
import { applySetVarsForWork } from "./set-vars-apply.js"
import { captureOutputs } from "./output-capture.js"
import {
  attachBranchStabilityEvidence,
  checkBranchStability,
  expectedWorkspaceBranch,
  type BranchStabilityEvidence,
} from "./branch-stability.js"
import { executeCheckDispatch, type CheckDeclaration } from "./check-execution.js"
import { tryRecovery } from "./recovery.js"
import { cleanupAgentAction, enforceCleanWorktree } from "./worktree-enforcement.js"
import { createCredentialMaskerFromEnvironment, TaskLogCollector, TaskLogger } from "./task-log.js"
import {
  evaluateCompletion,
  promiseValue,
  type CompletionEvaluation,
} from "../actions/expectations.js"

const COMPLETED_STATUSES = new Set(["completed", "success", "succeeded", "pass", "passed"])
const CHECK_STATUS_BY_ACTION_STATUS = new Map([
  ["pass", "pass"],
  ["passed", "pass"],
  ["success", "pass"],
  ["succeeded", "pass"],
  ["completed", "pass"],
  ["pending", "pending"],
])
const CHECK_WORK_TYPES = new Set(["check", "checks"])

/**
 * Action identifiers whose public Action Output is the minimal
 * `null | { promise }` shape defined by `mohist/opencode`'s contract
 * (opencode-action-contract spec). Every other Action preserves its
 * handler's own output unchanged through completion evaluation
 * (design D5).
 */
const PROMISE_PROJECTED_ACTIONS = new Set(["mohist/opencode"])

function isPromiseProjected(uses?: string | null): boolean {
  return !!uses && PROMISE_PROJECTED_ACTIONS.has(uses.trim().toLowerCase())
}

export class WorkExecutor {
  constructor(
    private readonly actions: ActionRegistry,
    private readonly workspaceManager: WorkspaceManager,
    private readonly connection: ServerConnection,
    private readonly sessionManager: AcpSessionManager,
    private acpConnection: SharedAcpConnection | null,
    private readonly fallbackWorkDir = process.cwd(),
    /**
     * Injected clock for {@link TaskLogCollector} timestamps. Defaults
     * to `Date.now` (real wall clock). Tests override it so the per-work
     * log timestamps are deterministic without `vi.useFakeTimers`
     * bleeding into other modules. Hosts do not typically override —
     * the existing `n` convention threads this from the executor's
     * constructor so the value is per-host, not per-work.
     */
    private readonly now: () => Date = () => new Date(),
  ) {}

  updateAcpConnection(acp: SharedAcpConnection | null) {
    this.acpConnection = acp
  }

  async execute(work: RenderedWorkItem, signal: AbortSignal): Promise<WorkItemResult> {
    return this.executeWithLog(work, signal, null).then((exec) => exec.result)
  }

  /**
   * Per-work execution that exposes the buffered
   * {@link TaskLogCollector} so the host can flush it as a terminal
   * batch via the independent task-log channel (design D6). Callers
   * that do not care about the collector (tests, ad-hoc CLI usage) can
   * keep calling {@link execute}.
   */
  async executeWithLog(work: RenderedWorkItem, signal: AbortSignal, collector: TaskLogCollector | null): Promise<WorkExecution> {
    const ownedCollector = collector ?? new TaskLogCollector({ now: this.now })
    const logger = new TaskLogger({ collector: ownedCollector, masker: createCredentialMaskerFromEnvironment() })
    let resolvedWorkspace: ResolvedWorkspace
    if (work.ownerKind !== "agent-job") {
      const precheck = await this.prepareWorkspace(work, signal, logger)
      if (precheck.kind === "failure") return { result: precheck.result, collector: ownedCollector }
      resolvedWorkspace = precheck.workspace
    } else {
      resolvedWorkspace = this.workspaceFromVariables(work)
    }

    if (work.workType === "checks") {
      const result = await this.executeChecks(work, resolvedWorkspace, signal, logger)
      return { result, collector: ownedCollector }
    }
    const result = await this.executeOne(work, resolvedWorkspace, signal, logger)
    return { result, collector: ownedCollector }
  }

  private async prepareWorkspace(work: RenderedWorkItem, signal: AbortSignal, log: TaskLogger): Promise<{ kind: "ok", workspace: ResolvedWorkspace } | { kind: "failure", result: WorkItemResult }> {
    try {
      const info = await this.workspaceManager.prepare(work, signal, log)
      return { kind: "ok", workspace: infoToResolved(info) }
    } catch (error) {
      return { kind: "failure", result: workspaceSetupFailure(work, error) }
    }
  }

  private async executeOne(work: RenderedWorkItem, resolvedWorkspace: ResolvedWorkspace, signal: AbortSignal, log: TaskLogger): Promise<WorkItemResult> {
    const action = this.actions.resolve(work.uses)
    if (!action) return failure(work, `No action found for '${work.uses}'`)

    try {
      const variables = await this.variables(work, resolvedWorkspace, signal)
      const unresolved = [...wholeStringUnresolvedReferences(work.with, variables), ...wholeStringUnresolvedReferences(work.expect, variables)]
      if (unresolved.length > 0) {
        return failure(work, formatUnresolvedError(work, unresolved))
      }
      const renderedWith = renderTemplate(work.with, variables)
      const renderedExpect = renderTemplate(work.expect, variables)
      const workspaceRoot = this.workspaceRoot(variables)
      const workDir = await this.resolveWorkDir(renderedWith, workspaceRoot)
      const expectedBranch = expectedWorkspaceBranch(variables)
      const startCheck = await checkBranchStability(work, workDir, expectedBranch, "start", signal, log)
      if (startCheck.kind === "violation") {
        return startCheck.result
      }
      const result = await action({
        ...baseContext(work, variables, signal, this.sessionManager, this.acpConnection, this.connection, log),
        with: renderedWith,
        rawWith: work.with,
        workDir,
      })
      // stripRunnerPrivateFacts drops ActionResult.turnFact from the wire.
      // The fact is consumed only by completion evaluation below; it MUST
      // never be serialized into WorkItemResult.output, recovery matching,
      // setVars projections, captured outputs, or artifacts (design D4).
      const { publicActionResult, turnFact } = stripRunnerPrivateFacts(result)
      const completion = await evaluateCompletion(renderedExpect, workDir, turnFact?.finalAssistantText ?? null)
      const projected = projectTaskOutput(work, publicActionResult, completion)
      const normalized = normalize(work, projected)
      const recoveryResult = tryRecovery(work, normalized)
      if (recoveryResult) return recoveryResult
      // For `mohist/opencode`, the public Action Output is the minimal
      // `null | { promise }` shape — branch-stability evidence and
      // other diagnostics MUST NOT pollute it (opencode-action-contract
      // spec). For every other Action, we embed the evidence as the
      // task surfaced output so the UI/server can inspect it.
      const promiseProjected = isPromiseProjected(work.uses)
      if (normalized.status !== "completed") {
        return promiseProjected
          ? normalized
          : attachBranchStabilityEvidence(normalized, startCheck.evidence)
      }
      const endCheck = await checkBranchStability(work, workDir, expectedBranch, "end", signal, log)
      if (endCheck.kind === "violation") {
        return endCheck.result
      }
      const evidenceStack: BranchStabilityEvidence[] = [startCheck.evidence, endCheck.evidence]
      const worktreeResult = await enforceCleanWorktree(
        work,
        workDir,
        normalized,
        renderedWith,
        variables,
        signal,
        cleanupAgentAction,
        { baseContext: (cleanupWork, cleanupVariables, cleanupSignal) => baseContext(cleanupWork, cleanupVariables, cleanupSignal, this.sessionManager, this.acpConnection, this.connection, log) },
        log,
      )
      if (worktreeResult.status !== "completed") {
        return promiseProjected
          ? worktreeResult
          : attachBranchStabilityEvidence(worktreeResult, evidenceStack)
      }
      const withEvidence = promiseProjected
        ? worktreeResult
        : attachBranchStabilityEvidence(worktreeResult, evidenceStack)
      const finalResult = await captureAndUploadArtifactsForWork(this.connection, work, workspaceRoot, workDir, withEvidence, publicActionResult, variables, signal)
      const withCapturedOutputs = this.captureDeclaredOutputs(work, finalResult, publicActionResult)
      return await applySetVarsForWork(this.connection, work, withCapturedOutputs, signal)
    } catch (error) {
      return failure(work, errorMessage(error))
    }
  }

  private async executeChecks(work: RenderedWorkItem, resolvedWorkspace: ResolvedWorkspace, signal: AbortSignal, log: TaskLogger): Promise<WorkItemResult> {
    const variables = await this.variables(work, resolvedWorkspace, signal)
    const rawChecks: unknown[] = Array.isArray(work.with?.checks) ? work.with.checks : []
    const checks = rawChecks.filter(isCheck)
    const workspaceRoot = this.workspaceRoot(variables)
    return await executeCheckDispatch(checks, variables, {
      actions: this.actions,
      context: baseContext(work, variables, signal, this.sessionManager, this.acpConnection, this.connection, log),
      formatUnresolved: formatCheckUnresolvedError,
      resolveWorkDir: (withInput) => this.resolveWorkDir(withInput, workspaceRoot),
      toCheckStatus,
    })
  }

  private async variables(work: RenderedWorkItem, resolvedWorkspace: ResolvedWorkspace, signal: AbortSignal): Promise<JsonObject> {
    const workspace = resolvedWorkspaceToVariables(resolvedWorkspace)
    const userVariables = work.variables ?? {}
    const userRunner = userVariables.runner
    const mergedRunner: JsonObject = { ...runnerVariables() }
    if (isObject(userRunner)) {
      Object.assign(mergedRunner, userRunner)
    }
    return { ...userVariables, runner: mergedRunner, workspace }
  }

  // Read the workspace triple from dispatch variables (agent-job: caller-owned,
  // not re-cloned/verified by the runner — issue #126 standalone-workspace).
  private workspaceFromVariables(work: RenderedWorkItem): ResolvedWorkspace {
    const variables = work.variables ?? {}
    const ws = variables["workspace"]
    if (!isObject(ws)) {
      return { path: "", branch: null, changeDir: null }
    }
    return {
      path: stringField(ws, "path") ?? "",
      branch: stringField(ws, "branch"),
      changeDir: stringField(ws, "changeDir"),
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

  private captureDeclaredOutputs(work: RenderedWorkItem, result: WorkItemResult, actionResult: ActionResult): WorkItemResult {
    if (result.status !== "completed") return result
    const capturedOutputs = captureOutputs(work.outputs, actionResult)
    return capturedOutputs ? { ...result, capturedOutputs } : result
  }
}

/**
 * Drop ActionResult.turnFact from the Action result that crosses the
 * Action → Work boundary. The private fact channel is runner-internal
 * only; passing it through would surface runtime Session identity,
 * model observations, usage, transcript text, diagnostics, and the
 * final assistant text to the server, recovery matching, setVars
 * projections, captured outputs, and artifacts.
 *
 * The boundary is the canonical place to enforce this invariant — see
 * `AcpSessionResult`, `ActionResult.turnFact` (core/types.ts), and
 * design D4.
 */
function stripRunnerPrivateFacts(result: ActionResult): {
  publicActionResult: ActionResult
  turnFact: { finalAssistantText?: string | null } | null
} {
  if (!result || typeof result !== "object" || !("turnFact" in result)) {
    return { publicActionResult: result, turnFact: null }
  }
  const turnFact = result.turnFact ?? null
  const { turnFact: _ignored, ...rest } = result
  return { publicActionResult: rest as ActionResult, turnFact }
}

/**
 * Project the public task output AFTER completion evaluation so a
 * matched promise marker can drive `recovery when: promise=FAIL`
 * matching. The projection only rewires the `output` field; the rest
 * of the Action result (status/message/exitCode) flows through
 * unchanged.
 *
 * Behavior (design D5):
 *  - `mohist/opencode`: handler output is discarded. If completion
 *    matched a promise marker, output becomes `{ "promise": "<value>" }`;
 *    otherwise output is `null`. Other Action-owned fields do NOT
 *    appear.
 *  - Every other Action: output is preserved unchanged, including any
 *    structured `errorCode`/`prNumber`/etc. Completion diagnostics
 *    live in `result.message` (or in a failIf-driven status flip), not
 *    in `output`.
 *
 * A successful Action with unsatisfied expectations (missing files,
 * missing markers, failIf matched) becomes an ordinary failed
 * completion — that is the spec scenario "An unmet expectation does
 * not trigger an implicit repair turn". A separate Action failure is
 * preserved as-is: completion is not a recovery mechanism.
 */
function projectTaskOutput(
  work: RenderedWorkItem,
  result: ActionResult,
  completion: CompletionEvaluation,
): ActionResult {
  const uses = work.uses?.trim().toLowerCase() ?? ""
  if (PROMISE_PROJECTED_ACTIONS.has(uses)) {
    const value = promiseValue(completion.matched ?? null)
    const projectedOutput = value !== null ? JSON.stringify({ promise: value }) : null
    // For opencode, an unmet completion contract (no promise marker,
    // missing files, failIf matched) still drops the handler's raw
    // output. The status flip for "Action succeeded but completion
    // unsatisfied" lives in the non-opencode branch below.
    const statusFlip = completion.satisfied ? result.status : "failure"
    const message = completion.satisfied ? result.message : completion.message
    return { ...result, status: statusFlip, output: projectedOutput, message }
  }
  // Action failure stays an Action failure. An unmet expectation only
  // fails the task when the Action itself succeeded (so an Action that
  // returned `failure` does not get re-judged by completion).
  if (result.status !== "success" && result.status !== "completed") {
    return result
  }
  // Successful Action + unsatisfied expectation → failed completion with
  // the diagnostic message and the Action's exitCode preserved.
  if (!completion.satisfied) {
    return { ...result, status: "failure", message: completion.message }
  }
  return result
}

/**
 * Per-work execution outcome. The {@link TaskLogCollector} is returned
 * alongside the {@link WorkItemResult} so the host (`RunnerHost`) can
 * flush it as a terminal batch via the independent task-log channel
 * even when the work failed (best-effort: flush never blocks or fails
 * the report). Design D6.
 */
export interface WorkExecution {
  result: WorkItemResult
  collector: TaskLogCollector
}

type ResolvedWorkspace = { path: string, branch: string | null, changeDir: string | null }

function infoToResolved(info: { path: string, branch?: string | null, changeDir?: string | null }): ResolvedWorkspace {
  return { path: info.path, branch: info.branch ?? null, changeDir: info.changeDir ?? null }
}

function resolvedWorkspaceToVariables(workspace: ResolvedWorkspace): JsonObject {
  return { path: workspace.path, branch: workspace.branch, changeDir: workspace.changeDir }
}

function baseContext(work: RenderedWorkItem, variables: JsonObject, signal: AbortSignal, sessionManager: AcpSessionManager, acpConnection: SharedAcpConnection | null, connection: ServerConnection, log: TaskLogger | null = null): Omit<ActionContext, "with" | "workDir"> {
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
    epicNumber: work.epicNumber,
    ownerKind: work.ownerKind,
    agentJobId: work.agentJobId,
    agentSessionId: work.agentSessionId,
    acpSessionManager: sessionManager,
    acpConnection,
    serverConnection: connection,
    log,
    writeVars: async (vars) => connection.patchRunVars(work.workflowRunId, vars, signal),
  }
}

// Workspace-prepare failure (clone/base-branch/checkout): `output.kind` is
// structured so the CLI/API/UI render it distinctly from ordinary task failures.
function workspaceSetupFailure(work: RenderedWorkItem, error: unknown): WorkItemResult {
  const message = error instanceof Error ? error.message : String(error)
  const output = error instanceof WorkspaceNetworkTimeoutError
    ? JSON.stringify({ kind: "workspace-setup", failureKind: "retry-safe", step: error.step })
    : JSON.stringify({ kind: "workspace-setup" })
  return {
    status: failureStatus(work),
    message: `could not prepare workflow workspace (workspace-setup): ${message}`.slice(0, 4000),
    output,
  }
}

function normalize(work: RenderedWorkItem, result: WorkItemResult): WorkItemResult {
  const status = result.status.toLowerCase()
  if (work.workType === "check") {
    return { ...result, status: toCheckStatus(status) }
  }
  return { ...result, status: COMPLETED_STATUSES.has(status) ? "completed" : "failed" }
}

function failure(work: RenderedWorkItem, message: string): WorkItemResult {
  return { status: failureStatus(work), message }
}

function failureStatus(work: RenderedWorkItem): "fail" | "failed" {
  return CHECK_WORK_TYPES.has(work.workType) ? "fail" : "failed"
}

function toCheckStatus(status: string) {
  return CHECK_STATUS_BY_ACTION_STATUS.get(status.toLowerCase()) ?? "fail"
}

function isCheck(value: unknown): value is CheckDeclaration {
  return isObject(value) && typeof value.uses === "string"
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

function stringField(obj: JsonObject, key: string): string | null {
  const value = obj[key]
  return typeof value === "string" ? value : null
}
