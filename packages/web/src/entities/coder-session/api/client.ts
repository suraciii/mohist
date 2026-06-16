import { request, projectApiPath } from '../../../shared/api/client'
import type {
  AgentSessionEvent,
  AgentSessionMetadata,
  AgentSessionTranscriptResponse,
  CoderSessionSummary,
  WorkflowRunSession,
} from '../model/types'

export function getCoderSessions(number: number, projectId?: string | null) {
  return request<CoderSessionSummary[]>(projectApiPath(projectId, `/issues/${number}/coder-sessions`))
}

export function getWorkflowRunSessions(workflowRunId: string) {
  return request<WorkflowRunSession[]>(`/workflow-runs/${encodeURIComponent(workflowRunId)}/sessions`)
}

export function getAgentSessionMetadata(number: number, name: string, projectId?: string | null) {
  return request<AgentSessionMetadata>(
    projectApiPath(projectId, `/issues/${number}/sessions/${encodeURIComponent(name)}`),
  )
}

export function getAgentSessionTranscript(number: number, name: string, projectId?: string | null) {
  return request<AgentSessionTranscriptResponse>(
    projectApiPath(projectId, `/issues/${number}/sessions/${encodeURIComponent(name)}/transcript`),
  )
}

export function getAgentSessionEvents(number: number, name: string, projectId?: string | null) {
  return request<{ events: AgentSessionEvent[] }>(
    projectApiPath(projectId, `/issues/${number}/sessions/${encodeURIComponent(name)}/events`),
  )
}

export interface SessionRecoveryResult {
  id: string
  agentSessionId?: string | null
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
): Promise<SessionRecoveryResult> {
  return request<SessionRecoveryResult>(
    projectApiPath(projectId, `/issues/${number}/sessions/${encodeURIComponent(name)}/compact`),
    { method: 'POST' },
  )
}

export function resetSession(
  number: number,
  name: string,
  projectId?: string | null,
): Promise<SessionRecoveryResult> {
  return request<SessionRecoveryResult>(
    projectApiPath(projectId, `/issues/${number}/sessions/${encodeURIComponent(name)}/reset`),
    { method: 'POST' },
  )
}
