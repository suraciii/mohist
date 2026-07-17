import { request, projectApiPath } from '../../../shared/api/client'
import type { Epic, EpicDetail, EpicWithProgress, StoredCloudEventDto } from '../model/types'

export function getEpics(params?: { projectId?: string; search?: string; sort?: string; dir?: string }) {
  const query = buildEpicListQuery(params)
  return request<EpicWithProgress[]>(projectApiPath(params?.projectId, '/epics') + query)
}

function buildEpicListQuery(params?: { search?: string; sort?: string; dir?: string }) {
  const query = new URLSearchParams()
  if (params?.search) query.set('search', params.search)
  if (params?.sort) query.set('sort', params.sort)
  if (params?.dir) query.set('dir', params.dir)
  const qs = query.toString()
  return qs.length === 0 ? '' : `?${qs}`
}

export function getEpic(number: number, params?: { projectId?: string }) {
  return request<EpicDetail>(projectApiPath(params?.projectId, `/epics/${number}`))
}

export function createEpic(data: { title: string; description: string; priority: string; projectId?: string }) {
  const { projectId, ...body } = data
  return request<Epic>(projectApiPath(projectId, '/epics'), {
    method: 'POST',
    body: JSON.stringify(body),
  })
}

export function addEpicIssue(epicNumber: number, issueNumber: number, projectId?: string | null) {
  return request<{ epicNumber: number; issueNumber: number }>(projectApiPath(projectId, `/epics/${epicNumber}/issues`), {
    method: 'POST',
    body: JSON.stringify({ issueNumber }),
  })
}

export function removeEpicIssue(epicNumber: number, issueNumber: number, projectId?: string | null) {
  return request<{ epicNumber: number; issueNumber: number }>(projectApiPath(projectId, `/epics/${epicNumber}/issues/${issueNumber}`), {
    method: 'DELETE',
  })
}

export interface BatchMembershipOutcome {
  identifier: string
  status: 'linked' | 'already-linked' | 'conflict' | 'not-found' | 'unlinked' | 'was-not-a-member'
  issueNumber?: number | null
  owningEpicNumber?: number | null
  owningEpicTitle?: string | null
}

export interface BatchMembershipResponse {
  results: BatchMembershipOutcome[]
}

export function batchAddEpicIssues(
  epicNumber: number,
  issueNumbers: number[],
  projectId?: string | null,
) {
  return request<BatchMembershipResponse>(
    projectApiPath(projectId, `/epics/${epicNumber}/issues:batch`),
    {
      method: 'POST',
      body: JSON.stringify({ issueNumbers }),
    },
  )
}

export function batchRemoveEpicIssues(
  epicNumber: number,
  issueNumbers: number[],
  projectId?: string | null,
) {
  return request<BatchMembershipResponse>(
    projectApiPath(projectId, `/epics/${epicNumber}/issues:batch-unlink`),
    {
      method: 'POST',
      body: JSON.stringify({ issueNumbers }),
    },
  )
}

export function markEpicDone(number: number, projectId?: string | null) {
  return request<Epic>(projectApiPath(projectId, `/epics/${number}/done`), { method: 'POST' })
}

export function closeEpic(number: number, projectId?: string | null) {
  return request<Epic>(projectApiPath(projectId, `/epics/${number}/close`), { method: 'POST' })
}

export interface UpdateEpicInput {
  title?: string
  description?: string
  priority?: string
}

export function updateEpic(number: number, data: UpdateEpicInput, projectId?: string | null) {
  return request<Epic>(projectApiPath(projectId, `/epics/${number}`), {
    method: 'PATCH',
    body: JSON.stringify(data),
  })
}

export function pauseEpic(number: number, reason?: string | null, projectId?: string | null) {
  return request<Epic>(projectApiPath(projectId, `/epics/${number}/pause`), {
    method: 'POST',
    body: reason != null ? JSON.stringify({ reason }) : undefined,
  })
}

export function resumeEpic(number: number, projectId?: string | null) {
  return request<Epic>(projectApiPath(projectId, `/epics/${number}/resume`), { method: 'POST' })
}

export function startEpic(number: number, projectId?: string | null) {
  return request<Epic>(projectApiPath(projectId, `/epics/${number}/start`), { method: 'POST' })
}

export function reopenEpic(number: number, projectId?: string | null) {
  return request<Epic>(projectApiPath(projectId, `/epics/${number}/reopen`), { method: 'POST' })
}

export function getEpicEvents(number: number, projectId?: string | null) {
  return request<StoredCloudEventDto[]>(projectApiPath(projectId, `/epics/${number}/events`))
}
