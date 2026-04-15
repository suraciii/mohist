import type { ApiResponse } from './types'

const BASE = '/api'

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    headers: { 'Content-Type': 'application/json' },
    ...init,
  })
  const json: ApiResponse<T> = await res.json()
  if (!json.success) {
    throw new Error(json.error ?? `Request failed: ${res.status}`)
  }
  return json.data as T
}

export const api = {
  getProjects: () => request<import('./types').Project[]>('/projects'),

  getIssues: (params?: { stage?: string; label?: string; projectId?: string }) => {
    const search = new URLSearchParams()
    if (params?.projectId) search.set('projectId', params.projectId)
    if (params?.stage) search.set('stage', params.stage)
    if (params?.label) search.set('label', params.label)
    const qs = search.toString()
    return request<import('./types').Issue[]>(`/issues${qs ? `?${qs}` : ''}`)
  },

  getIssue: (number: number) =>
    request<import('./types').Issue>(`/issues/${number}`),

  createIssue: (data: { title: string; body?: string; labels?: string[] }) =>
    request<import('./types').Issue>('/issues', {
      method: 'POST',
      body: JSON.stringify(data),
    }),

  updateIssue: (number: number, data: { title?: string; body?: string; addLabels?: string[]; removeLabels?: string[] }) =>
    request<import('./types').Issue>(`/issues/${number}`, {
      method: 'PATCH',
      body: JSON.stringify(data),
    }),

  startIssue: (number: number) =>
    request<{ issue: import('./types').Issue; message: string }>(`/issues/${number}/start`, { method: 'POST' }),

  closeIssue: (number: number) =>
    request<{ issue: import('./types').Issue; message: string }>(`/issues/${number}/close`, { method: 'POST' }),

  reopenIssue: (number: number) =>
    request<{ issue: import('./types').Issue; message: string }>(`/issues/${number}/reopen`, { method: 'POST' }),

  approveIssue: (number: number) =>
    request<{ issue: import('./types').Issue; context: string | null; message: string }>(`/issues/${number}/approve`, { method: 'POST' }),

  getIssueDiff: (number: number) =>
    request<{ files: import('./types').DiffFile[] }>(`/issues/${number}/diff`),

  addComment: (issueNumber: number, body: string) =>
    request<import('./types').Comment>(`/issues/${issueNumber}/comments`, {
      method: 'POST',
      body: JSON.stringify({ body }),
    }),

  getQuestions: (issueId: string) =>
    request<import('./types').Question[]>(`/questions?issueId=${encodeURIComponent(issueId)}`),

  replyQuestion: (questionId: string, answer: string) =>
    request<import('./types').Question>(`/questions/${questionId}/reply`, {
      method: 'POST',
      body: JSON.stringify({ answer }),
    }),

  sendMessage: (issueNumber: number, message: string) =>
    request<{ message: string }>(`/issues/${issueNumber}/messages`, {
      method: 'POST',
      body: JSON.stringify({ message }),
    }),

  getLabels: () => request<string[]>('/labels'),

  getAgentStatus: () => request<import('./types').AgentStatus>('/agent/status'),

  createProject: (data: { name: string; path: string }) =>
    request<import('./types').Project>('/projects', {
      method: 'POST',
      body: JSON.stringify(data),
    }),

  deleteProject: (name: string) =>
    request<{ message: string }>(`/projects/${encodeURIComponent(name)}`, {
      method: 'DELETE',
    }),

  useProject: (name: string) =>
    request<import('./types').Project>(`/projects/${encodeURIComponent(name)}/use`, {
      method: 'POST',
    }),

  listDirectories: (path: string) => {
    const search = new URLSearchParams({ path })
    return request<import('./types').DirEntry[]>(`/fs/list?${search.toString()}`)
  },

  searchDirectories: (query: string, limit: number = 50) => {
    const search = new URLSearchParams({ query, limit: String(limit) })
    return request<import('./types').DirEntry[]>(`/fs/search?${search.toString()}`)
  },

  getHomeDir: () => request<string>('/fs/home'),

  createExploreSession: (data: { projectId?: string; title?: string }) =>
    request<import('./types').ExploreSession>('/explore', {
      method: 'POST',
      body: JSON.stringify(data),
    }),

  listExploreSessions: (projectId: string) =>
    request<import('./types').ExploreSession[]>(`/explore?projectId=${encodeURIComponent(projectId)}`),

  getExploreSession: (id: string) =>
    request<import('./types').ExploreSessionWithMessages>(`/explore/${encodeURIComponent(id)}`),

  deleteExploreSession: (id: string) =>
    request<{ message: string }>(`/explore/${encodeURIComponent(id)}`, { method: 'DELETE' }),

  getStatus: () => request<{
    name: string
    path: string
    issues: number
    activeIssues: number
    issuesByStage: Record<string, number>
    llm: { configured: false; provider?: undefined; model?: undefined } | { configured: true; provider: string; model: string }
  }>('/status'),

  getAvailableModels: () => request<import('./types').ModelProvider[]>('/providers/models'),

  updateSessionModel: (sessionId: string, model: string, variant?: string) =>
    request<import('./types').ExploreSession>(`/explore/${encodeURIComponent(sessionId)}/model`, {
      method: 'POST',
      body: JSON.stringify({ model, variant }),
    }),

  getCurrentProject: async () => {
    const res = await fetch(`${BASE}/projects/current`, {
      headers: { 'Content-Type': 'application/json' },
    })
    const json: ApiResponse<import('./types').Project> = await res.json()
    if (!json.success) {
      return null
    }
    return json.data
  },

  getLogTail: (cursor?: number, limit?: number, maxBytes?: number) => {
    const search = new URLSearchParams()
    if (cursor != null) search.set('cursor', String(cursor))
    if (limit != null) search.set('limit', String(limit))
    if (maxBytes != null) search.set('maxBytes', String(maxBytes))
    const qs = search.toString()
    return request<import('./types').LogTailResult>(`/logs/tail${qs ? `?${qs}` : ''}`)
  },
}
