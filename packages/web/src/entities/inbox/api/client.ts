import { request, projectApiPath } from '../../../shared/api/client'
import type { InboxArchiveResponse, InboxItem, InboxMarkAllReadResponse, InboxMarkReadResponse } from '../model/types'

export function getInbox(projectId: string | null | undefined) {
  return request<InboxItem[]>(projectApiPath(projectId, '/inbox'))
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