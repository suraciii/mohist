import { request, projectApiPath } from '../../../shared/api/client'
import type { Workspace } from '../model/types'

export function getWorkspaces(params?: { projectId?: string; status?: string; origin?: string }) {
  const query = new URLSearchParams()
  if (params?.status) query.set('status', params.status)
  if (params?.origin) query.set('origin', params.origin)
  const qs = query.toString()
  return request<Workspace[]>(projectApiPath(params?.projectId, '/workspaces') + (qs.length === 0 ? '' : `?${qs}`))
}

export function getWorkspace(name: string, params?: { projectId?: string }) {
  return request<Workspace>(projectApiPath(params?.projectId, `/workspaces/${encodeURIComponent(name)}`))
}

export function closeWorkspace(name: string, projectId?: string | null) {
  return request<Workspace>(projectApiPath(projectId, `/workspaces/${encodeURIComponent(name)}/close`), {
    method: 'POST',
  })
}
