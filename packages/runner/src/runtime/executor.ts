import { join } from "node:path"
import type { ActionContext, JsonObject, WorkItem, WorkItemResult } from "../core/types.js"
import { stringInput } from "../core/json.js"
import { renderTemplate } from "../core/template.js"
import { ensureDir } from "../system/process.js"
import { runnerVariables, WorkspaceManager } from "./workspace.js"
import type { ActionRegistry } from "../actions/registry.js"
import type { ServerConnection } from "../server/connection.js"
import type { AcpSessionPool } from "./session-pool.js"

export class WorkExecutor {
  constructor(
    private readonly actions: ActionRegistry,
    private readonly workspaceManager: WorkspaceManager,
    private readonly connection: ServerConnection,
    private readonly pool: AcpSessionPool,
    private readonly fallbackWorkDir = process.cwd(),
  ) {}

  async execute(work: WorkItem, signal: AbortSignal): Promise<WorkItemResult> {
    if (work.workType === "checks") return await this.executeChecks(work, signal)
    return await this.executeOne(work, signal)
  }

  private async executeOne(work: WorkItem, signal: AbortSignal): Promise<WorkItemResult> {
    const action = this.actions.resolve(work.uses)
    if (!action) return failure(work, `No action found for '${work.uses}'`)

    try {
      const variables = await this.variables(work, signal)
      const renderedWith = renderTemplate(work.with, variables)
      const workDir = await this.resolveWorkDir(renderedWith, variables)
      return normalize(work, await action({ ...baseContext(work, variables, signal, this.pool, this.connection), with: renderedWith, workDir, telemetry: telemetry(this.connection) }))
    } catch (error) {
      return failure(work, error instanceof Error ? error.message : String(error))
    }
  }

  private async executeChecks(work: WorkItem, signal: AbortSignal): Promise<WorkItemResult> {
    const variables = await this.variables(work, signal)
    const checks = Array.isArray(work.with?.checks) ? work.with.checks.filter(isCheck) : []
    if (checks.length === 0) return failure(work, "No checks found in dispatch")

    const results = await Promise.all(checks.map(async (check) => {
      const action = this.actions.resolve(check.uses)
      if (!action) return { name: check.name, status: "fail", message: `No action found for '${check.uses}'` }
      try {
        const renderedWith = renderTemplate(check.with ?? null, variables)
        const workDir = await this.resolveWorkDir(renderedWith, variables)
        const result = await action({ ...baseContext(work, variables, signal, this.pool, this.connection), workType: "check", title: check.title, uses: check.uses, with: renderedWith, workDir, telemetry: telemetry(this.connection) })
        return { name: check.name, status: toCheckStatus(result.status), message: result.message, output: result.output }
      } catch (error) {
        return { name: check.name, status: "fail", message: error instanceof Error ? error.message : String(error) }
      }
    }))

    return { status: results.every((result) => result.status === "pass") ? "pass" : "fail", output: JSON.stringify(results) }
  }

  private async variables(work: WorkItem, signal: AbortSignal): Promise<JsonObject> {
    const workspace = await this.workspaceManager.ensure(work, signal)
    return { ...(work.variables ?? {}), runner: runnerVariables(), workspace: { path: workspace.path, branch: workspace.branch ?? null, changeDir: workspace.changeDir ?? null } }
  }

  private async resolveWorkDir(withInput: JsonObject | null, variables: JsonObject) {
    const workDir = stringInput(withInput, "working-directory") ?? stringAt(variables, ["workspace", "path"]) ?? join(this.fallbackWorkDir, "default")
    await ensureDir(workDir)
    return workDir
  }
}

function baseContext(work: WorkItem, variables: JsonObject, signal: AbortSignal, pool: AcpSessionPool, connection: ServerConnection): Omit<ActionContext, "with" | "workDir"> {
  return { workflowRunId: work.workflowRunId, workId: work.workId, workType: work.workType, stage: work.stage, title: work.title, uses: work.uses, variables, signal, session: work.session, sessionPool: pool, serverConnection: connection }
}

function normalize(work: WorkItem, result: WorkItemResult): WorkItemResult {
  const status = result.status.toLowerCase()
  if (work.workType === "check") {
    if (["pass", "passed", "success", "succeeded", "completed"].includes(status)) return { ...result, status: "pass" }
    if (status === "pending") return { ...result, status: "pending" }
    return { ...result, status: "fail" }
  }
  if (work.workType === "load") {
    if (["loaded", "success", "succeeded", "completed"].includes(status)) return { ...result, status: "loaded" }
    return { ...result, status: "failed" }
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

function telemetry(connection: ServerConnection) {
  return {
    started: (sessionId: string, body: unknown, signal: AbortSignal) => connection.sessionStarted(sessionId, body, signal),
    events: (sessionId: string, events: unknown[], signal: AbortSignal) => connection.sessionEvents(sessionId, events, signal),
    completed: (sessionId: string, body: unknown, signal: AbortSignal) => connection.sessionCompleted(sessionId, body, signal),
    status: (sessionId: string, body: unknown, signal: AbortSignal) => connection.sessionStatus(sessionId, body, signal),
  }
}
