import { projectApiPath, request } from '@/shared/api/client'
import type {
  AgentConnectionClaimOwnerResponse,
  AgentConnectionConfigureRequest,
  AgentConnectionCreateRequest,
  AgentConnectionCreateResponse,
  AgentConnectionDetailResponse,
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

export function getAgentConnection(
  projectId: string | null | undefined,
  connectionId: string,
) {
  return request<AgentConnectionDetailResponse>(
    projectApiPath(projectId, `/slack-connections/${encodeURIComponent(connectionId)}`),
  )
}

export function configureAgentConnection(
  projectId: string | null | undefined,
  connectionId: string,
  data: AgentConnectionConfigureRequest,
) {
  return request<AgentConnectionDto>(
    projectApiPath(projectId, `/slack-connections/${encodeURIComponent(connectionId)}/configure`),
    {
      method: 'POST',
      body: JSON.stringify(data),
    },
  )
}

export function claimAgentConnectionOwner(
  projectId: string | null | undefined,
  connectionId: string,
) {
  return request<AgentConnectionClaimOwnerResponse>(
    projectApiPath(projectId, `/slack-connections/${encodeURIComponent(connectionId)}/claim-owner`),
    { method: 'POST' },
  )
}
