import { request, projectApiPath } from '../../../shared/api/client'
import type {
  AgentSessionMetadata,
  AgentSessionTranscriptResponse,
  CoderSessionSummary,
} from '../model/types'

export function getCoderSessions(number: number, projectId?: string | null) {
  return request<CoderSessionSummary[]>(projectApiPath(projectId, `/issues/${number}/coder-sessions`))
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
