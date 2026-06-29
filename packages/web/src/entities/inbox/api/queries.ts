import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { useProject } from '../../project/@x/project-context'
import type { InboxArchiveResponse, InboxItem, InboxMarkAllReadResponse, InboxMarkReadResponse } from '../model/types'
import { archiveInboxItem, getInbox, markAllInboxRead, markInboxItemRead } from './client'

export const inboxQueryKey = (projectId?: string | null) =>
  projectId
    ? ['inbox', projectId] as const
    : ['inbox'] as const

export function invalidateInbox(queryClient: ReturnType<typeof useQueryClient>, projectId?: string | null) {
  queryClient.invalidateQueries({ queryKey: inboxQueryKey(projectId) })
}

export function useUnreadInboxCount() {
  const { projectId } = useProject()
  return useQuery<InboxItem[], Error, number>({
    queryKey: inboxQueryKey(projectId),
    queryFn: () => getInbox(projectId ?? undefined),
    enabled: !!projectId,
    select: (data) => data.filter((i) => !i.isRead).length,
  })
}

export function useInbox() {
  const { projectId } = useProject()
  return useQuery<InboxItem[]>({
    queryKey: inboxQueryKey(projectId),
    queryFn: () => getInbox(projectId ?? undefined),
    enabled: !!projectId,
  })
}

export function useMarkInboxItemRead() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<InboxMarkReadResponse, Error, string>({
    mutationFn: (itemId) => markInboxItemRead(itemId, projectId),
    onSuccess: () => {
      invalidateInbox(queryClient, projectId)
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useMarkAllInboxRead() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<InboxMarkAllReadResponse, Error, void>({
    mutationFn: () => markAllInboxRead(projectId),
    onSuccess: (data) => {
      invalidateInbox(queryClient, projectId)
      if (data.marked > 0) {
        toast.success(`Marked ${data.marked} inbox items as read`)
      }
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useArchiveInboxItem() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<InboxArchiveResponse, Error, string>({
    mutationFn: (itemId) => archiveInboxItem(itemId, projectId),
    onSuccess: () => {
      invalidateInbox(queryClient, projectId)
      toast.success('Inbox item archived')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}
