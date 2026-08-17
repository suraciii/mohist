import { request, projectApiPath } from '../../../shared/api/client'
import type { AgentActivity, AgentSessionInfo, AgentStatus } from '../model/types'

type AgentRuntime = 'opencode' | 'pi'
const DEFAULT_AGENT_RUNTIME: AgentRuntime = 'opencode'

export interface AgentReadinessGap {
  code: string
  message: string
  action: string
}

export interface AgentReadinessSetup {
  label: string
  path: string
}

export interface AgentReadinessResult {
  conclusion: 'Ready' | 'Needs setup' | 'Unknown'
  gaps: AgentReadinessGap[]
  setup: AgentReadinessSetup | null
}

export interface AgentInfo {
  id: string
  projectId: string
  name: string
  description: string
  instructions: string
  agentConfig: Record<string, unknown> | null
  effectiveExecutionConfig?: {
    runtime: AgentRuntime
    model: string | null
    variant: string | null
  } | null
  skills: string[]
  allowedSubagentAgentIds?: string[] | null
  maxConcurrentRuns: number | null
  status: string
  createdAt: string
  updatedAt: string
  readiness?: AgentReadinessResult | null
}

export interface AgentCreateRequest {
  name: string
  description?: string | null
  instructions: string
  agentConfig?: Record<string, unknown> | null
  skills?: string[] | null
  maxConcurrentRuns?: number | null
  allowedSubagentAgentIds?: string[] | null
}

export interface AgentUpdateRequest {
  name?: string | null
  description?: string | null
  instructions?: string | null
  agentConfig?: Record<string, unknown> | null
  skills?: string[] | null
  maxConcurrentRuns?: number | null
  allowedSubagentAgentIds?: string[] | null
}

export interface AgentAvailabilityCapacity {
  usedSlots: number
  totalSlots: number
}

export interface AgentAvailabilityResponse {
  canStartNow: boolean
  waitingReason: string | null
  activeRuns: number
  maxConcurrentRuns: number | null
  capacity: AgentAvailabilityCapacity
  observedAt: string
}

export interface AgentAvailabilitySummaryEntry {
  agentId: string
  canStartNow: boolean
  waitingReason: string | null
  activeRuns: number
  maxConcurrentRuns: number | null
  capacity: AgentAvailabilityCapacity
  queuedCount: number
}

export interface AgentWaitingWorkItem {
  jobId: string
  status: string
  waitingReason: string
  submittedAt: string | null
}

export interface AgentStatusDetailResponse {
  agentId: string
  agentName: string
  availability: AgentAvailabilityResponse
  waitingWork: AgentWaitingWorkItem[]
}

export function getAgentStatus(projectId?: string | null) {
  return request<AgentStatus>(projectApiPath(projectId, '/agent/status'))
}

export function getAgentDetailStatus(projectId: string, agentRef: string) {
  return request<AgentStatusDetailResponse>(projectApiPath(projectId, `/agents/${encodeURIComponent(agentRef)}/status`))
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

export function getAgentListAvailability(projectId: string) {
  return request<AgentAvailabilitySummaryEntry[]>(projectApiPath(projectId, '/agents/availability'))
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

export function readAgentDefinitionModelAndVariant(agent: Pick<AgentInfo, 'agentConfig'> | null | undefined): {
  model: string | null
  variant: string | null
  runtime: AgentRuntime
} {
  const config = agent?.agentConfig
  if (!config || typeof config !== 'object') return { model: null, variant: null, runtime: DEFAULT_AGENT_RUNTIME }
  const rawModel = typeof config.model === 'string' ? config.model : null
  const model = rawModel && rawModel.trim() ? rawModel : null
  const runtime = config.runtime === 'opencode' || config.runtime === 'pi' ? config.runtime : DEFAULT_AGENT_RUNTIME
  if (!model) return { model: null, variant: null, runtime }
  const rawVariant = typeof config.variant === 'string' ? config.variant : null
  return {
    model,
    variant: rawVariant && rawVariant.trim() ? rawVariant : null,
    runtime,
  }
}

export function readAgentModelAndVariant(
  agent: (Pick<AgentInfo, 'agentConfig'> & Partial<Pick<AgentInfo, 'effectiveExecutionConfig'>>) | null | undefined,
): { model: string | null; variant: string | null; runtime: AgentRuntime } {
  const effective = agent?.effectiveExecutionConfig
  if (effective && (effective.runtime === 'opencode' || effective.runtime === 'pi')) {
    return {
      model: typeof effective.model === 'string' && effective.model.trim() ? effective.model : null,
      variant: typeof effective.variant === 'string' && effective.variant.trim() ? effective.variant : null,
      runtime: effective.runtime,
    }
  }
  return readAgentDefinitionModelAndVariant(agent)
}

export function writeAgentModelAndVariant(
  _current: Record<string, unknown> | null | undefined,
  model: string | null,
  variant: string | null,
  runtime: AgentRuntime = DEFAULT_AGENT_RUNTIME,
): Record<string, unknown> | null {
  const next: Record<string, unknown> = {}
  if (model === null) {
    return runtime === DEFAULT_AGENT_RUNTIME ? null : { runtime }
  }
  next.model = model
  if (variant !== null) {
    next.variant = variant
  }
  next.runtime = runtime
  return next
}
