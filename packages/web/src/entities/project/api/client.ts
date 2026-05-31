import { request } from '../../../shared/api/client'
import type { DirEntry, Project, Repository } from '../model/types'

export function getProjects() {
  return request<Project[]>('/projects')
}

export function createProject(data: { name: string; path: string }) {
  return request<Project>('/projects', {
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

export function addRepository(projectId: string, data: { name: string; path?: string; remote?: string; baseBranch?: string }) {
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

export function listDirectories(path: string) {
  const search = new URLSearchParams({ path })
  return request<DirEntry[]>(`/fs/list?${search.toString()}`)
}

export function searchDirectories(query: string, limit: number = 50) {
  const search = new URLSearchParams({ query, limit: String(limit) })
  return request<DirEntry[]>(`/fs/search?${search.toString()}`)
}

export function getHomeDir() {
  return request<string>('/fs/home')
}
