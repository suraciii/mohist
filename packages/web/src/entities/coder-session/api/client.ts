import { request, projectApiPath } from '../../../shared/api/client'
import { useQuery } from '@tanstack/react-query'
import { useProject } from '../../project/@x/project-context'
import { createIdempotencyKey } from '../../../shared/lib/idempotency-key'
import type {
  AgentSessionEvent,
  AgentSessionMetadata,
  AgentSessionTranscriptResponse,
  CoderSessionSummary,
  UnifiedSessionSummaryDto,
  WorkflowRunSession,
} from '../model/types'

export function getUnifiedSessionSummary(projectId: string, sessionId: string) {
  return request<UnifiedSessionSummaryDto>(
    projectApiPath(projectId, `/sessions/${encodeURIComponent(sessionId)}`),
  )
}

export function getUnifiedSessionTranscript(
  projectId: string,
  sessionId: string,
  runtimeSessionId?: string | null,
  view: 'public' | 'raw' = 'public',
) {
  const params = new URLSearchParams()
  if (runtimeSessionId) params.set('runtimeSessionId', runtimeSessionId)
  if (view === 'raw') params.set('view', 'raw')
  const search = params.toString() ? `?${params.toString()}` : ''
  return request<AgentSessionTranscriptResponse>(
    projectApiPath(projectId, `/sessions/${encodeURIComponent(sessionId)}/transcript${search}`),
  )
}

export function unifiedSessionSummaryQueryOptions(projectId: string | null | undefined, sessionId: string) {
  return {
    queryKey: ['unified-session', projectId, sessionId] as const,
    queryFn: () => getUnifiedSessionSummary(projectId!, sessionId),
    enabled: !!projectId && !!sessionId,
    refetchInterval: (query: { state: { data: UnifiedSessionSummaryDto | undefined } }) => {
      const activity = query.state.data?.activity
      return activity === 'idle' ? false : 5000
    },
  }
}

export function unifiedSessionTranscriptQueryOptions(
  projectId: string | null | undefined,
  sessionId: string,
  runtimeSessionId?: string | null,
  view: 'public' | 'raw' = 'public',
) {
  return {
    queryKey: ['unified-session', projectId, sessionId, 'transcript', runtimeSessionId ?? null, view] as const,
    queryFn: () => getUnifiedSessionTranscript(projectId!, sessionId, runtimeSessionId, view),
    enabled: !!projectId && !!sessionId,
    refetchInterval: (query: { state: { data: AgentSessionTranscriptResponse | undefined } }) => {
      const turns = query.state.data?.turns
      return turns?.some((turn) => turn.incomplete) ? 5000 : false
    },
  }
}

export function useUnifiedSessionSummary(sessionId: string) {
  const { projectId } = useProject()
  return useQuery<UnifiedSessionSummaryDto>(unifiedSessionSummaryQueryOptions(projectId, sessionId))
}

export function useUnifiedSessionTranscript(
  sessionId: string,
  runtimeSessionId?: string | null,
  view: 'public' | 'raw' = 'public',
) {
  const { projectId } = useProject()
  return useQuery<AgentSessionTranscriptResponse>(unifiedSessionTranscriptQueryOptions(projectId, sessionId, runtimeSessionId, view))
}

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
  attachments?: SessionAttachment[] | null
  rejectedAttachments?: SessionAttachmentRejection[] | null
}

export interface SessionAttachment {
  id: string
  name: string
  contentType?: string | null
  size: number
}

export interface SessionAttachmentRejection {
  id: string
  reason: string
  message: string
}

export function postFollowup(
  number: number,
  name: string,
  text: string,
  projectId?: string | null,
  idempotencyKey?: string,
  attachments?: string[],
): Promise<SessionFollowupResult> {
  const requestKey = idempotencyKey ?? createIdempotencyKey()
  return request<SessionFollowupResult>(
    projectApiPath(projectId, `/issues/${number}/sessions/${encodeURIComponent(name)}/followup`),
    {
      method: 'POST',
      body: JSON.stringify({ text, ...(attachments?.length ? { attachments } : {}) }),
      headers: { 'Idempotency-Key': requestKey },
    },
  )
}

export interface SessionStopResult {
  state: string
  interruptUnconfirmed?: boolean | null
}

export function stopSession(
  number: number,
  name: string,
  turnId: string,
  projectId?: string | null,
  idempotencyKey?: string,
): Promise<SessionStopResult> {
  const requestKey = idempotencyKey ?? createIdempotencyKey()
  return request<SessionStopResult>(
    projectApiPath(projectId, `/issues/${number}/sessions/${encodeURIComponent(name)}/stop`),
    {
      method: 'POST',
      body: JSON.stringify({ turnId }),
      headers: { 'Idempotency-Key': requestKey },
    },
  )
}
