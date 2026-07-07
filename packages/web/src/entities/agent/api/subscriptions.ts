import { request, projectApiPath } from '../../../shared/api/client'

export interface AgentSubscriptionFilterDto {
  type: string
  source: string | null
  subject: string | null
}

export interface AgentSubscriptionDto {
  id: string
  projectId: string
  agentId: string
  name: string
  filter: AgentSubscriptionFilterDto
  responsePrompt: string
  priority: number | null
  status: 'active' | 'archived'
  createdAt: string
  updatedAt: string
}

export interface AgentSubscriptionCreateRequest {
  name: string
  filter: {
    type: string
    source?: string | null
    subject?: string | null
  }
  responsePrompt: string
  priority?: number | null
}

export function listAgentSubscriptions(projectId: string, agentRef: string) {
  return request<AgentSubscriptionDto[]>(
    projectApiPath(projectId, `/agents/${encodeURIComponent(agentRef)}/subscriptions`),
  )
}

export function createAgentSubscription(
  projectId: string,
  agentRef: string,
  data: AgentSubscriptionCreateRequest,
) {
  return request<AgentSubscriptionDto>(
    projectApiPath(projectId, `/agents/${encodeURIComponent(agentRef)}/subscriptions`),
    { method: 'POST', body: JSON.stringify(data) },
  )
}

export function archiveAgentSubscription(projectId: string, agentRef: string, subscriptionId: string) {
  return request<AgentSubscriptionDto>(
    projectApiPath(
      projectId,
      `/agents/${encodeURIComponent(agentRef)}/subscriptions/${encodeURIComponent(subscriptionId)}/archive`,
    ),
    { method: 'POST' },
  )
}

export function restoreAgentSubscription(projectId: string, agentRef: string, subscriptionId: string) {
  return request<AgentSubscriptionDto>(
    projectApiPath(
      projectId,
      `/agents/${encodeURIComponent(agentRef)}/subscriptions/${encodeURIComponent(subscriptionId)}/restore`,
    ),
    { method: 'POST' },
  )
}

export function deleteAgentSubscription(projectId: string, agentRef: string, subscriptionId: string) {
  return request<unknown>(
    projectApiPath(
      projectId,
      `/agents/${encodeURIComponent(agentRef)}/subscriptions/${encodeURIComponent(subscriptionId)}`,
    ),
    { method: 'DELETE' },
  )
}

export function formatAgentSubscriptionFilter(filter: AgentSubscriptionFilterDto): string {
  const parts: string[] = [filter.type]
  if (filter.source) parts.push(`source=${filter.source}`)
  if (filter.subject) parts.push(`subject=${filter.subject}`)
  return parts.join(', ')
}
