import { request, projectApiPath } from '../../../shared/api/client'
import type { AgentActivity, AgentSessionInfo, AgentStatus } from '../model/types'

export interface AgentInfo {
  id: string
  projectId: string
  name: string
  description: string
  instructions: string
  agentConfig: Record<string, unknown> | null
  skills: string[]
  maxConcurrentRuns: number | null
  status: string
  createdAt: string
  updatedAt: string
}

export interface AgentCreateRequest {
  name: string
  description?: string | null
  instructions: string
  agentConfig?: Record<string, unknown> | null
  skills?: string[] | null
  maxConcurrentRuns?: number | null
}

export interface AgentUpdateRequest {
  name?: string | null
  description?: string | null
  instructions?: string | null
  agentConfig?: Record<string, unknown> | null
  skills?: string[] | null
  maxConcurrentRuns?: number | null
}

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

export function listAgents(projectId: string, params?: { status?: string; all?: boolean }) {
  const search = new URLSearchParams()
  if (params?.status) search.set('status', params.status)
  if (params?.all) search.set('all', 'true')
  const qs = search.toString()
  return request<AgentInfo[]>(projectApiPath(projectId, `/agents${qs ? `?${qs}` : ''}`))
}

export function getAgent(projectId: string, id: string) {
  return request<AgentInfo>(projectApiPath(projectId, `/agents/${encodeURIComponent(id)}`))
}

export function createAgent(projectId: string, data: AgentCreateRequest) {
  return request<AgentInfo>(projectApiPath(projectId, '/agents'), {
    method: 'POST',
    body: JSON.stringify(data),
  })
}

export function updateAgent(projectId: string, id: string, data: AgentUpdateRequest) {
  return request<AgentInfo>(projectApiPath(projectId, `/agents/${encodeURIComponent(id)}`), {
    method: 'PATCH',
    body: JSON.stringify(data),
  })
}

export function archiveAgent(projectId: string, id: string) {
  return request<AgentInfo>(projectApiPath(projectId, `/agents/${encodeURIComponent(id)}`), {
    method: 'DELETE',
  })
}

export function unarchiveAgent(projectId: string, id: string) {
  return request<AgentInfo>(projectApiPath(projectId, `/agents/${encodeURIComponent(id)}/unarchive`), {
    method: 'POST',
  })
}

export function readAgentModelAndVariant(agent: Pick<AgentInfo, 'agentConfig'> | null | undefined): { model: string | null; variant: string | null } {
  const config = agent?.agentConfig
  if (!config || typeof config !== 'object') return { model: null, variant: null }
  const rawModel = typeof config.model === 'string' ? config.model : null
  const model = rawModel && rawModel.trim() ? rawModel : null
  if (!model) return { model: null, variant: null }
  const rawVariant = typeof config.variant === 'string' ? config.variant : null
  return {
    model,
    variant: rawVariant && rawVariant.trim() ? rawVariant : null,
  }
}

export function writeAgentModelAndVariant(
  _current: Record<string, unknown> | null | undefined,
  model: string | null,
  variant: string | null,
): Record<string, unknown> | null {
  const next: Record<string, unknown> = {}
  if (model === null) {
    return null
  }
  next.model = model
  if (variant !== null) {
    next.variant = variant
  }
  return next
}
