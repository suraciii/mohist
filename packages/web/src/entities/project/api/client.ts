import { request } from '../../../shared/api/client'
import type { Project, Repository } from '../model/types'

export function getProjects() {
  return request<Project[]>('/projects')
}

export function createProject(
  data: { name: string; repository: { name: string; gitUrl: string; baseBranch?: string } },
  requester: typeof request = request,
) {
  return requester<Project>('/projects', {
    method: 'POST',
    body: JSON.stringify(data),
  })
}

export function deleteProject(id: string) {
  return request<{ message: string }>(`/projects/${encodeURIComponent(id)}`, {
    method: 'DELETE',
  })
}

export function getRepositories(projectId: string) {
  return request<Repository[]>(`/projects/${encodeURIComponent(projectId)}/repositories`)
}

export function addRepository(projectId: string, data: { name: string; gitUrl: string; baseBranch?: string; setDefault?: boolean }) {
  return request<Project>(`/projects/${encodeURIComponent(projectId)}/repositories`, {
    method: 'POST',
    body: JSON.stringify(data),
  })
}

export function removeRepository(projectId: string, repoName: string) {
  return request<Project>(`/projects/${encodeURIComponent(projectId)}/repositories/${encodeURIComponent(repoName)}`, {
    method: 'DELETE',
  })
}

export function setDefaultRepository(projectId: string, repoName: string) {
  return request<Project>(`/projects/${encodeURIComponent(projectId)}/repositories/${encodeURIComponent(repoName)}`, {
    method: 'PATCH',
    body: JSON.stringify({ setDefault: true }),
  })
}

export function updateRepositoryMetadata(
  projectId: string,
  repoName: string,
  patch: { gitUrl?: string; baseBranch?: string },
) {
  return request<Project>(`/projects/${encodeURIComponent(projectId)}/repositories/${encodeURIComponent(repoName)}`, {
    method: 'PATCH',
    body: JSON.stringify(patch),
  })
}
