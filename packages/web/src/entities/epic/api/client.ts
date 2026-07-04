import { request, projectApiPath } from '../../../shared/api/client'
import type { Epic, EpicDetail, EpicWithProgress } from '../model/types'

export function getEpics(params?: { projectId?: string }) {
  return request<EpicWithProgress[]>(projectApiPath(params?.projectId, '/epics'))
}

export function getEpic(id: string, params?: { projectId?: string }) {
  return request<EpicDetail>(projectApiPath(params?.projectId, `/epics/${encodeURIComponent(id)}`))
}

export function createEpic(data: { title: string; description: string; priority: string; projectId?: string }) {
  const { projectId, ...body } = data
  return request<Epic>(projectApiPath(projectId, '/epics'), {
    method: 'POST',
    body: JSON.stringify(body),
  })
}

export function addEpicIssue(epicId: string, issueId: string, projectId?: string | null) {
  return request<{ epicId: string; issueId: string }>(projectApiPath(projectId, `/epics/${encodeURIComponent(epicId)}/issues`), {
    method: 'POST',
    body: JSON.stringify({ issueId }),
  })
}

export function removeEpicIssue(epicId: string, issueId: string, projectId?: string | null) {
  return request<{ epicId: string; issueId: string }>(projectApiPath(projectId, `/epics/${encodeURIComponent(epicId)}/issues/${encodeURIComponent(issueId)}`), {
    method: 'DELETE',
  })
}

export function markEpicDone(id: string, projectId?: string | null) {
  return request<Epic>(projectApiPath(projectId, `/epics/${encodeURIComponent(id)}/done`), { method: 'POST' })
}

export function closeEpic(id: string, projectId?: string | null) {
  return request<Epic>(projectApiPath(projectId, `/epics/${encodeURIComponent(id)}/close`), { method: 'POST' })
}

export interface UpdateEpicInput {
  title?: string
  description?: string
  priority?: string
}

export function updateEpic(id: string, data: UpdateEpicInput, projectId?: string | null) {
  return request<Epic>(projectApiPath(projectId, `/epics/${encodeURIComponent(id)}`), {
    method: 'PATCH',
    body: JSON.stringify(data),
  })
}

export function pauseEpic(id: string, reason?: string | null, projectId?: string | null) {
  return request<Epic>(projectApiPath(projectId, `/epics/${encodeURIComponent(id)}/pause`), {
    method: 'POST',
    body: reason != null ? JSON.stringify({ reason }) : undefined,
  })
}

export function resumeEpic(id: string, projectId?: string | null) {
  return request<Epic>(projectApiPath(projectId, `/epics/${encodeURIComponent(id)}/resume`), { method: 'POST' })
}

export function startEpic(id: string, projectId?: string | null) {
  return request<Epic>(projectApiPath(projectId, `/epics/${encodeURIComponent(id)}/start`), { method: 'POST' })
}

export function reopenEpic(id: string, projectId?: string | null) {
  return request<Epic>(projectApiPath(projectId, `/epics/${encodeURIComponent(id)}/reopen`), { method: 'POST' })
}
