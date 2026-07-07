import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
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

export const agentSubscriptionsQueryKey = (
  projectId: string | null | undefined,
  agentRef: string,
) =>
  projectId
    ? ['agents', projectId, agentRef, subscriptionKeySegment] as const
    : ['agents', undefined, agentRef, subscriptionKeySegment] as const

export const agentScopedQueryKey = (projectId: string | null | undefined, agentRef: string) =>
  ['agents', projectId, agentRef] as const

export function useAgentSubscriptions(agentRef: string) {
  const { projectId } = useProject()
  return useQuery<AgentSubscriptionDto[]>({
    queryKey: agentSubscriptionsQueryKey(projectId, agentRef),
    queryFn: () => listAgentSubscriptions(projectId!, agentRef),
    enabled: !!projectId && !!agentRef,
  })
}

export function useCreateAgentSubscription(agentRef: string) {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<AgentSubscriptionDto, Error, AgentSubscriptionCreateRequest>({
    mutationFn: (data) => createAgentSubscription(projectId!, agentRef, data),
    onSuccess: (created) => {
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
  })
}

interface TransitionVariables {
  subscriptionId: string
}

export function useArchiveAgentSubscription(agentRef: string) {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<AgentSubscriptionDto, Error, TransitionVariables>({
    mutationFn: ({ subscriptionId }) =>
      archiveAgentSubscription(projectId!, agentRef, subscriptionId),
    onSuccess: (updated) => {
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
  })
}

export function useRestoreAgentSubscription(agentRef: string) {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<AgentSubscriptionDto, Error, TransitionVariables>({
    mutationFn: ({ subscriptionId }) =>
      restoreAgentSubscription(projectId!, agentRef, subscriptionId),
    onSuccess: (updated) => {
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
  })
}

export function useDeleteAgentSubscription(agentRef: string) {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<unknown, Error, TransitionVariables>({
    mutationFn: ({ subscriptionId }) =>
      deleteAgentSubscription(projectId!, agentRef, subscriptionId),
    onSuccess: (_data, { subscriptionId }) => {
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
  })
}
