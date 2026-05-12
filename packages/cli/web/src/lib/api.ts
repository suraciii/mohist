import type { ApiResponse } from './types'

const BASE = '/api'

class ApiError extends Error {
  constructor(
    message: string,
    public readonly status: number,
    public readonly data?: unknown,
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
    )
  }
  return json.data as T
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

  getIssue: (number: number) =>
    request<import('./types').Issue>(`/issues/${number}`),

  createIssue: (data: { title: string; body?: string; labels?: string[]; model?: string }) =>
    request<import('./types').Issue>('/issues', {
      method: 'POST',
      body: JSON.stringify(data),
    }),

  updateIssue: (number: number, data: { title?: string; body?: string; addLabels?: string[]; removeLabels?: string[]; model?: string | null; stageModels?: Record<string, string> | null }) =>
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

  resumeIssue: (number: number) =>
    request<{ issue: import('./types').Issue; message: string }>(`/issues/${number}/resume`, { method: 'POST' }),

  retryIssue: (number: number) =>
    request<{ issue: import('./types').Issue; message: string }>(`/issues/${number}/retry`, { method: 'POST' }),

  approveIssue: (number: number) =>
    request<{ issue: import('./types').Issue; context: string | null; message: string }>(`/issues/${number}/approve`, { method: 'POST' }),

  rejectIssue: (number: number, data: { message?: string }) =>
    request<{ issue: import('./types').Issue; message: string }>(`/issues/${number}/reject`, {
      method: 'POST',
      body: JSON.stringify(data),
    }),

  getIssueDiff: (number: number) =>
    request<import('./types').IssueDiffResponse>(`/issues/${number}/diff`),

  getIssueCommits: (number: number) =>
    request<import('./types').IssueCommitsResponse>(`/issues/${number}/commits`),

  getCommitDiff: (number: number, hash: string) =>
    request<import('./types').CommitDiffResponse>(`/issues/${number}/commits/${hash}/diff`),

  addComment: (issueNumber: number, body: string) =>
    request<import('./types').Comment>(`/issues/${issueNumber}/comments`, {
      method: 'POST',
      body: JSON.stringify({ body }),
    }),

  deleteComment: (issueNumber: number, commentId: string) =>
    request<{ message: string }>(`/issues/${issueNumber}/comments/${commentId}`, {
      method: 'DELETE',
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

  getAgentSessions: (params?: { status?: string; limit?: number }) => {
    const search = new URLSearchParams()
    if (params?.status) search.set('status', params.status)
    if (params?.limit != null) search.set('limit', String(params.limit))
    const qs = search.toString()
    return request<import('./types').AgentSessionInfo[]>(`/agent/sessions${qs ? `?${qs}` : ''}`)
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

  createExploreSession: (data: { projectId?: string; title?: string; issueId?: string }) =>
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
    version: string | null
    gitHash: string | null
    sourceHead: string | null
    upToDate: boolean
  }>('/status'),

  getAvailableModels: () => request<import('./types').ModelProvider[]>('/providers/models'),

  updateSessionModel: (sessionId: string, model: string, variant?: string) =>
    request<import('./types').ExploreSession>(`/explore/${encodeURIComponent(sessionId)}/model`, {
      method: 'POST',
      body: JSON.stringify({ model, variant }),
    }),

  updateExploreSessionTitle: (sessionId: string, title: string) =>
    request<import('./types').ExploreSession>(`/explore/${encodeURIComponent(sessionId)}`, {
      method: 'PATCH',
      body: JSON.stringify({ title }),
    }),

  getAgentSession: (number: number) =>
    request<import('./types').AgentSessionMessageItem[]>(`/issues/${number}/agent-session`),

  getCoderSessions: (number: number) =>
    request<import('./types').CoderSessionItem[]>(`/issues/${number}/coder-sessions`),

  getCoderSessionDetail: (number: number, sessionId: string) =>
    request<import('./types').CoderSessionDetail>(`/issues/${number}/coder-sessions/${sessionId}`),

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

  getWorkflowLogs: (number: number) =>
    request<import('./types').WorkflowLogItem[]>(`/issues/${number}/logs`),

  rebaseIssue: async (number: number, reEvalPlan?: boolean) => {
    try {
      return await request<{ rebased: boolean; rePlan?: boolean; conflicts?: string[]; buildPassed?: boolean; message: string; status?: 'resolving-conflicts' }>(`/issues/${number}/rebase`, { method: 'POST', body: reEvalPlan ? JSON.stringify({ reEvalPlan: true }) : undefined })
    } catch (err) {
      if (err instanceof ApiError && err.data && typeof err.data === 'object') {
        return err.data as { rebased: boolean; rePlan?: boolean; conflicts?: string[]; buildPassed?: boolean; message: string; status?: 'resolving-conflicts' }
      }
      throw err
    }
  },

  retryMerge: (number: number) =>
    request<{ issue: import('./types').Issue; message: string }>(`/issues/${number}/retry-merge`, { method: 'POST' }),

  rerunIssue: (number: number) =>
    request<{ issue: import('./types').Issue; message: string }>(`/issues/${number}/rerun`, { method: 'POST' }),

  forceStopIssue: (number: number) =>
    request<{ ok: boolean; issueNumber: number }>(`/issues/${number}/force-stop`, { method: 'POST' }),

  getBuildStatus: (number: number) =>
    request<import('./types').BuildStatus>(`/issues/${number}/build-status`),

  getTasks: (number: number) =>
    request<{ version: number; tasks: import('./types').Task[] }>(`/issues/${number}/tasks`),

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

  getWorktreeStatus: (number: number) =>
    request<{
      exists: boolean
      branch?: string
      baseBranch?: string
      ahead?: number
      behind?: number
      rebaseInProgress?: boolean
      conflictingFiles?: string[]
    }>(`/issues/${number}/worktree-status`),

  getIssueExecutions: (number: number) =>
    request<import('./types').StageExecution[]>(`/issues/${number}/executions`),

  archiveIssue: (number: number) =>
    request<{ issue: import('./types').Issue; message: string; warning?: string }>(`/issues/${number}/archive`, { method: 'POST' }),

  unarchiveIssue: (number: number) =>
    request<{ issue: import('./types').Issue; message: string }>(`/issues/${number}/unarchive`, { method: 'POST' }),

  archiveAllCompleted: () =>
    request<{ archived: number; skipped: number; skippedNumbers: number[]; message: string }>('/issues/archive-completed', { method: 'POST' }),

  getOpencodeModel: () =>
    request<{ model: string | null }>('/opencode-model'),

  updateOpencodeModel: (model: string | null) =>
    request<{ model: string | null }>('/opencode-model', {
      method: 'PUT',
      body: JSON.stringify({ model }),
    }),

  getOpencodeModels: () =>
    request<string[]>('/opencode/models'),

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

  getIssueStageState: (number: number) =>
    request<import('./types').IssueStageStateResponse>(`/issues/${number}/stage-state`),

  getWorkflowRun: (number: number) =>
    request<import('./types').WorkflowRun>(`/issues/${number}/workflow-run`),

  rebuildSystem: () =>
    request<{ success: boolean }>('/settings/system/rebuild', { method: 'POST' }),

  }
