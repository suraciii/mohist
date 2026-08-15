import { request, projectApiPath } from '../../../shared/api/client'
import { createIdempotencyKey } from '../../../shared/lib/idempotency-key'

export type AgentSubscriptionStatus = 'active' | 'archived'
export type AgentSubscriptionState =
  | 'configured'
  | 'empty'
  | 'unconfigured'
  | 'not_executable'
  | 'unavailable'
  | 'no_connection'

export interface AgentSubscriptionDto {
  id: string
  projectId: string
  agentId: string
  name: string
  match: string
  responsePrompt: string
  continue: boolean
  position: number
  status: AgentSubscriptionStatus
  createdAt: string
  updatedAt: string
}

export interface AgentSubscriptionListDto {
  subscriptions: AgentSubscriptionDto[]
  state: AgentSubscriptionState
  agentStatus: string
  executability: 'not-configured' | 'not-executable' | 'unknown' | 'executable'
  connection: 'connected' | 'unavailable' | 'no_connection'
}

export interface AgentSubscriptionCreateRequest {
  name: string
  match: string
  responsePrompt: string
  continue?: boolean
  idempotencyKey?: string
}

export type AgentSubscriptionCreateResult = AgentSubscriptionDto & {
  idempotencyKey: string
}

export type AgentSubscriptionCreateError = Error & {
  idempotencyKey: string
}

export interface AgentSubscriptionUpdateRequest {
  name?: string
  match?: string
  responsePrompt?: string
  continue?: boolean | null
}

function subscriptionsPath(projectId: string, agentRef: string) {
  return projectApiPath(projectId, `/agents/${encodeURIComponent(agentRef)}/subscriptions`)
}

export function listAgentSubscriptions(projectId: string, agentRef: string) {
  return request<AgentSubscriptionListDto>(subscriptionsPath(projectId, agentRef))
}

export function createAgentSubscription(projectId: string, agentRef: string, data: AgentSubscriptionCreateRequest) {
  const { idempotencyKey, ...body } = data
  const key = idempotencyKey ?? createIdempotencyKey()
  return request<AgentSubscriptionDto>(subscriptionsPath(projectId, agentRef), {
    method: 'POST',
    headers: { 'Idempotency-Key': key },
    body: JSON.stringify(body),
  })
    .then((resource): AgentSubscriptionCreateResult => ({ ...resource, idempotencyKey: key }))
    .catch((error: unknown) => {
      const retryable = error instanceof Error ? error : new Error('Subscription create response was lost.')
      Object.assign(retryable, { idempotencyKey: key })
      throw retryable as AgentSubscriptionCreateError
    })
}

export function updateAgentSubscription(
  projectId: string,
  agentRef: string,
  subscriptionId: string,
  data: AgentSubscriptionUpdateRequest,
) {
  return request<AgentSubscriptionDto>(
    `${subscriptionsPath(projectId, agentRef)}/${encodeURIComponent(subscriptionId)}`,
    { method: 'PATCH', body: JSON.stringify(data) },
  )
}

export function deleteAgentSubscription(projectId: string, agentRef: string, subscriptionId: string) {
  return request<{ id: string; status: 'deleted' }>(
    `${subscriptionsPath(projectId, agentRef)}/${encodeURIComponent(subscriptionId)}`,
    { method: 'DELETE' },
  )
}
