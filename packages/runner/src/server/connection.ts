import { hostname } from "node:os"
import type { RunnerOptions, RunnerRegistration, WorkDispatchResponse, WorkItem, WorkItemResult } from "../core/types.js"
import { parseObject } from "../core/json.js"

export class ServerConnection {
  constructor(private readonly options: RunnerOptions) {}

  async connect(registration: RunnerRegistration, signal: AbortSignal) {
    await this.post("register", { hostname: hostname(), ...registration }, signal)
  }

  async heartbeat(signal: AbortSignal) {
    await this.post("heartbeat", undefined, signal)
  }

  async disconnect(signal: AbortSignal) {
    await this.post("unregister", undefined, signal)
  }

  async poll(signal: AbortSignal): Promise<WorkItem | null> {
    const response = await fetch(this.url("poll"), { method: "POST", signal })
    if (response.status === 204) return null
    if (!response.ok) throw new Error(`poll failed: ${response.status} ${await response.text()}`)
    return toWorkItem((await response.json()) as WorkDispatchResponse)
  }

  async report(work: WorkItem, result: WorkItemResult, signal: AbortSignal): Promise<Record<string, unknown>> {
    const response = await fetch(this.url("report"), { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ workflowRunId: work.workflowRunId, workId: work.workId, projectId: work.projectId, status: result.status, message: result.message, output: result.output, exitCode: result.exitCode }), signal })
    if (!response.ok) throw new Error(`report failed: ${response.status} ${await response.text()}`)
    try {
      return await response.json() as Record<string, unknown>
    } catch {
      return {}
    }
  }

  async getWorkflowAgentSession(projectId: string, workflowRunId: string, sessionName: string, signal: AbortSignal): Promise<{ acpSessionId?: string | null; workDir?: string | null } | null> {
    const response = await fetch(this.url(`sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(workflowRunId)}/${encodeURIComponent(sessionName)}`), { method: "GET", signal })
    if (response.status === 404) return null
    if (!response.ok) throw new Error(`session lookup failed: ${response.status} ${await response.text()}`)
    return response.json() as Promise<{ acpSessionId?: string | null; workDir?: string | null }>
  }

  async openWorkflowAgentSession(projectId: string, workflowRunId: string, sessionName: string, body: unknown, signal: AbortSignal): Promise<{ acpSessionId?: string | null; workDir?: string | null }> {
    const response = await fetch(this.url(`sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(workflowRunId)}/${encodeURIComponent(sessionName)}/open`), { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body), signal })
    if (!response.ok) throw new Error(`session open failed: ${response.status} ${await response.text()}`)
    return response.json() as Promise<{ acpSessionId?: string | null; workDir?: string | null }>
  }

  async addTasks(workflowRunId: string, tasks: Array<{ id: string; title: string; uses?: string; with?: string | null }>) {
    const response = await fetch(`${this.options.serverUrl.replace(/\/$/, "")}/api/workflow-runs/${encodeURIComponent(workflowRunId)}/tasks/batch`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ tasks }),
    })
    if (!response.ok) throw new Error(`addTasks failed: ${response.status} ${await response.text()}`)
  }

  async attachWorkflowAgentSession(projectId: string, workflowRunId: string, sessionName: string, body: unknown, signal: AbortSignal) {
    await this.post(`sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(workflowRunId)}/${encodeURIComponent(sessionName)}/attach`, body, signal)
  }

  async workflowAgentSessionRuntimeEvents(projectId: string, workflowRunId: string, sessionName: string, body: unknown, signal: AbortSignal) {
    await this.post(`sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(workflowRunId)}/${encodeURIComponent(sessionName)}/runtime-events`, body, signal)
  }

  private async post(path: string, body: unknown, signal: AbortSignal) {
    const response = await fetch(this.url(path), { method: "POST", headers: body === undefined ? undefined : { "content-type": "application/json" }, body: body === undefined ? undefined : JSON.stringify(body), signal })
    if (!response.ok) throw new Error(`${path} failed: ${response.status} ${await response.text()}`)
  }

  private url(path: string) {
    return `${this.options.serverUrl.replace(/\/$/, "")}/api/runner/${encodeURIComponent(this.options.runnerId)}/${path}`
  }
}

function toWorkItem(dispatch: WorkDispatchResponse): WorkItem {
  return {
    workflowRunId: dispatch.workflowRunId,
    workId: dispatch.workId,
    workType: dispatch.workType,
    stage: dispatch.stage,
    title: dispatch.title,
    uses: dispatch.uses,
    with: parseObject(dispatch.with),
    variables: parseObject(dispatch.variables),
    projectId: dispatch.projectId,
    issueNumber: dispatch.issueNumber ?? undefined,
  }
}
