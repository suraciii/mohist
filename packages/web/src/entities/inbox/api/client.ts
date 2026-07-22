import { request, projectApiPath } from '../../../shared/api/client'
import type { InboxArchiveResponse, InboxItem, InboxMarkAllReadResponse, InboxMarkReadResponse, InboxSubscription, InboxSubscriptionUpdate, InboxUnreadCount } from '../model/types'

export function getInbox(projectId: string | null | undefined, signal?: AbortSignal) {
  return request<InboxItem[]>(projectApiPath(projectId, '/inbox'), { signal })
}

export function getUnreadInboxCount(projectId: string | null | undefined, signal?: AbortSignal) {
  return request<InboxUnreadCount>(projectApiPath(projectId, '/inbox/unread-count'), { signal })
}

export function markInboxItemRead(itemId: string, projectId: string | null | undefined) {
  return request<InboxMarkReadResponse>(
    projectApiPath(projectId, `/inbox/${encodeURIComponent(itemId)}/read`),
    { method: 'POST' },
  )
}

export function markAllInboxRead(projectId: string | null | undefined) {
  return request<InboxMarkAllReadResponse>(
    projectApiPath(projectId, '/inbox/read-all'),
    { method: 'POST' },
  )
}

export function archiveInboxItem(itemId: string, projectId: string | null | undefined) {
  return request<InboxArchiveResponse>(
    projectApiPath(projectId, `/inbox/${encodeURIComponent(itemId)}/archive`),
    { method: 'POST' },
  )
}

export function getInboxSubscription(projectId: string | null | undefined, signal?: AbortSignal) {
  return request<InboxSubscription>(
    projectApiPath(projectId, '/inbox/subscription'),
    { signal },
  )
}

export function updateInboxSubscription(
  projectId: string | null | undefined,
  data: InboxSubscriptionUpdate,
) {
  return request<InboxSubscription>(
    projectApiPath(projectId, '/inbox/subscription'),
    { method: 'PUT', body: JSON.stringify(data) },
  )
}
