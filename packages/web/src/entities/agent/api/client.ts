import { request, projectApiPath } from '../../../shared/api/client'
import type { AgentActivity, AgentSessionInfo, AgentStatus } from '../model/types'

export function getAgentStatus(projectId?: string | null) {
  return request<AgentStatus>(projectApiPath(projectId, '/agent/status'))
}

export function getAgentSessions(params?: { status?: string; limit?: number; projectId?: string | null }) {
  const search = new URLSearchParams()
  if (params?.status) search.set('status', params.status)
  if (params?.limit != null) search.set('limit', String(params.limit))
  const qs = search.toString()
  return request<AgentSessionInfo[]>(projectApiPath(params?.projectId, `/agent/sessions${qs ? `?${qs}` : ''}`))
}

export function getAgentActivity(params?: { limit?: number; projectId?: string | null }) {
  const search = new URLSearchParams()
  if (params?.limit != null) search.set('limit', String(params.limit))
  const qs = search.toString()
  return request<AgentActivity>(projectApiPath(params?.projectId, `/agent/activity${qs ? `?${qs}` : ''}`))
}
