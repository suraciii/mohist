import { request, withProject } from '../../../shared/api/client'
import type { Epic, EpicDetail, EpicWithProgress } from '../model/types'

export function getEpics(params?: { projectId?: string }) {
  const search = new URLSearchParams()
  if (params?.projectId) search.set('projectId', params.projectId)
  const qs = search.toString()
  return request<EpicWithProgress[]>(`/epics${qs ? `?${qs}` : ''}`)
}

export function getEpic(id: string, params?: { projectId?: string }) {
  const search = new URLSearchParams()
  if (params?.projectId) search.set('projectId', params.projectId)
  const qs = search.toString()
  return request<EpicDetail>(`/epics/${encodeURIComponent(id)}${qs ? `?${qs}` : ''}`)
}

export function createEpic(data: { title: string; description: string; priority: string; projectId?: string }) {
  return request<Epic>('/epics', {
    method: 'POST',
    body: JSON.stringify(data),
  })
}

export function addEpicIssue(epicId: string, issueId: string, projectId?: string | null) {
  return request<{ epicId: string; issueId: string }>(withProject(`/epics/${encodeURIComponent(epicId)}/issues`, projectId), {
    method: 'POST',
    body: JSON.stringify({ issueId }),
  })
}

export function removeEpicIssue(epicId: string, issueId: string, projectId?: string | null) {
  return request<{ epicId: string; issueId: string }>(withProject(`/epics/${encodeURIComponent(epicId)}/issues/${encodeURIComponent(issueId)}`, projectId), {
    method: 'DELETE',
  })
}

export function markEpicDone(id: string, projectId?: string | null) {
  return request<Epic>(withProject(`/epics/${encodeURIComponent(id)}/done`, projectId), { method: 'POST' })
}

export function closeEpic(id: string, projectId?: string | null) {
  return request<Epic>(withProject(`/epics/${encodeURIComponent(id)}/close`, projectId), { method: 'POST' })
}
