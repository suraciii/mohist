import { hostname } from 'node:os'
import type {
  AgentExecutionBinding,
  CleanupPolicy,
  DispatchReportOwner,
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
import { getSegments } from '../core/json-path.js'
import type { TaskLogBatch } from '../runtime/task-log.js'
import { parsePolledDispatch } from './connection-dispatch.js'
import { reportWork } from './connection-report.js'
import { RunnerTransportError } from './connection-errors.js'
import { createRunnerProtocolError, RunnerTransport, type RunnerRequestTransport } from './connection-transport.js'
export {
  isConfirmedRunnerCredentialRejection,
  runnerTransportDiagnostics,
  RUNNER_REENROLL_ACTION,
  withRunnerEnrollmentGuidance,
  RunnerTransportError,
  type RunnerTransportDiagnosticOptions,
  type RunnerTransportErrorKind,
  type RunnerTransportErrorOptions,
} from './connection-errors.js'
export {
  createRunnerProtocolError,
  RunnerTransport,
  type RunnerRequestOptions,
  type RunnerRequestTransport,
  type RunnerTransportOptions,
} from './connection-transport.js'
import {
  getWorkspaceReclaimability as getWorkspaceReclaimabilityViaTransport,
  reportWorkspaceMaterialized as reportWorkspaceMaterializedViaTransport,
  type WorkspaceMaterializedReport,
  type WorkspaceReclaimability,
  type WorkspaceReportTransport,
} from './connection-workspaces.js'
export {
  parseWorkspaceReclaimability,
  type WorkspaceMaterializedReport,
  type WorkspaceReclaimability,
} from './connection-workspaces.js'
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
  private readonly requestTransport: RunnerRequestTransport
  readonly runnerId: string
  private managerDeploymentEpoch: string | null = null

  constructor(
    private readonly options: RunnerOptions,
    buildGitHash: string | null = null,
    buildInfo: BuildInfo | null = null,
  ) {
    this.buildGitHash = buildGitHash
    this.buildInfo = buildInfo
    this.credential = options.credential ?? null
    this.requestTransport = new RunnerTransport({ credential: this.credential })
    this.runnerId = options.runnerId
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

  /**
   * Polls for work and returns the grant-bearing view directly. The work
   * item and its one-shot Manager grant/origin metadata travel together
   * through the same wire response, so consumers cannot accidentally
   * drop the grant by forgetting a follow-up getter call.
   */
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
  ): Promise<PolledDispatch[]> {
    const response = await this.requestTransport.request('poll', this.url('poll'), {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(report),
      signal,
    })
    this.observeDeploymentEpoch(response.headers.get('x-mohist-manager-deployment-epoch'))
    if (response.status === 204) return []
    const payload = await this.requestTransport.readJson<unknown>(response, 'poll')
    if (!isObjectRecord(payload) || !Array.isArray(payload.dispatches)) {
      throw createRunnerProtocolError('poll', 'returned a malformed dispatch envelope')
    }
    try {
      return payload.dispatches.map((dispatch) => parsePolledDispatch(dispatch as WorkDispatchResponse))
    } catch (cause) {
      if (cause instanceof RunnerTransportError) throw cause
      throw createRunnerProtocolError('poll', 'returned a malformed dispatch envelope')
    }
  }

  async fetchConfig(signal: AbortSignal): Promise<CleanupPolicy | null> {
    const response = await this.requestTransport.request('fetchConfig', this.url('config'), {
      method: 'GET',
      signal,
    })
    const payload = await this.requestTransport.readJson<RunnerConfigResponse>(response, 'fetchConfig')
    return payload?.cleanupPolicy ?? null
  }

  async workflowRunsStatus(workflowRunIds: string[], signal: AbortSignal): Promise<Record<string, string>> {
    if (workflowRunIds.length === 0) return {}
    const response = await this.requestTransport.request('workflowRunsStatus', this.url('workflow-runs/status'), {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ workflowRunIds }),
      signal,
    })
    const payload = await this.requestTransport.readJson<unknown>(response, 'workflowRunsStatus')
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
    binding?: AgentExecutionBinding,
    reportOwner?: DispatchReportOwner,
  ): Promise<Record<string, unknown>> {
    return await reportWork(this.requestTransport, this.url.bind(this), work, result, signal, binding, reportOwner)
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
    const blob = new Blob([view], {
      type: upload.contentType ?? 'application/octet-stream',
    })
    form.set('content', blob, upload.filename ?? 'artifact')
    const response = await this.requestTransport.request(
      'uploadArtifact',
      this.artifactUrl(ownerId, workId, ownerKind),
      {
        method: 'POST',
        body: form,
        signal,
      },
    )
    const payload = await this.requestTransport.readJson<Record<string, unknown>>(response, 'uploadArtifact')
    const data = readObject(payload, ['data']) ?? (isObjectRecord(payload) ? payload : null)
    if (!data) throw createRunnerProtocolError('uploadArtifact', 'returned a malformed response')
    const uploadId = readString(data, ['uploadId'])
    if (!uploadId) throw createRunnerProtocolError('uploadArtifact', 'returned a response without an upload id')
    return {
      uploadId,
      workflowRunId: readString(data, ['workflowRunId']) ?? ownerId,
      workId: readString(data, ['workId']) ?? workId,
      actionAttemptId: readString(data, ['actionAttemptId']) ?? null,
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
    const response = await this.requestTransport.request('uploadTaskLog', this.taskLogUrl(ownerId, workId, ownerKind), {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        'x-mohist-runner-id': this.options.runnerId,
      },
      body: JSON.stringify(body),
      signal,
    })
    const payload = await this.requestTransport.readJson<Record<string, unknown>>(response, 'uploadTaskLog')
    const data = readObject(payload, ['data']) ?? (isObjectRecord(payload) ? payload : null)
    if (!data) throw createRunnerProtocolError('uploadTaskLog', 'returned a malformed response')
    const status = readString(data, ['status'])
    if (status !== 'changed' && status !== 'duplicate') {
      throw createRunnerProtocolError(
        'uploadTaskLog',
        'returned no terminal acknowledgement',
        undefined,
        'terminal_ack_missing',
      )
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
    const response = await this.requestTransport.request(
      'getWorkflowAgentSession',
      this.url(
        `sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(workflowRunId)}/${encodeURIComponent(sessionName)}`,
      ),
      { method: 'GET', signal },
      { allowedStatuses: [404] },
    )
    if (response.status === 404) return null
    return requireWorkflowSessionPayload(
      await this.requestTransport.readJson<unknown>(response, 'getWorkflowAgentSession'),
      'getWorkflowAgentSession',
    )
  }

  async openWorkflowAgentSession(
    projectId: string,
    workflowRunId: string,
    sessionName: string,
    body: unknown,
    signal: AbortSignal,
  ): Promise<WorkflowAgentSession> {
    const response = await this.requestTransport.request(
      'openWorkflowAgentSession',
      this.url(
        `sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(workflowRunId)}/${encodeURIComponent(sessionName)}/open`,
      ),
      {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(body),
        signal,
      },
    )
    return requireWorkflowSessionPayload(
      await this.requestTransport.readJson<unknown>(response, 'openWorkflowAgentSession'),
      'openWorkflowAgentSession',
    )
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
    await this.requestTransport.request(
      'addTasks',
      `${this.options.serverUrl.replace(/\/$/, '')}/api/workflow-runs/${encodeURIComponent(workflowRunId)}/tasks/batch`,
      {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ tasks }),
      },
    )
  }

  async patchRunVars(workflowRunId: string, vars: JsonObject, signal: AbortSignal) {
    await this.requestTransport.request(
      'patchRunVars',
      `${this.options.serverUrl.replace(/\/$/, '')}/api/workflow-runs/${encodeURIComponent(workflowRunId)}/variables`,
      {
        method: 'PATCH',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ vars }),
        signal,
      },
    )
  }

  async attachWorkflowAgentSession(
    projectId: string,
    workflowRunId: string,
    sessionName: string,
    body: unknown,
    signal: AbortSignal,
  ): Promise<WorkflowAgentSession> {
    const response = await this.requestTransport.request(
      'attachWorkflowAgentSession',
      this.url(
        `sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(workflowRunId)}/${encodeURIComponent(sessionName)}/attach`,
      ),
      {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(body),
        signal,
      },
    )
    return requireWorkflowSessionPayload(
      await this.requestTransport.readJson<unknown>(response, 'attachWorkflowAgentSession'),
      'attachWorkflowAgentSession',
    )
  }

  async recoverMissingWorkflowAgentSession(
    projectId: string,
    workflowRunId: string,
    sessionName: string,
    body: unknown,
    signal: AbortSignal,
  ): Promise<WorkflowAgentSession> {
    const response = await this.requestTransport.request(
      'recoverMissingWorkflowAgentSession',
      this.url(
        `sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(workflowRunId)}/${encodeURIComponent(sessionName)}/recover-missing`,
      ),
      {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(body),
        signal,
      },
    )
    return requireWorkflowSessionPayload(
      await this.requestTransport.readJson<unknown>(response, 'recoverMissingWorkflowAgentSession'),
      'recoverMissingWorkflowAgentSession',
    )
  }

  async resetWorkflowAgentSession(
    projectId: string,
    workflowRunId: string,
    sessionName: string,
    body: unknown,
    signal: AbortSignal,
  ): Promise<WorkflowAgentSession> {
    const response = await this.requestTransport.request(
      'resetWorkflowAgentSession',
      this.url(
        `sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(workflowRunId)}/${encodeURIComponent(sessionName)}/reset`,
      ),
      {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(body),
        signal,
      },
    )
    return requireWorkflowSessionPayload(
      await this.requestTransport.readJson<unknown>(response, 'resetWorkflowAgentSession'),
      'resetWorkflowAgentSession',
    )
  }

  async workflowAgentSessionCleanupTurn(
    projectId: string,
    workflowRunId: string,
    sessionName: string,
    body: unknown,
    signal: AbortSignal,
  ): Promise<AgentSessionRuntimeEventReceipt[]> {
    const response = await this.requestTransport.request(
      'workflowAgentSessionCleanupTurn',
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
    const payload = await this.requestTransport.readJson<unknown>(response, 'workflowAgentSessionCleanupTurn')
    if (!Array.isArray(payload)) {
      throw createRunnerProtocolError('workflowAgentSessionCleanupTurn', 'returned a malformed receipt array')
    }
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
        throw createRunnerProtocolError('workflowAgentSessionCleanupTurn', 'returned a malformed receipt')
      }
      if (value.type !== 'session.cleanup') {
        throw createRunnerProtocolError('workflowAgentSessionCleanupTurn', 'returned an unexpected receipt type')
      }
      const requestedOperationId = isObjectRecord(body) ? body.cleanupOperationId : null
      if (typeof requestedOperationId !== 'string' || requestedOperationId !== value.cleanupOperationId) {
        throw createRunnerProtocolError('workflowAgentSessionCleanupTurn', 'returned a mismatched operation identity')
      }
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
    const response = await this.requestTransport.request(
      'workflowAgentSessionRuntimeEvents',
      this.url(
        `sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(workflowRunId)}/${encodeURIComponent(sessionName)}/runtime-events`,
      ),
      {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(body),
        signal,
      },
    )
    const payload = await parseRuntimeEventReceiptArray(
      this.requestTransport,
      response,
      'workflowAgentSessionRuntimeEvents',
    )
    const submitted = isObjectRecord(body) && Array.isArray(body.runtimeEvents) ? body.runtimeEvents.length : 0
    if (submitted > 0 && payload.length > 0 && payload.length !== submitted) {
      throw createRunnerProtocolError(
        'workflowAgentSessionRuntimeEvents',
        `returned an acceptance mismatch: submitted ${submitted}, accepted ${payload.length}`,
      )
    }
    return payload as AgentSessionRuntimeEventAcceptance[]
  }

  async listAgentSessionsForReconcile(signal: AbortSignal): Promise<AgentSessionReconcileBinding[]> {
    const response = await this.requestTransport.request(
      'listAgentSessionsForReconcile',
      this.url('agent-sessions/reconcile'),
      { method: 'GET', signal },
    )
    const payload = await this.requestTransport.readJson<unknown>(response, 'listAgentSessionsForReconcile')
    if (!Array.isArray(payload)) {
      throw createRunnerProtocolError('listAgentSessionsForReconcile', 'returned a malformed response')
    }
    return payload.map((value) => parseAgentSessionReconcileBinding(value, 'listAgentSessionsForReconcile'))
  }

  async reconcileMissingAgentSession(
    sessionId: string,
    body: unknown,
    signal: AbortSignal,
  ): Promise<AgentSessionReconcileBinding> {
    const response = await this.requestTransport.request(
      'reconcileMissingAgentSession',
      this.url(`agent-sessions/${encodeURIComponent(sessionId)}/reconcile-missing`),
      {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(body),
        signal,
      },
    )
    return parseAgentSessionReconcileBinding(
      await this.requestTransport.readJson<unknown>(response, 'reconcileMissingAgentSession'),
      'reconcileMissingAgentSession',
    )
  }

  async reconcileAgentSessionRuntimeEvents(
    sessionId: string,
    body: unknown,
    signal: AbortSignal,
  ): Promise<AgentSessionRuntimeEventReceipt[]> {
    const response = await this.requestTransport.request(
      'reconcileAgentSessionRuntimeEvents',
      this.url(`agent-sessions/${encodeURIComponent(sessionId)}/runtime-events`),
      {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(body),
        signal,
      },
    )
    return await parseRuntimeEventReceiptArray(this.requestTransport, response, 'reconcileAgentSessionRuntimeEvents')
  }

  async getAgentSession(projectId: string, sessionId: string, signal: AbortSignal): Promise<AgentSession | null> {
    const response = await this.requestTransport.request(
      'getAgentSession',
      this.url(`agent-sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(sessionId)}`),
      { method: 'GET', signal },
      { allowedStatuses: [404] },
    )
    if (response.status === 404) return null
    return requireGenericSessionPayload(
      await this.requestTransport.readJson<unknown>(response, 'getAgentSession'),
      'getAgentSession',
    )
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
    return await getWorkspaceReclaimabilityViaTransport(this.transport(), projectId, workspaceName, signal)
  }

  async openAgentSession(
    projectId: string,
    sessionId: string,
    body: unknown,
    signal: AbortSignal,
  ): Promise<AgentSession> {
    const response = await this.requestTransport.request(
      'openAgentSession',
      this.url(`agent-sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(sessionId)}/open`),
      {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(body),
        signal,
      },
    )
    return requireGenericSessionPayload(
      await this.requestTransport.readJson<unknown>(response, 'openAgentSession'),
      'openAgentSession',
    )
  }

  async attachAgentSession(
    projectId: string,
    sessionId: string,
    body: unknown,
    signal: AbortSignal,
  ): Promise<AgentSession | null> {
    const response = await this.requestTransport.request(
      'attachAgentSession',
      this.url(`agent-sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(sessionId)}/attach`),
      {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(body),
        signal,
      },
    )
    const payload = await this.requestTransport.readJson<unknown>(response, 'attachAgentSession', true)
    return payload === null ? null : requireGenericSessionPayload(payload, 'attachAgentSession')
  }

  async recoverMissingAgentSession(
    projectId: string,
    sessionId: string,
    body: unknown,
    signal: AbortSignal,
  ): Promise<AgentSession> {
    const response = await this.requestTransport.request(
      'recoverMissingAgentSession',
      this.url(`agent-sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(sessionId)}/recover-missing`),
      {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(body),
        signal,
      },
    )
    return requireGenericSessionPayload(
      await this.requestTransport.readJson<unknown>(response, 'recoverMissingAgentSession'),
      'recoverMissingAgentSession',
    )
  }

  async agentSessionRuntimeEvents(
    projectId: string,
    sessionId: string,
    body: unknown,
    signal: AbortSignal,
  ): Promise<AgentSessionRuntimeEventReceipt[]> {
    const response = await this.requestTransport.request(
      'agentSessionRuntimeEvents',
      this.url(`agent-sessions/${encodeURIComponent(projectId)}/${encodeURIComponent(sessionId)}/runtime-events`),
      {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(body),
        signal,
      },
    )
    return await parseRuntimeEventReceiptArray(this.requestTransport, response, 'agentSessionRuntimeEvents')
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
    const response = await this.requestTransport.request(
      'openAgentInputAttachment',
      this.agentInputAttachmentContentUrl(projectId, agentSessionId, inputId, attachmentId),
      { method: 'GET', signal },
      { allowedStatuses: [404] },
    )
    if (response.status === 404) return null
    const bytes = await this.requestTransport.readBytes(response, 'openAgentInputAttachment')
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
    await this.requestTransport.request(
      'revokeManagerExecution',
      this.url(`manager-executions/${encodeURIComponent(executionId)}/revoke`),
      { method: 'POST', signal },
    )
  }

  private async post(path: string, body: unknown, signal: AbortSignal): Promise<Response> {
    return await this.requestTransport.request(path, this.url(path), {
      method: 'POST',
      headers: body === undefined ? undefined : { 'content-type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
      signal,
    })
  }

  private transport(): WorkspaceReportTransport {
    return {
      request: this.requestTransport.request.bind(this.requestTransport),
      readJson: this.requestTransport.readJson.bind(this.requestTransport),
      url: (path) => this.url(path),
    }
  }

  private url(path: string) {
    return `${this.options.serverUrl.replace(/\/$/, '')}/api/runner/${encodeURIComponent(this.options.runnerId)}/${path}`
  }
}

async function parseRuntimeEventReceiptArray(
  transport: RunnerRequestTransport,
  response: Response,
  operation: string,
): Promise<AgentSessionRuntimeEventReceipt[]> {
  const payload = await transport.readJson<unknown>(response, operation)
  if (!Array.isArray(payload)) throw createRunnerProtocolError(operation, 'returned a malformed receipt array')
  for (const receipt of payload) {
    if (!isObjectRecord(receipt) || typeof receipt.type !== 'string' || receipt.type.length === 0) {
      throw createRunnerProtocolError(operation, 'returned a malformed receipt')
    }
  }
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
  actionAttemptId: string | null
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

function requireWorkflowSessionPayload(value: unknown, operation: string): WorkflowAgentSession {
  if (!isObjectRecord(value) || !nonEmptyString(value.sessionId)) {
    throw createRunnerProtocolError(operation, 'returned a malformed session payload')
  }
  return value as unknown as WorkflowAgentSession
}

function requireGenericSessionPayload(value: unknown, operation: string): AgentSession {
  if (!isObjectRecord(value)) throw createRunnerProtocolError(operation, 'returned a malformed session payload')
  if ('sessionId' in value && !nonEmptyString(value.sessionId)) {
    throw createRunnerProtocolError(operation, 'returned a malformed session payload')
  }
  if (!nonEmptyString(value.sessionId) && !nonEmptyString(value.runtimeSessionId) && !nonEmptyString(value.status)) {
    throw createRunnerProtocolError(operation, 'returned a malformed session payload')
  }
  return value as unknown as AgentSession
}

function nonEmptyString(value: unknown): value is string {
  return typeof value === 'string' && value.trim().length > 0
}

function parseAgentSessionReconcileBinding(value: unknown, operation: string): AgentSessionReconcileBinding {
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
    throw createRunnerProtocolError(operation, 'returned a malformed binding')
  }
  return {
    sessionId: value.sessionId,
    runtime: value.runtime,
    runtimeSessionId: value.runtimeSessionId,
    workDir: value.workDir,
  }
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
