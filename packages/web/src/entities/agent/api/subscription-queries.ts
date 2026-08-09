import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { QueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { useProject } from '../../project/@x/project-context'
import {
  createAgentSubscription,
  deleteAgentSubscription,
  listAgentSubscriptions,
  updateAgentSubscription,
} from './subscriptions'
import type {
  AgentSubscriptionCreateRequest,
  AgentSubscriptionDto,
  AgentSubscriptionListDto,
  AgentSubscriptionUpdateRequest,
} from './subscriptions'

const subscriptionKeySegment = 'subscriptions' as const

type InvalidationClient = Pick<QueryClient, 'invalidateQueries'>

export const agentSubscriptionsQueryKey = (
  projectId: string | null | undefined,
  agentRef: string,
) =>
  projectId
    ? ['agents', projectId, agentRef, subscriptionKeySegment] as const
    : ['agents', undefined, agentRef, subscriptionKeySegment] as const

export const agentScopedQueryKey = (projectId: string | null | undefined, agentRef: string) =>
  ['agents', projectId, agentRef] as const

export function agentSubscriptionsQueryOptions(projectId: string | null | undefined, agentRef: string) {
  return {
    queryKey: agentSubscriptionsQueryKey(projectId, agentRef),
    queryFn: () => listAgentSubscriptions(projectId!, agentRef),
    enabled: !!projectId && !!agentRef,
    retry: false,
  }
}

export function useAgentSubscriptions(agentRef: string) {
  const { projectId } = useProject()
  return useQuery<AgentSubscriptionListDto>(agentSubscriptionsQueryOptions(projectId, agentRef))
}

export function createAgentSubscriptionMutationOptions(
  projectId: string | null | undefined,
  agentRef: string,
  queryClient: InvalidationClient,
) {
  return {
    mutationFn: (data: AgentSubscriptionCreateRequest) =>
      createAgentSubscription(projectId!, agentRef, data),
    onSuccess: (created: AgentSubscriptionDto) => {
      if (projectId) {
        queryClient.invalidateQueries({ queryKey: agentSubscriptionsQueryKey(projectId, agentRef) })
        queryClient.invalidateQueries({ queryKey: agentScopedQueryKey(projectId, agentRef) })
      }
      toast.success(`Subscription "${created.name}" saved`)
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Failed to save subscription')
    },
  }
}

export function useCreateAgentSubscription(agentRef: string) {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation(createAgentSubscriptionMutationOptions(projectId, agentRef, queryClient))
}

interface SubscriptionVariables {
  subscriptionId: string
}

export function updateAgentSubscriptionMutationOptions(
  projectId: string | null | undefined,
  agentRef: string,
  queryClient: InvalidationClient,
) {
  return {
    mutationFn: ({ subscriptionId, data }: SubscriptionVariables & { data: AgentSubscriptionUpdateRequest }) =>
      updateAgentSubscription(projectId!, agentRef, subscriptionId, data),
    onSuccess: (updated: AgentSubscriptionDto) => {
      if (projectId) {
        queryClient.invalidateQueries({ queryKey: agentSubscriptionsQueryKey(projectId, agentRef) })
        queryClient.invalidateQueries({ queryKey: agentScopedQueryKey(projectId, agentRef) })
      }
      toast.success(`Subscription "${updated.name}" saved`)
    },
    onError: (err: Error) => toast.error(err.message || 'Failed to update subscription'),
  }
}

export function useUpdateAgentSubscription(agentRef: string) {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation(updateAgentSubscriptionMutationOptions(projectId, agentRef, queryClient))
}

export function deleteAgentSubscriptionMutationOptions(
  projectId: string | null | undefined,
  agentRef: string,
  queryClient: InvalidationClient,
) {
  return {
    mutationFn: ({ subscriptionId }: SubscriptionVariables) =>
      deleteAgentSubscription(projectId!, agentRef, subscriptionId),
    onSuccess: (_deleted: { id: string; status: 'deleted' }, { subscriptionId }: SubscriptionVariables) => {
      if (projectId) {
        queryClient.invalidateQueries({ queryKey: agentSubscriptionsQueryKey(projectId, agentRef) })
        queryClient.invalidateQueries({ queryKey: agentScopedQueryKey(projectId, agentRef) })
      }
      toast.success(`Subscription ${subscriptionId} deleted`)
    },
    onError: (err: Error) => toast.error(err.message || 'Failed to delete subscription'),
  }
}

export function useDeleteAgentSubscription(agentRef: string) {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation(deleteAgentSubscriptionMutationOptions(projectId, agentRef, queryClient))
}
