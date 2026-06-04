import { request, withProject } from '../../../shared/api/client'
import type {
  AgentSessionEventsResponse,
  AgentSessionMetadata,
  CoderSessionSummary,
} from '../model/types'

export function getCoderSessions(number: number, projectId?: string | null) {
  return request<CoderSessionSummary[]>(`/issues/${number}/coder-sessions`, withProject(undefined, projectId))
}

export function getAgentSessionMetadata(number: number, name: string, projectId?: string | null) {
  return request<AgentSessionMetadata>(
    `/issues/${number}/sessions/${encodeURIComponent(name)}`,
    withProject(undefined, projectId),
  )
}

export function getAgentSessionEvents(number: number, name: string, projectId?: string | null) {
  return request<AgentSessionEventsResponse>(
    `/issues/${number}/sessions/${encodeURIComponent(name)}/events`,
    withProject(undefined, projectId),
  )
}
