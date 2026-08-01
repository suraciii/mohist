import { projectApiPath, request } from '@/shared/api/client'
import type {
  AgentConnectionCreateRequest,
  AgentConnectionCreateResponse,
  AgentConnectionDto,
  ConnectionDiagnostic,
} from '../model/types'

export function getConnectionDiagnostic(projectId: string | null | undefined, connectionId: string) {
  return request<ConnectionDiagnostic>(
    projectApiPath(projectId, `/slack-connections/${encodeURIComponent(connectionId)}/diagnostic`),
  )
}

export function listAgentConnections(projectId: string | null | undefined) {
  return request<AgentConnectionDto[]>(projectApiPath(projectId, '/slack-connections'))
}

export function createAgentConnection(
  projectId: string | null | undefined,
  data: AgentConnectionCreateRequest,
) {
  return request<AgentConnectionCreateResponse>(projectApiPath(projectId, '/slack-connections'), {
    method: 'POST',
    body: JSON.stringify(data),
  })
}
