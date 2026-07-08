import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { QueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { useProject } from '../../project/@x/project-context'
import type { InboxItem, InboxMarkAllReadResponse, InboxSubscription, InboxSubscriptionUpdate } from '../model/types'
import { archiveInboxItem, getInbox, getInboxSubscription, markAllInboxRead, markInboxItemRead, updateInboxSubscription } from './client'

type InvalidationClient = Pick<QueryClient, 'invalidateQueries'>

export const inboxQueryKey = (projectId?: string | null) =>
  projectId
    ? ['inbox', projectId] as const
    : ['inbox'] as const

export function invalidateInbox(queryClient: InvalidationClient, projectId?: string | null) {
  queryClient.invalidateQueries({ queryKey: inboxQueryKey(projectId) })
}

export function inboxQueryOptions(projectId?: string | null) {
  return {
    queryKey: inboxQueryKey(projectId),
    queryFn: () => getInbox(projectId ?? undefined),
    enabled: !!projectId,
  }
}

export function unreadInboxCountQueryOptions(projectId?: string | null) {
  return {
    queryKey: inboxQueryKey(projectId),
    queryFn: () => getInbox(projectId ?? undefined),
    enabled: !!projectId,
    select: (data: InboxItem[]) => data.filter((i) => !i.isRead).length,
  }
}

export function useUnreadInboxCount() {
  const { projectId } = useProject()
  return useQuery<InboxItem[], Error, number>(unreadInboxCountQueryOptions(projectId))
}

export function useInbox() {
  const { projectId } = useProject()
  return useQuery<InboxItem[]>(inboxQueryOptions(projectId))
}

export function markInboxItemReadMutationOptions(projectId: string | null | undefined, queryClient: InvalidationClient) {
  return {
    mutationFn: (itemId: string) => markInboxItemRead(itemId, projectId),
    onSuccess: () => {
      invalidateInbox(queryClient, projectId)
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  }
}

export function useMarkInboxItemRead() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation(markInboxItemReadMutationOptions(projectId, queryClient))
}

export function markAllInboxReadMutationOptions(projectId: string | null | undefined, queryClient: InvalidationClient) {
  return {
    mutationFn: () => markAllInboxRead(projectId),
    onSuccess: (data: InboxMarkAllReadResponse) => {
      invalidateInbox(queryClient, projectId)
      if (data.marked > 0) {
        toast.success(`Marked ${data.marked} inbox items as read`)
      }
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  }
}

export function useMarkAllInboxRead() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation(markAllInboxReadMutationOptions(projectId, queryClient))
}

export function archiveInboxItemMutationOptions(projectId: string | null | undefined, queryClient: InvalidationClient) {
  return {
    mutationFn: (itemId: string) => archiveInboxItem(itemId, projectId),
    onSuccess: () => {
      invalidateInbox(queryClient, projectId)
      toast.success('Inbox item archived')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  }
}

export function useArchiveInboxItem() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation(archiveInboxItemMutationOptions(projectId, queryClient))
}

export const subscriptionQueryKey = (projectId?: string | null) =>
  projectId
    ? ['inbox-subscription', projectId] as const
    : ['inbox-subscription'] as const

export function inboxSubscriptionQueryOptions(projectId?: string | null) {
  return {
    queryKey: subscriptionQueryKey(projectId),
    queryFn: () => getInboxSubscription(projectId ?? undefined),
    enabled: !!projectId,
  }
}

export function useInboxSubscription() {
  const { projectId } = useProject()
  return useQuery<InboxSubscription>(inboxSubscriptionQueryOptions(projectId))
}

export function updateInboxSubscriptionMutationOptions(projectId: string | null | undefined, queryClient: InvalidationClient) {
  return {
    mutationFn: (data: InboxSubscriptionUpdate) => updateInboxSubscription(projectId, data),
    onSuccess: () => {
      if (projectId) {
        queryClient.invalidateQueries({
          queryKey: ['inbox-subscription', projectId],
        })
      }
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  }
}

export function useUpdateInboxSubscription() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation(updateInboxSubscriptionMutationOptions(projectId, queryClient))
}
