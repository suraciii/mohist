import { hostname } from 'node:os'
import type {
  CleanupPolicy,
  DispatchWorkItem,
  JsonObject,
  RunnerConfigResponse,
  RunnerOptions,
  RunnerRegistration,
  RuntimeReadinessWitness,
  WorkDispatchResponse,
  WorkItemResult,
  PolledDispatch,
} from '../core/types.js'
import type { BuildInfo } from '../runtime/build-info.js'
import { parseObject } from '../core/json.js'
import { getSegments } from '../core/json-path.js'
import type { TaskLogBatch } from '../runtime/task-log.js'
import type {
  PendingUpdateOperation,
  RecoveryReceiptAcknowledgement,
  RuntimeRecoveryReceipt,
} from '../runtime/recovery-receipt.js'
import { parseDispatchWorkItem } from './connection-dispatch.js'
import * as recoveryRequests from './connection.update-recovery.js'
import { reportWork } from './connection-report.js'
import { extractErrorMessage, RuntimeEventDeliveryError } from './connection-errors.js'
export { RuntimeEventDeliveryError } from './connection-errors.js'
import {
  getWorkspaceReclaimability as getWorkspaceReclaimabilityViaTransport,
  reportWorkspaceMaterialized as reportWorkspaceMaterializedViaTransport,
  type WorkspaceReportTransport,
} from './connection-workspaces.js'
import { currentRunnerTransport } from '../system/filesystem.js'
import type {
  AgentInputAttachmentContent,
  AgentSession,
  AgentSessionReconcileBinding,
  AgentSessionRuntimeEventAcceptance,
  AgentSessionRuntimeEventReceipt,
  WorkflowAgentSession,
} from './connection-session-models.js'

export type {
  AgentInputAttachmentContent,
  AgentSession,
  AgentSessionReconcileBinding,
  AgentSessionRuntimeEventAcceptance,
  AgentSessionRuntimeEventReceipt,
  WorkflowAgentSession,
} from './connection-session-models.js'

export class ServerConnection {
  private readonly buildGitHash: string | null
  private readonly buildInfo: BuildInfo | null
  private readonly credential: string | null
  readonly runnerId: string
  private managerDeploymentEpoch: string | null = null
  private lastPolledDispatches: PolledDispatch[] = []

  constructor(
    private readonly options: RunnerOptions,
    buildGitHash: string | null = null,
    buildInfo: BuildInfo | null = null,
  ) {
    this.buildGitHash = buildGitHash
    this.buildInfo = buildInfo
    this.credential = options.credential ?? null
    this.runnerId = options.runnerId
  }

  private async fetchWithAuth(input: string, init: RequestInit): Promise<Response> {
    const headers = new Headers(init.headers)
    if (this.credential) {
      headers.set('authorization', `Bearer ${this.credential}`)
    }
    return currentRunnerTransport()(input, { ...init, headers })
  }

  async connect(registration: RunnerRegistration, signal: AbortSignal) {
    await this.post('register', { hostname: hostname(), ...registration, ...this.identityPayload() }, signal)
  }

  async heartbeat(state: RunnerRegistration, signal: AbortSignal) {
    const response = await this.post('heartbeat', { hostname: hostname(), ...state, ...this.identityPayload() }, signal)
    this.observeDeploymentEpoch(response.headers.get('x-mohist-manager-deployment-epoch'))
  }

  private identityPayload(): Record<string, unknown> {
    return {
      buildGitHash: this.buildGitHash,
      component: this.buildInfo?.component ?? null,
      version: this.buildInfo?.version ?? null,
      sourceRevision: this.buildInfo?.sourceRevision ?? this.buildInfo?.gitHash ?? null,
      treeHash: this.buildInfo?.treeHash ?? null,
      artifactDigest: this.buildInfo?.artifactDigest ?? null,
      releaseId: this.buildInfo?.releaseId ?? null,
      generation: this.buildInfo?.generation ?? null,
      runnerId: this.buildInfo?.runnerId ?? this.options.runnerId,
    }
  }

  async disconnect(signal: AbortSignal) {
    await this.post('unregister', undefined, signal)
  }

  /** Current epoch observed from the latest Manager poll/heartbeat response. */
  get deploymentEpoch(): string | null {
    return this.managerDeploymentEpoch
  }

  /** Polls for work and retains the response-only grant view out of work items. */
  async poll(
    signal: AbortSignal,
    report: {
      processGeneration: string
      inFlight: string[]
      awaitingAck: string[]
      runtimeReadiness?: RuntimeReadinessWitness[]
      connectionId?: string | null
      admissionReady?: boolean
      deploymentEpoch?: string | null
    },
  ): Promise<DispatchWorkItem[]> {
    const polled = await this.pollWithGrants(signal, report)
    this.lastPolledDispatches = polled
    return polled.map((item) => item.work)
  }

  /**
   * Returns the grant-bearing view produced by the immediately preceding
   * `poll` call. This keeps the established poll seam usable by host fakes
   * while the grant remains outside DispatchWorkItem.
   */
  takeLastPolledDispatches(work: readonly DispatchWorkItem[]): PolledDispatch[] {
    if (this.lastPolledDispatches.length === 0) return work.map((item) => ({ work: item }))
    const byKey = new Map(this.lastPolledDispatches.map((item) => [dispatchKey(item.work), item]))
    this.lastPolledDispatches = []
    return work.map((item) => byKey.get(dispatchKey(item)) ?? { work: item })
  }

  async pollWithGrants(
    signal: AbortSignal,
    report: {
      processGeneration: string
      inFlight: string[]
      awaitingAck: string[]
      runtimeReadiness?: RuntimeReadinessWitness[]
      connectionId?: string | null
      admissionReady?: boolean
      deploymentEpoch?: string | null
    },
  ): Promise<PolledDispatch[]> {
    const response = await this.fetchWithAuth(this.url('poll'), {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(report),
      signal,
    })
    this.observeDeploymentEpoch(response.headers.get('x-mohist-manager-deployment-epoch'))
    if (response.status === 204) return []
    if (!response.ok) throw new Error(`poll failed: ${response.status} ${await response.text()}`)
    const payload = (await response.json()) as { dispatches?: WorkDispatchResponse[] }
    return (payload.dispatches ?? []).map((dispatch) => ({
      work: parseDispatchWorkItem(dispatch),
      ...(dispatch.managerExecutionGrant ? { managerExecutionGrant: dispatch.managerExecutionGrant } : {}),
      ...(dispatch.originMarker != null ? { originMarker: dispatch.originMarker } : {}),
    }))
  }

  async fetchPendingUpdateOperation(signal: AbortSignal): Promise<PendingUpdateOperation | null> {
    return recoveryRequests.fetchPendingUpdateOperation(this.fetchWithAuth.bind(this), this.url.bind(this), signal)
  }

  async sendRecoveryReceipt(
    receipt: RuntimeRecoveryReceipt,
    signal: AbortSignal,
  ): Promise<RecoveryReceiptAcknowledgement> {
    return recoveryRequests.sendRecoveryReceipt(this.fetchWithAuth.bind(this), this.url.bind(this), receipt, signal)
  }

  readonly reportRecoveryStopFailure = (
    failure: recoveryRequests.RecoveryStopFailure,
    signal: AbortSignal,
  ): Promise<void> =>
    recoveryRequests.reportRecoveryStopFailure(this.fetchWithAuth.bind(this), this.url.bind(this), failure, signal)

  async fetchConfig(signal: AbortSignal): Promise<CleanupPolicy | null> {
    const response = await this.fetchWithAuth(this.url('config'), { method: 'GET', signal })
    if (!response.ok) throw new Error(`fetchConfig failed: ${response.status} ${await response.text()}`)
    const payload = (await response.json()) as RunnerConfigResponse
    return payload.cleanupPolicy ?? null
  }

  async workflowRunsStatus(workflowRunIds: string[], signal: AbortSignal): Promise<Record<string, string>> {
    if (workflowRunIds.length === 0) return {}
    const response = await this.fetchWithAuth(this.url('workflow-runs/status'), {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ workflowRunIds }),
      signal,
    })
    if (!response.ok) throw new Error(`workflowRunsStatus failed: ${response.status} ${await response.text()}`)
    const payload = (await response.json()) as unknown
    const statuses = readObject(payload, ['statuses'])
    if (!statuses) return {}
    const result: Record<string, string> = {}
    for (const [key, value] of Object.entries(statuses)) {
      if (typeof value === 'string') result[key] = value
    }
    return result
  }

  async report(
    work: DispatchWorkItem,
    result: WorkItemResult,
    signal: AbortSignal,
    binding?: Pick<RuntimeRecoveryReceipt, 'agentSessionId' | 'agentTurnId' | 'runtime' | 'runtimeSessionId'>,
  ): Promise<Record<string, unknown>> {
    return await reportWork(this.fetchWithAuth.bind(this), this.url.bind(this), work, result, signal, binding)
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
    ownerKind = 'workflow',
  ): Promise<ArtifactUploadResponse> {
    const form = new FormData()
    form.set('path', upload.path)
    if (upload.contentType) form.set('contentType', upload.contentType)
    if (upload.contentHash) form.set('contentHash', upload.contentHash)
    form.set('size', String(upload.size))
    const view = new Uint8Array(upload.content.byteLength)
    view.set(upload.content)
    const blob = new Blob([view], { type: upload.contentType ?? 'application/octet-stream' })
    form.set('content', blob, upload.filename ?? 'artifact')
    const response = await this.fetchWithAuth(this.artifactUrl(ownerId, workId, ownerKind), {
      method: 'POST',
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
        const code = readString(payload, ['code'])
        if (code) error.code = code
        const uploadId =
          readString(payload, ['details', 'existingUploadId']) ??
          readString(payload, ['data', 'uploadId']) ??
          readString(payload, ['uploadId'])
        if (uploadId) error.uploadId = uploadId
      }
      throw error
    }
    const data = readObject(payload, ['data']) ?? payload ?? {}
    return {
      uploadId: readString(data, ['uploadId']) ?? '',
      workflowRunId: readString(data, ['workflowRunId']) ?? ownerId,
      workId: readString(data, ['workId']) ?? workId,
      taskRunId: readString(data, ['taskRunId']) ?? null,
      path: readString(data, ['path']) ?? upload.path,
      contentType: readString(data, ['contentType']) ?? upload.contentType ?? null,
      contentHash: readString(data, ['contentHash']) ?? upload.contentHash ?? null,
      size: readNumber(data, ['size']) ?? upload.size,
      createdAt: readString(data, ['createdAt']) ?? null,
      expiresAt: readString(data, ['expiresAt']) ?? null,
      idempotent: readBoolean(data, ['idempotent']) ?? false,
    }
  }

  private artifactUrl(ownerId: string, workId: string, ownerKind: string) {
    if (ownerKind === 'agent-job') {
      return `${this.options.serverUrl.replace(/\/$/, '')}/api/agent-jobs/${encodeURIComponent(ownerId)}/work/${encodeURIComponent(workId)}/artifact-uploads`
    }

    return `${this.options.serverUrl.replace(/\/$/, '')}/api/workflow-runs/${encodeURIComponent(ownerId)}/work/${encodeURIComponent(workId)}/artifact-uploads`
  }

  /**
   * Upload a task-log terminal batch to the dedicated, independent
   * task-log channel. Mirrors {@link uploadArtifact}'s routing shape
   * (owner-kind pair), but the body is JSON, not multipart, and the
   * server endpoint is a separate store (`TaskLogStore`) that does
   * not invoke any grain — the upload is decoupled from the report
   * call and from status adjudication.
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
    ownerKind: string = 'workflow',
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
    const response = await this.fetchWithAuth(this.taskLogUrl(ownerId, workId, ownerKind), {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'x-mohist-runner-id': this.options.runnerId },
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
      const code = readString(payload ?? {}, ['code'])
      if (code) error.code = code
      throw error
    }
    const data = readObject(payload, ['data']) ?? payload ?? {}
    const status = readString(data, ['status'])
    if (status !== 'changed' && status !== 'duplicate') {
      const error = new Error('task-log upload returned no terminal acknowledgement') as Error & {
        code?: string
        status: number
      }
      error.code = 'terminal_ack_missing'
      error.status = response.status
      throw error
    }
    return {
      status,
      accepted: readNumber(data, ['accepted']) ?? batch.entries.length,
      truncated: readBoolean(data, ['truncated']) ?? batch.truncated,
    }
  }

  private taskLogUrl(ownerId: string, workId: string, ownerKind: string) {
    if (ownerKind === 'agent-job') {
      return `${this.options.serverUrl.replace(/\/$/, '')}/api/agent-jobs/${encodeURIComponent(ownerId)}/work/${encodeURIComponent(workId)}/task-log`
    }

    return `${this.options.serverUrl.replace(/\/$/, '')}/api/workflow-runs/${encodeURIComponent(ownerId)}/work/${encodeURIComponent(workId)}/task-log`
  }

  async getWorkflowAgentSession(
    projectId: string,
    workflowRunId: string,
    sessionName: string,
    signal: AbortSignal,
  ): Promise<WorkflowAgentSession | null> {
    const response = await this.fetchWithAuth(
      this.url(
        `sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(workflowRunId)}/${encodeURIComponent(sessionName)}`,
      ),
      { method: 'GET', signal },
    )
    if (response.status === 404) return null
    if (!response.ok) throw new Error(`session lookup failed: ${response.status} ${await response.text()}`)
    return response.json() as Promise<WorkflowAgentSession>
  }

  async openWorkflowAgentSession(
    projectId: string,
    workflowRunId: string,
    sessionName: string,
    body: unknown,
    signal: AbortSignal,
  ): Promise<WorkflowAgentSession> {
    const response = await this.fetchWithAuth(
      this.url(
        `sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(workflowRunId)}/${encodeURIComponent(sessionName)}/open`,
      ),
      { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body), signal },
    )
    if (!response.ok) throw new Error(`session open failed: ${response.status} ${await response.text()}`)
    return response.json() as Promise<WorkflowAgentSession>
  }

  async addTasks(
    workflowRunId: string,
    tasks: Array<{
      id: string
      title: string
      uses?: string | null
      with?: JsonObject | null
      expect?: JsonObject | null
    }>,
  ) {
    const response = await this.fetchWithAuth(
      `${this.options.serverUrl.replace(/\/$/, '')}/api/workflow-runs/${encodeURIComponent(workflowRunId)}/tasks/batch`,
      {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ tasks }),
      },
    )
    if (!response.ok) throw new Error(`addTasks failed: ${response.status} ${await response.text()}`)
  }

  async patchRunVars(workflowRunId: string, vars: JsonObject, signal: AbortSignal) {
    const response = await this.fetchWithAuth(
      `${this.options.serverUrl.replace(/\/$/, '')}/api/workflow-runs/${encodeURIComponent(workflowRunId)}/variables`,
      {
        method: 'PATCH',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ vars }),
        signal,
      },
    )
    if (!response.ok) throw new Error(`patchRunVars failed: ${response.status} ${await response.text()}`)
  }

  async attachWorkflowAgentSession(
    projectId: string,
    workflowRunId: string,
    sessionName: string,
    body: unknown,
    signal: AbortSignal,
  ): Promise<WorkflowAgentSession> {
    const response = await this.fetchWithAuth(
      this.url(
        `sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(workflowRunId)}/${encodeURIComponent(sessionName)}/attach`,
      ),
      { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body), signal },
    )
    if (!response.ok) throw new Error(`session attach failed: ${response.status} ${await response.text()}`)
    return response.json() as Promise<WorkflowAgentSession>
  }

  async recoverMissingWorkflowAgentSession(
    projectId: string,
    workflowRunId: string,
    sessionName: string,
    body: unknown,
    signal: AbortSignal,
  ): Promise<WorkflowAgentSession> {
    const response = await this.fetchWithAuth(
      this.url(
        `sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(workflowRunId)}/${encodeURIComponent(sessionName)}/recover-missing`,
      ),
      { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body), signal },
    )
    if (!response.ok) throw new Error(`session missing recovery failed: ${response.status} ${await response.text()}`)
    return response.json() as Promise<WorkflowAgentSession>
  }

  async resetWorkflowAgentSession(
    projectId: string,
    workflowRunId: string,
    sessionName: string,
    body: unknown,
    signal: AbortSignal,
  ): Promise<WorkflowAgentSession> {
    const response = await this.fetchWithAuth(
      this.url(
        `sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(workflowRunId)}/${encodeURIComponent(sessionName)}/reset`,
      ),
      { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body), signal },
    )
    if (!response.ok) throw new Error(`session retry reset failed: ${response.status} ${await response.text()}`)
    return response.json() as Promise<WorkflowAgentSession>
  }

  async workflowAgentSessionCleanupTurn(
    projectId: string,
    workflowRunId: string,
    sessionName: string,
    body: unknown,
    signal: AbortSignal,
  ): Promise<AgentSessionRuntimeEventReceipt[]> {
    const response = await this.fetchWithAuth(
      this.url(
        `sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(workflowRunId)}/${encodeURIComponent(sessionName)}/cleanup-turn`,
      ),
      {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(body),
        signal,
      },
    )
    if (!response.ok) throw await this.runtimeEventDeliveryError('workflow cleanup turn', response)
    let payload: unknown
    try {
      payload = await response.json()
    } catch {
      throw new Error('workflow cleanup turn returned malformed JSON')
    }
    if (!Array.isArray(payload)) throw new Error('workflow cleanup turn returned a malformed receipt array')
    return payload.map((value) => {
      if (
        !isObjectRecord(value) ||
        typeof value.type !== 'string' ||
        value.type.length === 0 ||
        typeof value.cleanupOperationId !== 'string' ||
        value.cleanupOperationId.length === 0 ||
        typeof value.inputDeliveryId !== 'string' ||
        value.inputDeliveryId.length === 0 ||
        typeof value.agentTurnId !== 'string' ||
        value.agentTurnId.length === 0 ||
        typeof value.agentSessionId !== 'string' ||
        value.agentSessionId.length === 0
      ) {
        throw new Error('workflow cleanup turn returned a malformed receipt')
      }
      if (value.type !== 'session.cleanup') throw new Error('workflow cleanup turn returned an unexpected receipt type')
      const requestedOperationId = isObjectRecord(body) ? body.cleanupOperationId : null
      if (typeof requestedOperationId !== 'string' || requestedOperationId !== value.cleanupOperationId)
        throw new Error('workflow cleanup turn returned a mismatched operation identity')
      return {
        type: value.type,
        cleanupOperationId: value.cleanupOperationId,
        inputDeliveryId: value.inputDeliveryId,
        agentTurnId: value.agentTurnId,
        agentSessionId: value.agentSessionId,
      }
    })
  }

  async workflowAgentSessionRuntimeEvents(
    projectId: string,
    workflowRunId: string,
    sessionName: string,
    body: unknown,
    signal: AbortSignal,
  ): Promise<AgentSessionRuntimeEventAcceptance[]> {
    const response = await this.fetchWithAuth(
      this.url(
        `sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(workflowRunId)}/${encodeURIComponent(sessionName)}/runtime-events`,
      ),
      { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body), signal },
    )
    if (!response.ok) throw await this.runtimeEventDeliveryError('session runtime events', response)
    let payload: unknown
    try {
      payload = await response.json()
    } catch {
      throw new Error('session runtime events returned malformed JSON')
    }
    if (!Array.isArray(payload)) throw new Error('session runtime events returned a malformed acceptance response')
    const submitted = isObjectRecord(body) && Array.isArray(body.runtimeEvents) ? body.runtimeEvents.length : 0
    if (submitted > 0 && payload.length > 0 && payload.length !== submitted)
      throw new Error(`session runtime events acceptance mismatch: submitted ${submitted}, accepted ${payload.length}`)
    return payload as AgentSessionRuntimeEventAcceptance[]
  }

  async listAgentSessionsForReconcile(signal: AbortSignal): Promise<AgentSessionReconcileBinding[]> {
    const response = await this.fetchWithAuth(this.url('agent-sessions/reconcile'), { method: 'GET', signal })
    if (!response.ok)
      throw new Error(`agent session reconcile list failed: ${response.status} ${await response.text()}`)
    const payload = (await response.json()) as unknown
    if (!Array.isArray(payload)) throw new Error('agent session reconcile list returned a malformed response')
    return payload.map((value) => {
      if (
        !isObjectRecord(value) ||
        typeof value.sessionId !== 'string' ||
        value.sessionId.length === 0 ||
        (value.runtime !== 'opencode' && value.runtime !== 'pi') ||
        typeof value.runtimeSessionId !== 'string' ||
        value.runtimeSessionId.length === 0 ||
        typeof value.workDir !== 'string' ||
        value.workDir.length === 0
      ) {
        throw new Error('agent session reconcile list returned a malformed binding')
      }
      return {
        sessionId: value.sessionId,
        runtime: value.runtime,
        runtimeSessionId: value.runtimeSessionId,
        workDir: value.workDir,
      }
    })
  }

  async reconcileMissingAgentSession(
    sessionId: string,
    body: unknown,
    signal: AbortSignal,
  ): Promise<AgentSessionReconcileBinding> {
    const response = await this.fetchWithAuth(
      this.url(`agent-sessions/${encodeURIComponent(sessionId)}/reconcile-missing`),
      {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(body),
        signal,
      },
    )
    if (!response.ok)
      throw new Error(`agent session reconcile missing failed: ${response.status} ${await response.text()}`)
    return response.json() as Promise<AgentSessionReconcileBinding>
  }

  async reconcileAgentSessionRuntimeEvents(
    sessionId: string,
    body: unknown,
    signal: AbortSignal,
  ): Promise<AgentSessionRuntimeEventReceipt[]> {
    const response = await this.fetchWithAuth(
      this.url(`agent-sessions/${encodeURIComponent(sessionId)}/runtime-events`),
      {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(body),
        signal,
      },
    )
    if (!response.ok) throw await this.runtimeEventDeliveryError('agent session reconcile runtime events', response)
    return await parseRuntimeEventReceiptArray(response, 'agent session reconcile runtime events')
  }

  async getAgentSession(projectId: string, sessionId: string, signal: AbortSignal): Promise<AgentSession | null> {
    const response = await this.fetchWithAuth(
      this.url(`agent-sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(sessionId)}`),
      { method: 'GET', signal },
    )
    if (response.status === 404) return null
    if (!response.ok) throw new Error(`agent session lookup failed: ${response.status} ${await response.text()}`)
    return response.json() as Promise<AgentSession>
  }

  /**
   * Reports a materialized named workspace directory to the server
   * (`POST /api/runner/{runnerId}/workspaces/{projectId}/{workspaceName}/materialized`).
   * The server records the workspace home (first writer wins); a 409
   * `workspace_home_claimed` answer throws {@link WorkspaceHomeClaimedError}
   * so the dispatching runner can yield its local directory and fail the
   * dispatch (the job retries against the home runner).
   */
  async reportWorkspaceMaterialized(
    projectId: string,
    workspaceName: string,
    path: string,
    signal: AbortSignal,
  ): Promise<WorkspaceMaterializedReport> {
    return await reportWorkspaceMaterializedViaTransport(this.transport(), projectId, workspaceName, path, signal)
  }

  /**
   * Runner-scoped lifecycle observation for the named-workspace cleanup
   * guard (`GET /api/runner/{runnerId}/workspaces/{projectId}/{workspaceName}/reclaimable`).
   * The server is the lifecycle referee: the runner cannot know archive
   * state or bound-session activity locally, so each active entry is
   * probed against this endpoint before it may be promoted to eligible.
   */
  async getWorkspaceReclaimability(
    projectId: string,
    workspaceName: string,
    signal: AbortSignal,
  ): Promise<WorkspaceReclaimability> {
    return await getWorkspaceReclaimabilityViaTransport(
      this.transport(),
      (payload) => parseWorkspaceReclaimability(readObject(payload, ['data'])),
      projectId,
      workspaceName,
      signal,
    )
  }

  async openAgentSession(
    projectId: string,
    sessionId: string,
    body: unknown,
    signal: AbortSignal,
  ): Promise<AgentSession> {
    const response = await this.fetchWithAuth(
      this.url(`agent-sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(sessionId)}/open`),
      { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body), signal },
    )
    if (!response.ok) throw new Error(`agent session open failed: ${response.status} ${await response.text()}`)
    return response.json() as Promise<AgentSession>
  }

  async attachAgentSession(
    projectId: string,
    sessionId: string,
    body: unknown,
    signal: AbortSignal,
  ): Promise<AgentSession | null> {
    const response = await this.fetchWithAuth(
      this.url(`agent-sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(sessionId)}/attach`),
      {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(body),
        signal,
      },
    )
    if (!response.ok) throw new Error(`agent session attach failed: ${response.status} ${await response.text()}`)
    const text = await response.text()
    return text.length > 0 ? (JSON.parse(text) as AgentSession) : null
  }

  async recoverMissingAgentSession(
    projectId: string,
    sessionId: string,
    body: unknown,
    signal: AbortSignal,
  ): Promise<AgentSession> {
    const response = await this.fetchWithAuth(
      this.url(`agent-sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(sessionId)}/recover-missing`),
      {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(body),
        signal,
      },
    )
    if (!response.ok)
      throw new Error(`agent session missing recovery failed: ${response.status} ${await response.text()}`)
    return response.json() as Promise<AgentSession>
  }

  async agentSessionRuntimeEvents(
    projectId: string,
    sessionId: string,
    body: unknown,
    signal: AbortSignal,
  ): Promise<AgentSessionRuntimeEventReceipt[]> {
    const response = await this.fetchWithAuth(
      this.url(`agent-sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(sessionId)}/runtime-events`),
      {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(body),
        signal,
      },
    )
    if (!response.ok)
      throw await this.runtimeEventDeliveryError(
        `agent-sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(sessionId)}/runtime-events`,
        response,
      )
    return await parseRuntimeEventReceiptArray(response, 'agent session runtime events')
  }

  /**
   * Fetch an accepted attachment's bytes through the owning
   * SessionInput's scoped content route. The server only serves the
   * content when the attachment's owner matches the supplied session +
   * input id; a mismatch (or a missing / expired / unreadable row)
   * surfaces as `null` so the caller can render an honest
   * "unavailable" status without leaking the request URL into the
   * transcript.
   *
   * Issue-513: the runner never reaches this surface via caller
   * temp URLs, tokens, or raw platform event payloads — the wire
   * identity is the runner's existing server connection plus the
   * owning `agentSessionId` + `inputId` carried on the dispatch
   * envelope.
   */
  async openAgentInputAttachment(
    projectId: string,
    agentSessionId: string,
    inputId: string,
    attachmentId: string,
    signal: AbortSignal,
  ): Promise<AgentInputAttachmentContent | null> {
    const response = await this.fetchWithAuth(
      this.agentInputAttachmentContentUrl(projectId, agentSessionId, inputId, attachmentId),
      {
        method: 'GET',
        signal,
      },
    )
    if (response.status === 404) return null
    if (!response.ok)
      throw new Error(`agent-input attachment content failed: ${response.status} ${await response.text()}`)
    const bytes = new Uint8Array(await response.arrayBuffer())
    const contentType = response.headers.get('content-type')
    const contentDisposition = response.headers.get('content-disposition')
    return {
      bytes,
      contentType,
      contentDisposition,
    }
  }

  private agentInputAttachmentContentUrl(
    projectId: string,
    agentSessionId: string,
    inputId: string,
    attachmentId: string,
  ): string {
    return `${this.options.serverUrl.replace(/\/$/, '')}/api/projects/${encodeURIComponent(projectId)}/agent-sessions/${encodeURIComponent(agentSessionId)}/inputs/${encodeURIComponent(inputId)}/attachments/${encodeURIComponent(attachmentId)}/content`
  }

  private observeDeploymentEpoch(value: string | null): void {
    if (value && value.length > 0) this.managerDeploymentEpoch = value
  }

  async revokeManagerExecution(executionId: string, signal: AbortSignal): Promise<void> {
    if (!executionId) return
    const response = await this.fetchWithAuth(
      this.url(`manager-executions/${encodeURIComponent(executionId)}/revoke`),
      { method: 'POST', signal },
    )
    if (!response.ok) throw new Error(`Manager execution revocation failed: ${response.status}`)
  }

  private async post(path: string, body: unknown, signal: AbortSignal): Promise<Response> {
    const response = await this.fetchWithAuth(this.url(path), {
      method: 'POST',
      headers: body === undefined ? undefined : { 'content-type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
      signal,
    })
    if (!response.ok) throw new Error(`${path} failed: ${response.status} ${await response.text()}`)
    return response
  }

  private async runtimeEventDeliveryError(operation: string, response: Response): Promise<RuntimeEventDeliveryError> {
    const body = await response.text()
    let code: string | null = null
    try {
      const payload = JSON.parse(body) as unknown
      if (isObjectRecord(payload) && typeof payload.code === 'string') code = payload.code
    } catch {
      // The status remains useful when the Server did not return JSON.
    }
    return new RuntimeEventDeliveryError(operation, response.status, code, body)
  }

  private transport(): WorkspaceReportTransport {
    return { fetchWithAuth: (input, init) => this.fetchWithAuth(input, init), url: (path) => this.url(path) }
  }

  private url(path: string) {
    return `${this.options.serverUrl.replace(/\/$/, '')}/api/runner/${encodeURIComponent(this.options.runnerId)}/${path}`
  }
}

function dispatchKey(work: DispatchWorkItem): string {
  const ownerKind = work.ownerKind ?? 'workflow'
  const ownerId = ownerKind === 'agent-job' ? (work.agentJobId ?? '') : work.workflowRunId
  return `${ownerKind}:${ownerId}:${work.workId}`
}

async function parseRuntimeEventReceiptArray(
  response: Response,
  operation: string,
): Promise<AgentSessionRuntimeEventReceipt[]> {
  let payload: unknown
  try {
    payload = await response.json()
  } catch {
    throw new Error(`${operation} returned malformed JSON`)
  }
  if (!Array.isArray(payload)) throw new Error(`${operation} returned a malformed receipt array`)
  return payload as AgentSessionRuntimeEventReceipt[]
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
  status: 'changed' | 'duplicate'
  accepted: number
  truncated: boolean
}

function readObject(value: unknown, path: string[]): Record<string, unknown> | null {
  const found = getSegments(value, path)
  return found && typeof found === 'object' && !Array.isArray(found) ? (found as Record<string, unknown>) : null
}

function isObjectRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function readString(value: unknown, path: string[]): string | null {
  const found = getSegments(value, path)
  return typeof found === 'string' ? found : null
}

function readNumber(value: unknown, path: string[]): number | null {
  const found = getSegments(value, path)
  return typeof found === 'number' && Number.isFinite(found) ? found : null
}

function readBoolean(value: unknown, path: string[]): boolean | null {
  const found = getSegments(value, path)
  return typeof found === 'boolean' ? found : null
}

/**
 * Answer shape for
 * `POST /api/runner/{runnerId}/workspaces/{projectId}/{workspaceName}/materialized`.
 * `runnerId` is the workspace home runner recorded by the server (this runner on success).
 */
export interface WorkspaceMaterializedReport {
  readonly runnerId: string
  readonly path: string
}

/**
 * Answer shape for
 * `GET /api/runner/{runnerId}/workspaces/{projectId}/{workspaceName}/reclaimable`.
 * `status` is the Workspace lifecycle status; `activeBoundSessions` counts
 * sessions bound to and actively using the workspace.
 */
export interface WorkspaceReclaimability {
  readonly status: 'active' | 'archived'
  readonly activeBoundSessions: number
}

export function parseWorkspaceReclaimability(payload: unknown): WorkspaceReclaimability {
  if (!isObjectRecord(payload)) throw new Error('workspace reclaimability returned a malformed response')
  const status = readString(payload, ['status'])
  if (status !== 'active' && status !== 'archived') {
    throw new Error('workspace reclaimability returned an unknown status')
  }
  const count = readNumber(payload, ['activeBoundSessions'])
  if (count === null || !Number.isInteger(count) || count < 0) {
    throw new Error('workspace reclaimability returned an invalid session count')
  }
  return { status, activeBoundSessions: count }
}
