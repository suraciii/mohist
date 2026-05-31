import { request, withProject } from '../../../shared/api/client'
import type { Epic, EpicDetail, EpicWithProgress } from '../model/types'

export function getEpics(params?: { projectId?: string }) {
  return request<EpicWithProgress[]>('/epics', withProject(undefined, params?.projectId))
}

export function getEpic(id: string, params?: { projectId?: string }) {
  return request<EpicDetail>(`/epics/${encodeURIComponent(id)}`, withProject(undefined, params?.projectId))
}

export function createEpic(data: { title: string; description: string; priority: string; projectId?: string }) {
  const { projectId, ...body } = data
  return request<Epic>('/epics', withProject({
    method: 'POST',
    body: JSON.stringify(body),
  }, projectId))
}

export function addEpicIssue(epicId: string, issueId: string, projectId?: string | null) {
  return request<{ epicId: string; issueId: string }>(`/epics/${encodeURIComponent(epicId)}/issues`, withProject({
    method: 'POST',
    body: JSON.stringify({ issueId }),
  }, projectId))
}

export function removeEpicIssue(epicId: string, issueId: string, projectId?: string | null) {
  return request<{ epicId: string; issueId: string }>(`/epics/${encodeURIComponent(epicId)}/issues/${encodeURIComponent(issueId)}`, withProject({
    method: 'DELETE',
  }, projectId))
}

export function markEpicDone(id: string, projectId?: string | null) {
  return request<Epic>(`/epics/${encodeURIComponent(id)}/done`, withProject({ method: 'POST' }, projectId))
}

export function closeEpic(id: string, projectId?: string | null) {
  return request<Epic>(`/epics/${encodeURIComponent(id)}/close`, withProject({ method: 'POST' }, projectId))
}
