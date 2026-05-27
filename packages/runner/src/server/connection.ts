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
    const response = await fetch(this.url("report"), { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ workId: work.workId, status: result.status, message: result.message, output: result.output, exitCode: result.exitCode }), signal })
    if (!response.ok) throw new Error(`report failed: ${response.status} ${await response.text()}`)
    try {
      return await response.json() as Record<string, unknown>
    } catch {
      return {}
    }
  }

  async sessionStarted(sessionId: string, body: unknown, signal: AbortSignal) {
    await this.post(`sessions/${sessionId}/started`, body, signal)
  }

  async sessionEvents(sessionId: string, events: unknown[], signal: AbortSignal) {
    await this.post(`sessions/${sessionId}/events`, { events }, signal)
  }

  async sessionCompleted(sessionId: string, body: unknown, signal: AbortSignal) {
    await this.post(`sessions/${sessionId}/completed`, body, signal)
  }

  async sessionStatus(sessionId: string, body: unknown, signal: AbortSignal) {
    await this.post(`sessions/${sessionId}/status`, body, signal)
  }

  async getSession(workflowRunId: string, sessionName: string, signal: AbortSignal): Promise<{ acpSessionId: string; workDir: string } | null> {
    const response = await fetch(this.url(`workflow-sessions/${encodeURIComponent(workflowRunId)}/${encodeURIComponent(sessionName)}`), { signal })
    if (response.status === 204) return null
    if (!response.ok) return null
    return response.json() as Promise<{ acpSessionId: string; workDir: string }>
  }

  async registerSession(workflowRunId: string, sessionName: string, acpSessionId: string, workDir: string, signal: AbortSignal) {
    await this.post(`workflow-sessions/${encodeURIComponent(workflowRunId)}/${encodeURIComponent(sessionName)}`, { acpSessionId, workDir }, signal)
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
    session: dispatch.session,
  }
}
