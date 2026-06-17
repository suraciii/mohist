import { hostname } from "node:os"
import type { RunnerOptions, RunnerRegistration, WorkDispatchResponse, WorkItem, WorkItemResult } from "../core/types.js"
import { parseObject, parseTaskOutputs } from "../core/json.js"

export class ServerConnection {
  private readonly buildGitHash: string | null

  constructor(private readonly options: RunnerOptions, buildGitHash: string | null = null) {
    this.buildGitHash = buildGitHash
  }

  async connect(registration: RunnerRegistration, signal: AbortSignal) {
    await this.post("register", { hostname: hostname(), ...registration }, signal)
  }

  async heartbeat(signal: AbortSignal) {
    if (this.buildGitHash) {
      await this.post("heartbeat", { buildGitHash: this.buildGitHash }, signal)
      return
    }
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
    const response = await fetch(this.url("report"), {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({
        workflowRunId: work.workflowRunId,
        workId: work.workId,
        projectId: work.projectId,
        status: result.status,
        message: result.message,
        output: result.output,
        exitCode: result.exitCode,
        artifactUploadIds: result.artifactUploadIds ?? null,
        capturedOutputs: result.capturedOutputs ?? null,
        cleanupAttempts: result.cleanupAttempts ?? null,
      }),
      signal,
    })
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
    workflowRunId: string,
    workId: string,
    upload: ArtifactUploadRequest,
    signal: AbortSignal,
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
    const response = await fetch(this.artifactUrl(workflowRunId, workId), {
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
      workflowRunId: readString(data, ["workflowRunId"]) ?? workflowRunId,
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

  private artifactUrl(workflowRunId: string, workId: string) {
    return `${this.options.serverUrl.replace(/\/$/, "")}/api/workflow-runs/${encodeURIComponent(workflowRunId)}/work/${encodeURIComponent(workId)}/artifact-uploads`
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

export interface WorkflowAgentSession {
  acpSessionId?: string | null
  workDir?: string | null
  model?: string | null
  resolvedModel?: string | null
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
    artifacts: parseObject(dispatch.artifacts),
    outputs: parseTaskOutputs(dispatch.outputs),
  }
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

function readObject(value: unknown, path: string[]): Record<string, unknown> | null {
  const found = readValueAt(value, path)
  return found && typeof found === "object" && !Array.isArray(found) ? (found as Record<string, unknown>) : null
}

function readString(value: unknown, path: string[]): string | null {
  const found = readValueAt(value, path)
  return typeof found === "string" ? found : null
}

function readNumber(value: unknown, path: string[]): number | null {
  const found = readValueAt(value, path)
  return typeof found === "number" && Number.isFinite(found) ? found : null
}

function readBoolean(value: unknown, path: string[]): boolean | null {
  const found = readValueAt(value, path)
  return typeof found === "boolean" ? found : null
}

function readValueAt(value: unknown, path: string[]): unknown {
  let current: unknown = value
  for (const part of path) {
    if (current === null || typeof current !== "object" || Array.isArray(current)) return undefined
    current = (current as Record<string, unknown>)[part]
  }
  return current
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
