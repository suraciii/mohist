import { request } from '../../../shared/api/client'
import type { AgentActivity, AgentSessionInfo, AgentStatus } from '../model/types'

export function getAgentStatus() {
  return request<AgentStatus>('/agent/status')
}

export function getAgentSessions(params?: { status?: string; limit?: number; projectId?: string | null }) {
  const search = new URLSearchParams()
  if (params?.projectId) search.set('projectId', params.projectId)
  if (params?.status) search.set('status', params.status)
  if (params?.limit != null) search.set('limit', String(params.limit))
  const qs = search.toString()
  return request<AgentSessionInfo[]>(`/agent/sessions${qs ? `?${qs}` : ''}`)
}

export function getAgentActivity(params?: { limit?: number; projectId?: string | null }) {
  const search = new URLSearchParams()
  if (params?.projectId) search.set('projectId', params.projectId)
  if (params?.limit != null) search.set('limit', String(params.limit))
  const qs = search.toString()
  return request<AgentActivity>(`/agent/activity${qs ? `?${qs}` : ''}`)
}
