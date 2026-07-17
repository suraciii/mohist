import { hostname } from "node:os"
import type { CleanupPolicy, JsonObject, RenderedWorkItem, RunnerConfigResponse, RunnerOptions, RunnerRegistration, WorkDispatchResponse, WorkItemResult } from "../core/types.js"
import { parseObject } from "../core/json.js"
import { getSegments } from "../core/json-path.js"
import type { TaskLogBatch } from "../runtime/task-log.js"

export class ServerConnection {
  private readonly buildGitHash: string | null

  constructor(private readonly options: RunnerOptions, buildGitHash: string | null = null) {
    this.buildGitHash = buildGitHash
  }

  async connect(registration: RunnerRegistration, signal: AbortSignal) {
    await this.post("register", { hostname: hostname(), ...registration }, signal)
  }

  async heartbeat(state: RunnerRegistration, signal: AbortSignal) {
    await this.post("heartbeat", { hostname: hostname(), ...state, buildGitHash: this.buildGitHash }, signal)
  }

  async disconnect(signal: AbortSignal) {
    await this.post("unregister", undefined, signal)
  }

  /**
   * Polls the server for dispatches. The body carries the process's full level
   * state (`inFlight` + `awaitingAck` work keys) so the server can reconcile
   * (`desired − reported`): repair lost dispatches and serve new claims against
   * spare capacity. The response is `{ dispatches: [...] }` carrying zero or
   * more work items; an empty list (HTTP 204 or empty array) means nothing to
   * do this round. Multi-dispatch replaces the old one-dispatch-per-poll limit.
   */
  async poll(
    signal: AbortSignal,
    report: { inFlight: string[]; awaitingAck: string[] } = { inFlight: [], awaitingAck: [] },
  ): Promise<RenderedWorkItem[]> {
    const response = await fetch(this.url("poll"), {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(report),
      signal,
    })
    if (response.status === 204) return []
    if (!response.ok) throw new Error(`poll failed: ${response.status} ${await response.text()}`)
    const payload = (await response.json()) as { dispatches?: WorkDispatchResponse[] } | WorkDispatchResponse
    // Tolerate both the new envelope `{ dispatches: [...] }` and a legacy
    // single-object response during a rolling update.
    const list = Array.isArray(payload) ? payload
      : "dispatches" in payload && Array.isArray(payload.dispatches) ? payload.dispatches
      : [payload as WorkDispatchResponse]
    return list.map(toWorkItem)
  }

  async fetchConfig(signal: AbortSignal): Promise<CleanupPolicy | null> {
    const response = await fetch(this.url("config"), { method: "GET", signal })
    if (!response.ok) throw new Error(`fetchConfig failed: ${response.status} ${await response.text()}`)
    const payload = (await response.json()) as RunnerConfigResponse
    return payload.cleanupPolicy ?? null
  }

  async workflowRunsStatus(workflowRunIds: string[], signal: AbortSignal): Promise<Record<string, string>> {
    if (workflowRunIds.length === 0) return {}
    const response = await fetch(this.url("workflow-runs/status"), {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ workflowRunIds }),
      signal,
    })
    if (!response.ok) throw new Error(`workflowRunsStatus failed: ${response.status} ${await response.text()}`)
    const payload = (await response.json()) as unknown
    const statuses = readObject(payload, ["statuses"])
    if (!statuses) return {}
    const result: Record<string, string> = {}
    for (const [key, value] of Object.entries(statuses)) {
      if (typeof value === "string") result[key] = value
    }
    return result
  }

  async report(work: RenderedWorkItem, result: WorkItemResult, signal: AbortSignal): Promise<Record<string, unknown>> {
    const ownerKind = work.ownerKind?.trim().toLowerCase()
    const body: Record<string, unknown> = {
      workId: work.workId,
      projectId: work.projectId,
      status: result.status,
      message: result.message,
      output: result.output,
      exitCode: result.exitCode,
      artifactUploadIds: result.artifactUploadIds ?? null,
      cleanupAttempts: result.cleanupAttempts ?? null,
      addTasks: result.addTasks ?? null,
    }
    if (ownerKind) {
      body.ownerKind = ownerKind
    }
    if (work.agentJobId) {
      body.agentJobId = work.agentJobId
    }
    if (ownerKind !== "agent-job") {
      body.workflowRunId = work.workflowRunId
    }
    const response = await fetch(this.url("report"), { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body), signal })
    if (!response.ok) throw new Error(`report failed: ${response.status} ${await response.text()}`)
    try {
      return await response.json() as Record<string, unknown>
    } catch {
      return {}
    }
  }

  /**
   * Upload a captured artifact to the internal multipart endpoint
   * (`POST /api/workflow-runs/{workflowRunId}/work/{workId}/artifact-uploads`).
   *
   * The endpoint identifies the producing task run from the active work
   * context (workflow run + work id), so the runner does not pass an
   * `attempt` number — that would be an unauthenticated guess the server
   * refuses. The upload metadata is intentionally minimal: `path`,
   * `contentType`, `contentHash`, `size`, and the binary `content`
   * part.
   */
  async uploadArtifact(
    ownerId: string,
    workId: string,
    upload: ArtifactUploadRequest,
    signal: AbortSignal,
    ownerKind = "workflow",
  ): Promise<ArtifactUploadResponse> {
    const form = new FormData()
    form.set("path", upload.path)
    if (upload.contentType) form.set("contentType", upload.contentType)
    if (upload.contentHash) form.set("contentHash", upload.contentHash)
    form.set("size", String(upload.size))
    const view = new Uint8Array(upload.content.byteLength)
    view.set(upload.content)
    const blob = new Blob([view], { type: upload.contentType ?? "application/octet-stream" })
    form.set("content", blob, upload.filename ?? "artifact")
    const response = await fetch(this.artifactUrl(ownerId, workId, ownerKind), {
      method: "POST",
      body: form,
      signal,
    })
    const text = await response.text()
    let payload: Record<string, unknown> | null = null
    if (text) {
      try {
        payload = JSON.parse(text) as Record<string, unknown>
      } catch {
        payload = null
      }
    }
    if (!response.ok) {
      const errorMessage = extractErrorMessage(payload, text) ?? `artifact upload failed: ${response.status}`
      const error = new Error(errorMessage) as Error & { code?: string; uploadId?: string; status: number }
      error.status = response.status
      if (payload) {
        const code = readString(payload, ["code"])
        if (code) error.code = code
        const uploadId = readString(payload, ["details", "existingUploadId"]) ?? readString(payload, ["data", "uploadId"]) ?? readString(payload, ["uploadId"])
        if (uploadId) error.uploadId = uploadId
      }
      throw error
    }
    const data = readObject(payload, ["data"]) ?? payload ?? {}
    return {
      uploadId: readString(data, ["uploadId"]) ?? "",
      workflowRunId: readString(data, ["workflowRunId"]) ?? ownerId,
      workId: readString(data, ["workId"]) ?? workId,
      taskRunId: readString(data, ["taskRunId"]) ?? null,
      path: readString(data, ["path"]) ?? upload.path,
      contentType: readString(data, ["contentType"]) ?? upload.contentType ?? null,
      contentHash: readString(data, ["contentHash"]) ?? upload.contentHash ?? null,
      size: readNumber(data, ["size"]) ?? upload.size,
      createdAt: readString(data, ["createdAt"]) ?? null,
      expiresAt: readString(data, ["expiresAt"]) ?? null,
      idempotent: readBoolean(data, ["idempotent"]) ?? false,
    }
  }

  private artifactUrl(ownerId: string, workId: string, ownerKind: string) {
    if (ownerKind === "agent-job") {
      return `${this.options.serverUrl.replace(/\/$/, "")}/api/agent-jobs/${encodeURIComponent(ownerId)}/work/${encodeURIComponent(workId)}/artifact-uploads`
    }

    return `${this.options.serverUrl.replace(/\/$/, "")}/api/workflow-runs/${encodeURIComponent(ownerId)}/work/${encodeURIComponent(workId)}/artifact-uploads`
  }

  /**
   * Upload a task-log terminal batch to the dedicated, independent
   * task-log channel. Mirrors {@link uploadArtifact}'s routing shape
   * (owner-kind pair), but the body is JSON, not multipart, and the
   * server endpoint is a separate store (`TaskLogStore`) that does
   * not invoke any grain — the upload is decoupled from the report
   * call and from status adjudication (design D1 / D6 / D7).
   *
   * `ownerKind` defaults to `"workflow"` for backwards compatibility
   * with callers that always dispatch workflow-scoped work; pass
   * `"agent-job"` explicitly for agent-job dispatches (same algorithm
   * as `artifact-side-effects.ts:107`).
   */
  async uploadTaskLog(
    ownerId: string,
    workId: string,
    batch: TaskLogBatch,
    signal: AbortSignal,
    ownerKind: string = "workflow",
    terminal = false,
  ): Promise<TaskLogUploadResult> {
    const body = {
      entries: batch.entries.map((entry) => ({
        seq: entry.seq,
        timestamp: entry.timestamp.toISOString(),
        source: entry.source,
        text: entry.text,
      })),
      truncated: batch.truncated,
      terminal,
    }
    const response = await fetch(this.taskLogUrl(ownerId, workId, ownerKind), {
      method: "POST",
      headers: { "content-type": "application/json", "x-mohist-runner-id": this.options.runnerId },
      body: JSON.stringify(body),
      signal,
    })
    const text = await response.text()
    let payload: Record<string, unknown> | null = null
    if (text) {
      try {
        payload = JSON.parse(text) as Record<string, unknown>
      } catch {
        payload = null
      }
    }
    if (!response.ok) {
      const errorMessage = extractErrorMessage(payload, text) ?? `task-log upload failed: ${response.status}`
      const error = new Error(errorMessage) as Error & { code?: string; status: number }
      error.status = response.status
      const code = readString(payload ?? {}, ["code"])
      if (code) error.code = code
      throw error
    }
    const data = readObject(payload, ["data"]) ?? payload ?? {}
    return {
      accepted: readNumber(data, ["accepted"]) ?? batch.entries.length,
      truncated: readBoolean(data, ["truncated"]) ?? batch.truncated,
    }
  }

  private taskLogUrl(ownerId: string, workId: string, ownerKind: string) {
    if (ownerKind === "agent-job") {
      return `${this.options.serverUrl.replace(/\/$/, "")}/api/agent-jobs/${encodeURIComponent(ownerId)}/work/${encodeURIComponent(workId)}/task-log`
    }

    return `${this.options.serverUrl.replace(/\/$/, "")}/api/workflow-runs/${encodeURIComponent(ownerId)}/work/${encodeURIComponent(workId)}/task-log`
  }

  async getWorkflowAgentSession(projectId: string, workflowRunId: string, sessionName: string, signal: AbortSignal): Promise<WorkflowAgentSession | null> {
    const response = await fetch(this.url(`sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(workflowRunId)}/${encodeURIComponent(sessionName)}`), { method: "GET", signal })
    if (response.status === 404) return null
    if (!response.ok) throw new Error(`session lookup failed: ${response.status} ${await response.text()}`)
    return response.json() as Promise<WorkflowAgentSession>
  }

  async openWorkflowAgentSession(projectId: string, workflowRunId: string, sessionName: string, body: unknown, signal: AbortSignal): Promise<WorkflowAgentSession> {
    const response = await fetch(this.url(`sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(workflowRunId)}/${encodeURIComponent(sessionName)}/open`), { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body), signal })
    if (!response.ok) throw new Error(`session open failed: ${response.status} ${await response.text()}`)
    return response.json() as Promise<WorkflowAgentSession>
  }

  async addTasks(workflowRunId: string, tasks: Array<{ id: string; title: string; uses?: string | null; with?: JsonObject | null }>) {
    const response = await fetch(`${this.options.serverUrl.replace(/\/$/, "")}/api/workflow-runs/${encodeURIComponent(workflowRunId)}/tasks/batch`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ tasks }),
    })
    if (!response.ok) throw new Error(`addTasks failed: ${response.status} ${await response.text()}`)
  }

  async patchRunVars(workflowRunId: string, vars: JsonObject, signal: AbortSignal) {
    const response = await fetch(`${this.options.serverUrl.replace(/\/$/, "")}/api/workflow-runs/${encodeURIComponent(workflowRunId)}/workflow-profile/variables`, {
      method: "PATCH",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ vars }),
      signal,
    })
    if (!response.ok) throw new Error(`patchRunVars failed: ${response.status} ${await response.text()}`)
  }

  async attachWorkflowAgentSession(projectId: string, workflowRunId: string, sessionName: string, body: unknown, signal: AbortSignal) {
    await this.post(`sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(workflowRunId)}/${encodeURIComponent(sessionName)}/attach`, body, signal)
  }

  async workflowAgentSessionRuntimeEvents(projectId: string, workflowRunId: string, sessionName: string, body: unknown, signal: AbortSignal) {
    await this.post(`sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(workflowRunId)}/${encodeURIComponent(sessionName)}/runtime-events`, body, signal)
  }

  async getAgentSession(projectId: string, sessionId: string, signal: AbortSignal): Promise<AgentSession | null> {
    const response = await fetch(this.url(`agent-sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(sessionId)}`), { method: "GET", signal })
    if (response.status === 404) return null
    if (!response.ok) throw new Error(`agent session lookup failed: ${response.status} ${await response.text()}`)
    return response.json() as Promise<AgentSession>
  }

  async openAgentSession(projectId: string, sessionId: string, body: unknown, signal: AbortSignal): Promise<AgentSession> {
    const response = await fetch(this.url(`agent-sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(sessionId)}/open`), { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body), signal })
    if (!response.ok) throw new Error(`agent session open failed: ${response.status} ${await response.text()}`)
    return response.json() as Promise<AgentSession>
  }

  async attachAgentSession(projectId: string, sessionId: string, body: unknown, signal: AbortSignal) {
    await this.post(`agent-sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(sessionId)}/attach`, body, signal)
  }

  async agentSessionRuntimeEvents(projectId: string, sessionId: string, body: unknown, signal: AbortSignal) {
    await this.post(`agent-sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(sessionId)}/runtime-events`, body, signal)
  }

  private async post(path: string, body: unknown, signal: AbortSignal) {
    const response = await fetch(this.url(path), { method: "POST", headers: body === undefined ? undefined : { "content-type": "application/json" }, body: body === undefined ? undefined : JSON.stringify(body), signal })
    if (!response.ok) throw new Error(`${path} failed: ${response.status} ${await response.text()}`)
  }

  private url(path: string) {
    return `${this.options.serverUrl.replace(/\/$/, "")}/api/runner/${encodeURIComponent(this.options.runnerId)}/${path}`
  }
}

export interface WorkflowAgentSession {
  runtimeSessionId?: string | null
  runtime?: string | null
  workDir?: string | null
  model?: string | null
  resolvedModel?: string | null
}

export type AgentSession = WorkflowAgentSession

function toWorkItem(dispatch: WorkDispatchResponse): RenderedWorkItem {
  const work: RenderedWorkItem = {
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
    epicNumber: dispatch.epicNumber ?? undefined,
    artifacts: parseObject(dispatch.artifacts),
    setVars: dispatch.setVars ? (parseObject(dispatch.setVars) as Record<string, string> | null) : null,
    ownerKind: dispatch.ownerKind ?? undefined,
    agentJobId: dispatch.agentJobId ?? undefined,
    agentSessionId: dispatch.agentSessionId ?? undefined,
    recovery: parseObject(dispatch.recovery),
  }
  if (Object.prototype.hasOwnProperty.call(dispatch, "recoveryRemaining"))
    work.recoveryRemaining = dispatch.recoveryRemaining
  return work
}

export interface ArtifactUploadRequest {
  path: string
  contentType?: string | null
  contentHash?: string | null
  size: number
  content: Uint8Array
  filename?: string
}

export interface ArtifactUploadResponse {
  uploadId: string
  workflowRunId: string
  workId: string
  taskRunId: string | null
  path: string
  contentType: string | null
  contentHash: string | null
  size: number
  createdAt: string | null
  expiresAt: string | null
  idempotent: boolean
}

export interface TaskLogUploadResult {
  accepted: number
  truncated: boolean
}

function readObject(value: unknown, path: string[]): Record<string, unknown> | null {
    const found = getSegments(value, path)
  return found && typeof found === "object" && !Array.isArray(found) ? (found as Record<string, unknown>) : null
}

function readString(value: unknown, path: string[]): string | null {
    const found = getSegments(value, path)
  return typeof found === "string" ? found : null
}

function readNumber(value: unknown, path: string[]): number | null {
    const found = getSegments(value, path)
  return typeof found === "number" && Number.isFinite(found) ? found : null
}

function readBoolean(value: unknown, path: string[]): boolean | null {
    const found = getSegments(value, path)
  return typeof found === "boolean" ? found : null
}

function extractErrorMessage(payload: Record<string, unknown> | null, fallback: string) {
  if (!payload) return null
  const data = readObject(payload, ["data"])
  if (data) {
    const message = readString(data, ["message"])
    if (message) return message
  }
  const error = readString(payload, ["error"])
  if (error) return error
  return null
}
