import type { ApiResponse } from './types'

const BASE = '/api'

class ApiError extends Error {
  constructor(
    message: string,
    public readonly status: number,
    public readonly data?: unknown,
    public readonly code?: string,
    public readonly details?: unknown,
  ) {
    super(message)
    this.name = 'ApiError'
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    headers: { 'Content-Type': 'application/json' },
    ...init,
  })
  const json: ApiResponse<T> = await res.json()
  if (!json.success) {
    throw new ApiError(
      json.error ?? `Request failed: ${res.status}`,
      res.status,
      json.data,
      json.code,
      json.details,
    )
  }
  return json.data as T
}

function withProject(path: string, projectId?: string | null): string {
  if (!projectId) return path
  const separator = path.includes('?') ? '&' : '?'
  return `${path}${separator}projectId=${encodeURIComponent(projectId)}`
}

export { ApiError }

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

  getIssue: (number: number, projectId?: string | null) =>
    request<import('./types').Issue>(withProject(`/issues/${number}`, projectId)),

  createIssue: (data: { title: string; body?: string; labels?: string[]; model?: string; priority?: string; projectId?: string }) =>
    request<import('./types').Issue>('/issues', {
      method: 'POST',
      body: JSON.stringify(data),
    }),

  updateIssue: (number: number, data: { title?: string; body?: string; addLabels?: string[]; removeLabels?: string[]; model?: string | null; stageModels?: Record<string, string> | null; priority?: string | null }, projectId?: string | null) =>
    request<import('./types').Issue>(withProject(`/issues/${number}`, projectId), {
      method: 'PATCH',
      body: JSON.stringify(data),
    }),

  startIssue: (number: number, projectId?: string | null) =>
    request<{ issue: import('./types').Issue; message: string }>(withProject(`/issues/${number}/start`, projectId), { method: 'POST' }),

  closeIssue: (number: number, projectId?: string | null) =>
    request<{ issue: import('./types').Issue; message: string }>(withProject(`/issues/${number}/close`, projectId), { method: 'POST' }),

  reopenIssue: (number: number, projectId?: string | null) =>
    request<{ issue: import('./types').Issue; message: string }>(withProject(`/issues/${number}/reopen`, projectId), { method: 'POST' }),

  resumeIssue: (number: number, projectId?: string | null) =>
    request<{ issue: import('./types').Issue; message: string }>(withProject(`/issues/${number}/resume`, projectId), { method: 'POST' }),

  retryIssue: (number: number, projectId?: string | null) =>
    request<{ issue: import('./types').Issue; message: string }>(withProject(`/issues/${number}/retry`, projectId), { method: 'POST' }),

  approveIssue: (number: number, projectId?: string | null) =>
    request<{ issue: import('./types').Issue; context: string | null; message: string }>(withProject(`/issues/${number}/approve`, projectId), { method: 'POST' }),

  rejectIssue: (number: number, data: { message?: string }, projectId?: string | null) =>
    request<{ issue: import('./types').Issue; message: string }>(withProject(`/issues/${number}/reject`, projectId), {
      method: 'POST',
      body: JSON.stringify(data),
    }),

  getIssueDiff: (number: number, projectId?: string | null) =>
    request<import('./types').IssueDiffResponse>(withProject(`/issues/${number}/diff`, projectId)),

  getIssueCommits: (number: number, projectId?: string | null) =>
    request<import('./types').IssueCommitsResponse>(withProject(`/issues/${number}/commits`, projectId)),

  getCommitDiff: (number: number, hash: string, projectId?: string | null) =>
    request<import('./types').CommitDiffResponse>(withProject(`/issues/${number}/commits/${hash}/diff`, projectId)),

  getFileContent: (number: number, filePath: string, projectId?: string | null) =>
    request<{ base: string; head: string }>(withProject(`/issues/${number}/file-content?path=${encodeURIComponent(filePath)}`, projectId)),

  addComment: (issueNumber: number, body: string, projectId?: string | null) =>
    request<import('./types').Comment>(withProject(`/issues/${issueNumber}/comments`, projectId), {
      method: 'POST',
      body: JSON.stringify({ body }),
    }),

  deleteComment: (issueNumber: number, commentId: string, projectId?: string | null) =>
    request<{ message: string }>(withProject(`/issues/${issueNumber}/comments/${commentId}`, projectId), {
      method: 'DELETE',
    }),

  getLabels: () => request<string[]>('/labels'),

  getAgentStatus: () => request<import('./types').AgentStatus>('/agent/status'),

  getAgentSessions: (params?: { status?: string; limit?: number; projectId?: string | null }) => {
    const search = new URLSearchParams()
    if (params?.projectId) search.set('projectId', params.projectId)
    if (params?.status) search.set('status', params.status)
    if (params?.limit != null) search.set('limit', String(params.limit))
    const qs = search.toString()
    return request<import('./types').AgentSessionInfo[]>(`/agent/sessions${qs ? `?${qs}` : ''}`)
  },

  getAgentActivity: (params?: { limit?: number; projectId?: string | null }) => {
    const search = new URLSearchParams()
    if (params?.projectId) search.set('projectId', params.projectId)
    if (params?.limit != null) search.set('limit', String(params.limit))
    const qs = search.toString()
    return request<import('./types').AgentActivity>(`/agent/activity${qs ? `?${qs}` : ''}`)
  },

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

  getStatus: (projectId?: string | null) => request<{
    name: string
    path: string
    issues: number
    activeIssues: number
    issuesByStage: Record<string, number>
    llm: { configured: false; provider?: undefined; model?: undefined } | { configured: true; provider: string; model: string }
    version: string | null
    gitHash: string | null
    sourceHead: string | null
    upToDate: boolean
  }>(withProject('/status', projectId)),

  getAvailableModels: () => request<import('./types').ModelProvider[]>('/providers/models'),

  getCoderSessions: (number: number, projectId?: string | null) =>
    request<import('./types').CoderSessionSummary[]>(withProject(`/issues/${number}/coder-sessions`, projectId)),

  getCoderSessionDetail: (number: number, sessionId: string, projectId?: string | null) =>
    request<import('./types').CoderSessionDetail>(withProject(`/issues/${number}/coder-sessions/${sessionId}`, projectId)),

  getWorkflowLogs: (number: number, projectId?: string | null) =>
    request<import('./types').WorkflowLogItem[]>(withProject(`/issues/${number}/logs`, projectId)),

  getWorkflowTimeline: (number: number, projectId?: string | null) =>
    request<import('./types').WorkflowTimeline>(withProject(`/issues/${number}/workflow/timeline`, projectId)),

  rebaseIssue: async (number: number, projectId?: string | null) => {
    try {
      return await request<{ rebased: boolean; rePlan?: boolean; conflicts?: string[]; buildPassed?: boolean; message: string; status?: 'queued' | 'resolving-conflicts'; workflowRunId?: string; taskId?: string; stage?: string; baseBranch?: string }>(withProject(`/issues/${number}/rebase`, projectId), { method: 'POST' })
    } catch (err) {
      if (err instanceof ApiError && err.data && typeof err.data === 'object') {
        return err.data as { rebased: boolean; rePlan?: boolean; conflicts?: string[]; buildPassed?: boolean; message: string; status?: 'queued' | 'resolving-conflicts'; workflowRunId?: string; taskId?: string; stage?: string; baseBranch?: string }
      }
      throw err
    }
  },

  rerunIssue: (number: number, projectId?: string | null) =>
    request<{ issue: import('./types').Issue; message: string }>(withProject(`/issues/${number}/rerun`, projectId), { method: 'POST' }),

  forceStopIssue: (number: number, projectId?: string | null) =>
    request<{ ok: boolean; issueNumber: number }>(withProject(`/issues/${number}/force-stop`, projectId), { method: 'POST' }),

  getConfig: () => request<import('./types').GeneralConfig>('/config'),

  updateConfig: (key: string, value: number) =>
    request<import('./types').GeneralConfig>(`/config/${encodeURIComponent(key)}`, {
      method: 'PUT',
      body: JSON.stringify({ value }),
    }),

  getLogTail: (cursor?: number, limit?: number, maxBytes?: number) => {
    const search = new URLSearchParams()
    if (cursor != null) search.set('cursor', String(cursor))
    if (limit != null) search.set('limit', String(limit))
    if (maxBytes != null) search.set('maxBytes', String(maxBytes))
    const qs = search.toString()
    return request<import('./types').LogTailResult>(`/logs/tail${qs ? `?${qs}` : ''}`)
  },

  getWorktreeStatus: (number: number, projectId?: string | null) =>
    request<{
      exists: boolean
      branch?: string
      baseBranch?: string
      ahead?: number
      behind?: number
      rebaseInProgress?: boolean
      conflictingFiles?: string[]
    }>(withProject(`/issues/${number}/worktree-status`, projectId)),

  cleanupIssueWorktree: (number: number, projectId?: string | null) =>
    request<{
      removed: boolean
      message: string
      resources: Array<{ type: string; status: string; path?: string | null; reason?: string | null }>
    }>(withProject(`/issues/${number}/cleanup`, projectId), { method: 'POST' }),

  archiveIssue: (number: number, projectId?: string | null) =>
    request<{ issue: import('./types').Issue; message: string; warning?: string }>(withProject(`/issues/${number}/archive`, projectId), { method: 'POST' }),

  unarchiveIssue: (number: number, projectId?: string | null) =>
    request<{ issue: import('./types').Issue; message: string }>(withProject(`/issues/${number}/unarchive`, projectId), { method: 'POST' }),

  archiveAllCompleted: (projectId?: string | null) =>
    request<{ archived: number; skipped: number; skippedNumbers: number[]; message: string }>(withProject('/issues/archive-completed', projectId), { method: 'POST' }),

  getOpencodeModel: () =>
    request<{ model: string | null }>('/opencode-model'),

  updateOpencodeModel: (model: string | null) =>
    request<{ model: string | null }>('/opencode-model', {
      method: 'PUT',
      body: JSON.stringify({ model }),
    }),

  getModel: () =>
    request<{ model: string | null }>('/model'),

  setModel: (model: string | null) =>
    request<{ model: string | null }>('/model', {
      method: 'PUT',
      body: JSON.stringify({ model }),
    }),

  getOpencodeModelConfig: () =>
    request<{ model: string | null }>('/opencode-model'),

  setOpencodeModel: (model: string | null) =>
    request<{ model: string | null }>('/opencode-model', {
      method: 'PUT',
      body: JSON.stringify({ model }),
    }),

  getLogLevel: () =>
    request<{ level: string }>('/log-level'),

  setLogLevel: (level: string) =>
    request<{ level: string }>('/log-level', {
      method: 'PUT',
      body: JSON.stringify({ level }),
    }),

  getAgentRuntime: () =>
    request<import('./types').AgentRuntimeConfig>('/agent-runtime'),

  updateAgentRuntime: (data: Partial<import('./types').AgentRuntimeConfig>) =>
    request<import('./types').AgentRuntimeConfig>('/agent-runtime', {
      method: 'PUT',
      body: JSON.stringify(data),
    }),

  getStageModels: () =>
    request<{ stageModels: Record<string, string> | null }>('/stage-models'),

  setStageModels: (stageModels: Record<string, string> | null) =>
    request<{ stageModels: Record<string, string> | null }>('/stage-models', {
      method: 'PUT',
      body: JSON.stringify({ stageModels }),
    }),

  getSystemInfo: () =>
    request<import('./types').SystemInfo>('/system/info'),

  addPrerequisite: (number: number, prerequisiteNumber: number, projectId?: string | null) =>
    request<{ issue: import('./types').Issue; message: string }>(withProject(`/issues/${number}/prerequisites`, projectId), {
      method: 'POST',
      body: JSON.stringify({ prerequisiteNumber }),
    }),

  removePrerequisite: (number: number, prerequisiteNumber: number, projectId?: string | null) =>
    request<{ issue: import('./types').Issue; message: string }>(withProject(`/issues/${number}/prerequisites/${prerequisiteNumber}`, projectId), {
      method: 'DELETE',
    }),

  getEpics: (params?: { projectId?: string }) => {
    const search = new URLSearchParams()
    if (params?.projectId) search.set('projectId', params.projectId)
    const qs = search.toString()
    return request<import('./types').EpicWithProgress[]>(`/epics${qs ? `?${qs}` : ''}`)
  },

  getEpic: (id: string, params?: { projectId?: string }) => {
    const search = new URLSearchParams()
    if (params?.projectId) search.set('projectId', params.projectId)
    const qs = search.toString()
    return request<import('./types').EpicDetail>(`/epics/${encodeURIComponent(id)}${qs ? `?${qs}` : ''}`)
  },

  createEpic: (data: { title: string; description: string; priority: string; projectId?: string }) =>
    request<import('./types').Epic>('/epics', {
      method: 'POST',
      body: JSON.stringify(data),
    }),

  addEpicIssue: (epicId: string, issueId: string, projectId?: string | null) =>
    request<{ epicId: string; issueId: string }>(withProject(`/epics/${encodeURIComponent(epicId)}/issues`, projectId), {
      method: 'POST',
      body: JSON.stringify({ issueId }),
    }),

  removeEpicIssue: (epicId: string, issueId: string, projectId?: string | null) =>
    request<{ epicId: string; issueId: string }>(withProject(`/epics/${encodeURIComponent(epicId)}/issues/${encodeURIComponent(issueId)}`, projectId), {
      method: 'DELETE',
    }),

  markEpicDone: (id: string, projectId?: string | null) =>
    request<import('./types').Epic>(withProject(`/epics/${encodeURIComponent(id)}/done`, projectId), { method: 'POST' }),

  closeEpic: (id: string, projectId?: string | null) =>
    request<import('./types').Epic>(withProject(`/epics/${encodeURIComponent(id)}/close`, projectId), { method: 'POST' }),

  }
