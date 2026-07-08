import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { QueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { useProject } from '../../project/@x/project-context'
import {
  archiveAgentSubscription,
  createAgentSubscription,
  deleteAgentSubscription,
  listAgentSubscriptions,
  restoreAgentSubscription,
} from './subscriptions'
import type {
  AgentSubscriptionCreateRequest,
  AgentSubscriptionDto,
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
  }
}

export function useAgentSubscriptions(agentRef: string) {
  const { projectId } = useProject()
  return useQuery<AgentSubscriptionDto[]>(agentSubscriptionsQueryOptions(projectId, agentRef))
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
        queryClient.invalidateQueries({
          queryKey: agentSubscriptionsQueryKey(projectId, agentRef),
        })
        queryClient.invalidateQueries({ queryKey: agentScopedQueryKey(projectId, agentRef) })
      }
      toast.success(`Subscription "${created.name}" created`)
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Failed to create subscription')
    },
  }
}

export function useCreateAgentSubscription(agentRef: string) {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation(createAgentSubscriptionMutationOptions(projectId, agentRef, queryClient))
}

interface TransitionVariables {
  subscriptionId: string
}

export function archiveAgentSubscriptionMutationOptions(
  projectId: string | null | undefined,
  agentRef: string,
  queryClient: InvalidationClient,
) {
  return {
    mutationFn: ({ subscriptionId }: TransitionVariables) =>
      archiveAgentSubscription(projectId!, agentRef, subscriptionId),
    onSuccess: (updated: AgentSubscriptionDto) => {
      if (projectId) {
        queryClient.invalidateQueries({
          queryKey: agentSubscriptionsQueryKey(projectId, agentRef),
        })
        queryClient.invalidateQueries({ queryKey: agentScopedQueryKey(projectId, agentRef) })
      }
      toast.success(`Subscription "${updated.name}" archived`)
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Failed to archive subscription')
    },
  }
}

export function useArchiveAgentSubscription(agentRef: string) {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation(archiveAgentSubscriptionMutationOptions(projectId, agentRef, queryClient))
}

export function restoreAgentSubscriptionMutationOptions(
  projectId: string | null | undefined,
  agentRef: string,
  queryClient: InvalidationClient,
) {
  return {
    mutationFn: ({ subscriptionId }: TransitionVariables) =>
      restoreAgentSubscription(projectId!, agentRef, subscriptionId),
    onSuccess: (updated: AgentSubscriptionDto) => {
      if (projectId) {
        queryClient.invalidateQueries({
          queryKey: agentSubscriptionsQueryKey(projectId, agentRef),
        })
        queryClient.invalidateQueries({ queryKey: agentScopedQueryKey(projectId, agentRef) })
      }
      toast.success(`Subscription "${updated.name}" restored`)
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Failed to restore subscription')
    },
  }
}

export function useRestoreAgentSubscription(agentRef: string) {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation(restoreAgentSubscriptionMutationOptions(projectId, agentRef, queryClient))
}

export function deleteAgentSubscriptionMutationOptions(
  projectId: string | null | undefined,
  agentRef: string,
  queryClient: InvalidationClient,
) {
  return {
    mutationFn: ({ subscriptionId }: TransitionVariables) =>
      deleteAgentSubscription(projectId!, agentRef, subscriptionId),
    onSuccess: (_data: unknown, { subscriptionId }: TransitionVariables) => {
      if (projectId) {
        queryClient.invalidateQueries({
          queryKey: agentSubscriptionsQueryKey(projectId, agentRef),
        })
        queryClient.invalidateQueries({ queryKey: agentScopedQueryKey(projectId, agentRef) })
      }
      toast.success(`Subscription ${subscriptionId} deleted`)
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Failed to delete subscription')
    },
  }
}

export function useDeleteAgentSubscription(agentRef: string) {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation(deleteAgentSubscriptionMutationOptions(projectId, agentRef, queryClient))
}
