import { request, projectApiPath } from '../../../shared/api/client'
import { createIdempotencyKey } from '../../../shared/lib/idempotency-key'
import type {
  AgentSessionEvent,
  AgentSessionMetadata,
  AgentSessionTranscriptResponse,
  CoderSessionSummary,
  WorkflowRunSession,
} from '../model/types'

export function getCoderSessions(number: number, projectId?: string | null, signal?: AbortSignal) {
  return request<CoderSessionSummary[]>(projectApiPath(projectId, `/issues/${number}/coder-sessions`), { signal })
}

export function getWorkflowRunSessions(workflowRunId: string) {
  return request<WorkflowRunSession[]>(`/workflow-runs/${encodeURIComponent(workflowRunId)}/sessions`)
}

export function getAgentSessionMetadata(number: number, name: string, projectId?: string | null) {
  return request<AgentSessionMetadata>(
    projectApiPath(projectId, `/issues/${number}/sessions/${encodeURIComponent(name)}`),
  )
}

export function getAgentSessionTranscript(
  number: number,
  name: string,
  projectId?: string | null,
  runtimeSessionId?: string | null,
) {
  const search = runtimeSessionId
    ? `?${new URLSearchParams({ runtimeSessionId }).toString()}`
    : ''
  return request<AgentSessionTranscriptResponse>(
    projectApiPath(projectId, `/issues/${number}/sessions/${encodeURIComponent(name)}/transcript${search}`),
  )
}

export function getAgentSessionEvents(number: number, name: string, projectId?: string | null) {
  return request<{ events: AgentSessionEvent[] }>(
    projectApiPath(projectId, `/issues/${number}/sessions/${encodeURIComponent(name)}/events`),
  )
}

export interface SessionRecoveryResult {
  id: string
  status: string
  contextWindowSize?: number | null
  contextWindowUsed?: number | null
  contextUsagePercent?: number | null
  contextWindowUsedBefore?: number | null
  operation?: string | null
  wasCompacted: boolean
}

export function compactSession(
  number: number,
  name: string,
  projectId?: string | null,
  idempotencyKey?: string,
): Promise<SessionRecoveryResult> {
  return request<SessionRecoveryResult>(
    projectApiPath(projectId, `/issues/${number}/sessions/${encodeURIComponent(name)}/compact`),
    { method: 'POST', headers: idempotencyKey ? { 'Idempotency-Key': idempotencyKey } : undefined },
  )
}

export function resetSession(
  number: number,
  name: string,
  projectId?: string | null,
  idempotencyKey?: string,
): Promise<SessionRecoveryResult> {
  return request<SessionRecoveryResult>(
    projectApiPath(projectId, `/issues/${number}/sessions/${encodeURIComponent(name)}/reset`),
    { method: 'POST', headers: idempotencyKey ? { 'Idempotency-Key': idempotencyKey } : undefined },
  )
}

export function compactGenericSession(
  sessionId: string,
  projectId?: string | null,
  idempotencyKey?: string,
): Promise<SessionRecoveryResult> {
  return request<SessionRecoveryResult>(
    projectApiPath(projectId, `/agent-sessions/${encodeURIComponent(sessionId)}/compact`),
    { method: 'POST', headers: idempotencyKey ? { 'Idempotency-Key': idempotencyKey } : undefined },
  )
}

export function resetGenericSession(
  sessionId: string,
  projectId?: string | null,
  idempotencyKey?: string,
): Promise<SessionRecoveryResult> {
  return request<SessionRecoveryResult>(
    projectApiPath(projectId, `/agent-sessions/${encodeURIComponent(sessionId)}/reset`),
    { method: 'POST', headers: idempotencyKey ? { 'Idempotency-Key': idempotencyKey } : undefined },
  )
}

export interface SessionFollowupResult {
  sessionId?: string | null
  inputId?: string | null
  turnId?: string | null
  status: 'accepted' | 'rejected' | 'unknown'
  error?: string | null
  code?: string | null
  inputAcceptance?: string | null
  turnStatus?: string | null
}

export function postFollowup(
  number: number,
  name: string,
  text: string,
  projectId?: string | null,
  idempotencyKey?: string,
): Promise<SessionFollowupResult> {
  const requestKey = idempotencyKey ?? createIdempotencyKey()
  return request<SessionFollowupResult>(
    projectApiPath(projectId, `/issues/${number}/sessions/${encodeURIComponent(name)}/followup`),
    {
      method: 'POST',
      body: JSON.stringify({ text }),
      headers: { 'Idempotency-Key': requestKey },
    },
  )
}

export interface SessionCancelResult {
  state: string
  interruptUnconfirmed?: boolean | null
}

export function cancelSession(
  number: number,
  name: string,
  turnId: string,
  projectId?: string | null,
): Promise<SessionCancelResult> {
  return request<SessionCancelResult>(
    projectApiPath(projectId, `/issues/${number}/sessions/${encodeURIComponent(name)}/cancel`),
    { method: 'POST', body: JSON.stringify({ turnId }) },
  )
}

export function stopSession(
  number: number,
  name: string,
  turnId: string,
  projectId?: string | null,
): Promise<SessionCancelResult> {
  return request<SessionCancelResult>(
    projectApiPath(projectId, `/issues/${number}/sessions/${encodeURIComponent(name)}/stop`),
    { method: 'POST', body: JSON.stringify({ turnId }) },
  )
}
