import { projectApiPath, request } from '@/shared/api/client'
import type {
  AgentConnectionClaimOwnerResponse,
  AgentConnectionConfigureRequest,
  AgentConnectionCreateRequest,
  AgentConnectionCreateResponse,
  AgentConnectionDetailResponse,
  AgentConnectionDto,
  AccessPolicyManageRequest,
  AccessPolicyManageResponse,
  AccessPolicyState,
  ConnectionDiagnostic,
  SlackMemberSearchResponse,
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

export function getAgentConnectionAccess(
  projectId: string | null | undefined,
  connectionId: string,
) {
  return request<AccessPolicyState>(
    projectApiPath(projectId, `/slack-connections/${encodeURIComponent(connectionId)}/access`),
  )
}

export function manageAgentConnectionAccess(
  projectId: string | null | undefined,
  connectionId: string,
  data: AccessPolicyManageRequest,
) {
  return request<AccessPolicyManageResponse>(
    projectApiPath(projectId, `/slack-connections/${encodeURIComponent(connectionId)}/manage-access`),
    {
      method: 'POST',
      body: JSON.stringify(data),
    },
  )
}

export function searchSlackConnectionMembers(
  projectId: string | null | undefined,
  connectionId: string,
  query: string,
) {
  return request<SlackMemberSearchResponse>(
    projectApiPath(
      projectId,
      `/slack-connections/${encodeURIComponent(connectionId)}/members?q=${encodeURIComponent(query)}`,
    ),
  )
}
