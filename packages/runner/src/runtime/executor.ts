import { isAbsolute, join, relative, resolve } from "node:path"
import type { ActionContext, ActionResult, JsonObject, RenderedWorkItem, WorkItemResult } from "../core/types.js"
import { isObject, stringInput } from "../core/json.js"
import { errorMessage } from "../core/errors.js"
import { stringAt } from "../core/json-path.js"
import { renderTemplate, wholeStringUnresolvedReferences } from "../core/template.js"
import { ensureDir } from "../system/process.js"
import { runnerVariables, WorkspaceManager } from "./workspace.js"
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
      const worktreeResult = await enforceCleanWorktree(
        work,
        workDir,
        normalized,
        renderedWith,
        variables,
        signal,
        cleanupAgentAction,
        { baseContext: (cleanupWork, cleanupVariables, cleanupSignal) => baseContext(cleanupWork, cleanupVariables, cleanupSignal, this.sessionManager, this.acpConnection, this.connection) },
      )
      if (worktreeResult.status !== "completed") {
        return attachBranchStabilityEvidence(worktreeResult, evidenceStack)
      }
      const withEvidence = attachBranchStabilityEvidence(worktreeResult, evidenceStack)
      const finalResult = await captureAndUploadArtifactsForWork(this.connection, work, workspaceRoot, workDir, withEvidence, result, variables, signal)
      const withCapturedOutputs = this.captureDeclaredOutputs(work, finalResult, result)
      return await applySetVarsForWork(this.connection, work, withCapturedOutputs, signal)
    } catch (error) {
      return failure(work, errorMessage(error))
    }
  }

  private async executeChecks(work: RenderedWorkItem, resolvedWorkspace: ResolvedWorkspace, signal: AbortSignal): Promise<WorkItemResult> {
    const variables = await this.variables(work, resolvedWorkspace, signal)
    const rawChecks: unknown[] = Array.isArray(work.with?.checks) ? work.with.checks : []
    const checks = rawChecks.filter(isCheck)
    const workspaceRoot = this.workspaceRoot(variables)
    return await executeCheckDispatch(checks, variables, {
      actions: this.actions,
      context: baseContext(work, variables, signal, this.sessionManager, this.acpConnection, this.connection),
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

// Workspace-prepare failure (clone/base-branch/checkout): `output.kind` is
// structured so the CLI/API/UI render it distinctly from ordinary task failures.
function workspaceSetupFailure(work: RenderedWorkItem, error: unknown): WorkItemResult {
  const message = error instanceof Error ? error.message : String(error)
  return {
    status: failureStatus(work),
    message: `could not prepare workflow workspace (workspace-setup): ${message}`.slice(0, 4000),
    output: JSON.stringify({ kind: "workspace-setup" }),
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
