import { request } from '../../../shared/api/client'
import type { DirEntry, Project } from '../model/types'

export function getProjects() {
  return request<Project[]>('/projects')
}

export function createProject(data: { name: string; path: string }) {
  return request<Project>('/projects', {
    method: 'POST',
    body: JSON.stringify(data),
  })
}

export function deleteProject(name: string) {
  return request<{ message: string }>(`/projects/${encodeURIComponent(name)}`, {
    method: 'DELETE',
  })
}

export function useProjectByName(name: string) {
  return request<Project>(`/projects/${encodeURIComponent(name)}/use`, {
    method: 'POST',
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
