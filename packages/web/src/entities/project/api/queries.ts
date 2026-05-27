import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { api } from '../../../shared/api/client'
import type { AgentActivity, AgentRuntimeConfig, AgentSessionInfo, Epic, EpicDetail, EpicWithProgress, GeneralConfig, SystemInfo } from '../../../shared/api/types'
import { useProject } from '../model/ProjectContext'

export function useProjects() {
  return useQuery({
    queryKey: ['projects'],
    queryFn: () => api.getProjects(),
  })
}

export function useIssues(params?: { stage?: string; label?: string; projectId?: string }) {
  return useQuery({
    queryKey: ['issues', params],
    queryFn: () => api.getIssues(params),
    enabled: !!params?.projectId,
  })
}

export function useIssue(number: number) {
  const { projectId } = useProject()
  return useQuery({
    queryKey: ['issues', number, projectId],
    queryFn: () => api.getIssue(number, projectId),
    enabled: number > 0 && !!projectId,
  })
}

export function useLabels() {
  return useQuery({
    queryKey: ['labels'],
    queryFn: () => api.getLabels(),
  })
}

export function useIssueDiff(number: number) {
  const { projectId } = useProject()
  return useQuery({
    queryKey: ['issues', number, projectId, 'diff'],
    queryFn: () => api.getIssueDiff(number, projectId),
    enabled: number > 0 && !!projectId,
  })
}

export function useIssueCommits(number: number) {
  const { projectId } = useProject()
  return useQuery({
    queryKey: ['issues', number, projectId, 'commits'],
    queryFn: () => api.getIssueCommits(number, projectId),
    enabled: number > 0 && !!projectId,
  })
}

export function useCommitDiff(number: number, hash: string, enabled: boolean = false) {
  const { projectId } = useProject()
  return useQuery({
    queryKey: ['issues', number, projectId, 'commits', hash, 'diff'],
    queryFn: () => api.getCommitDiff(number, hash, projectId),
    enabled: enabled && number > 0 && !!hash && !!projectId,
  })
}

export function useAgentStatus() {
  return useQuery({
    queryKey: ['agent-status'],
    queryFn: () => api.getAgentStatus(),
    refetchInterval: 5000,
  })
}

export function useWorkflowTimeline(issueNumber: number, enabled: boolean = true) {
  const { projectId } = useProject()
  return useQuery({
    queryKey: ['issues', issueNumber, projectId, 'workflow-timeline'],
    queryFn: () => api.getWorkflowTimeline(issueNumber, projectId),
    enabled: enabled && issueNumber > 0 && !!projectId,
    refetchInterval: enabled ? 5000 : false,
  })
}

export function useAgentSessions(params?: { status?: string; limit?: number }) {
  const { projectId } = useProject()
  return useQuery<AgentSessionInfo[]>({
    queryKey: ['agent-sessions', params, projectId],
    queryFn: () => api.getAgentSessions({ ...params, projectId }),
    enabled: !!projectId,
  })
}

export function useAgentActivity(params?: { limit?: number }) {
  const { projectId } = useProject()
  return useQuery<AgentActivity>({
    queryKey: ['agent-activity', params, projectId],
    queryFn: () => api.getAgentActivity({ ...params, projectId }),
    enabled: !!projectId,
    refetchInterval: 5000,
  })
}

export function useCreateProject() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (data: { name: string; path: string }) => api.createProject(data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['projects'] })
      toast.success('Project created')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useDeleteProject() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (name: string) => api.deleteProject(name),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['projects'] })
      toast.success('Project deleted')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useUseProject() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (name: string) => api.useProject(name),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['projects'] })
      toast.success('Switched to project')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useStatus() {
  const { projectId } = useProject()
  return useQuery({
    queryKey: ['status', projectId],
    queryFn: () => api.getStatus(projectId),
    enabled: !!projectId,
    retry: false,
  })
}

export function useConfig() {
  return useQuery<GeneralConfig, Error>({
    queryKey: ['config'],
    queryFn: () => api.getConfig(),
  })
}

interface UpdateConfigContext {
  previousConfig?: GeneralConfig
}

const CONFIG_KEY_TO_PROPERTY: Record<string, keyof GeneralConfig> = {
  'agent.timeout': 'agentTimeout',
  'agent.maxConcurrent': 'maxConcurrentAgents',
  'poll.interval': 'pollInterval',
}

export function useUpdateConfig() {
  const queryClient = useQueryClient()

  return useMutation<GeneralConfig, Error, { key: string; value: number }, UpdateConfigContext>({
    mutationFn: ({ key, value }) => api.updateConfig(key, value),
    onMutate: async ({ key, value }) => {
      await queryClient.cancelQueries({ queryKey: ['config'] })
      const previousConfig = queryClient.getQueryData<GeneralConfig>(['config'])

      const prop = CONFIG_KEY_TO_PROPERTY[key]
      if (prop) {
        queryClient.setQueryData<GeneralConfig>(['config'], (old) => {
          if (!old) return old
          return { ...old, [prop]: value }
        })
      }

      return { previousConfig }
    },
    onError: (_err, _variables, context) => {
      if (context?.previousConfig) {
        queryClient.setQueryData(['config'], context.previousConfig)
      }
      toast.error(_err.message || 'Request failed')
    },
    onSuccess: () => {
      toast.success('Setting updated')
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['config'] })
    },
  })
}

export function useWorktreeStatus(issueNumber: number, enabled: boolean) {
  const { projectId } = useProject()
  return useQuery({
    queryKey: ['issues', issueNumber, projectId, 'worktree-status'],
    queryFn: () => api.getWorktreeStatus(issueNumber, projectId),
    enabled: enabled && issueNumber > 0 && !!projectId,
    refetchInterval: 30_000,
  })
}

export function useArchivedIssues(params?: { projectId?: string }) {
  return useQuery({
    queryKey: ['archived-issues', params],
    queryFn: async () => {
      const issues = await api.getIssues({ ...params })
      return issues.filter(i => i.archivedAt != null)
    },
    enabled: !!params?.projectId,
  })
}

export function useUnarchiveIssue() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation({
    mutationFn: (number: number) => api.unarchiveIssue(number, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['archived-issues'] })
      toast.success('Issue unarchived')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useOpencodeModel() {
  return useQuery<{ model: string | null }>({
    queryKey: ['opencode-model'],
    queryFn: () => api.getOpencodeModel(),
  })
}

export function useUpdateOpencodeModel() {
  const queryClient = useQueryClient()
  return useMutation<{ model: string | null }, Error, string | null>({
    mutationFn: (model) => api.updateOpencodeModel(model),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['opencode-model'] })
      toast.success('Model updated')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useAvailableModelIds() {
  return useQuery<string[]>({
    queryKey: ['opencode-model-ids'],
    queryFn: async () => {
      const response = await api.getOpencodeModels()
      return response.models
    },
  })
}

export function useOpencodeRuntime() {
  return useQuery<{ mode: string; command: string; model: string | null; note: string }, Error>({
    queryKey: ['opencode-runtime'],
    queryFn: () => api.getOpencodeRuntime(),
  })
}

export function useRebuildSystem() {
  const queryClient = useQueryClient()
  return useMutation<{ success: boolean }, Error, void>({
    mutationFn: async () => {
      throw new Error('Server rebuild is not managed by Mohist Web')
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['status'] })
      toast.success('Rebuild started')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useModel() {
  return useQuery<{ model: string | null }>({
    queryKey: ['model'],
    queryFn: () => api.getModel(),
  })
}

export function useSetModel() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (model: string | null) => api.setModel(model),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['model'] })
      toast.success('Model updated')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useOpencodeModelConfig() {
  return useQuery<{ model: string | null }>({
    queryKey: ['opencode-model-config'],
    queryFn: () => api.getOpencodeModelConfig(),
  })
}

export function useSetOpencodeModelConfig() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (model: string | null) => api.setOpencodeModel(model),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['opencode-model-config'] })
      toast.success('Model updated')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useLogLevel() {
  return useQuery<{ level: string }>({
    queryKey: ['log-level'],
    queryFn: () => api.getLogLevel(),
  })
}

export function useSetLogLevel() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (level: string) => api.setLogLevel(level),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['log-level'] })
      toast.success('Log level updated')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useAgentRuntime() {
  return useQuery<AgentRuntimeConfig>({
    queryKey: ['agent-runtime'],
    queryFn: () => api.getAgentRuntime(),
  })
}

export function useSetAgentRuntime() {
  const queryClient = useQueryClient()
  return useMutation<AgentRuntimeConfig, Error, Partial<AgentRuntimeConfig>>({
    mutationFn: (data) => api.updateAgentRuntime(data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['agent-runtime'] })
      toast.success('Coder agent runtime updated')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useStageModels() {
  return useQuery<{ stageModels: Record<string, string> | null }>({
    queryKey: ['stage-models'],
    queryFn: () => api.getStageModels(),
  })
}

export function useSetStageModels() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (stageModels: Record<string, string> | null) => api.setStageModels(stageModels),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['stage-models'] })
      toast.success('Stage models updated')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useSystemInfo() {
  return useQuery<SystemInfo>({
    queryKey: ['system-info'],
    queryFn: () => api.getSystemInfo(),
  })
}

export function useEpics() {
  const { projectId } = useProject()
  return useQuery<EpicWithProgress[]>({
    queryKey: ['epics', projectId],
    queryFn: () => api.getEpics({ projectId: projectId ?? undefined }),
    enabled: !!projectId,
  })
}

export function useEpic(id: string) {
  const { projectId } = useProject()
  return useQuery<EpicDetail>({
    queryKey: ['epics', projectId, id],
    queryFn: () => api.getEpic(id, { projectId: projectId ?? undefined }),
    enabled: !!projectId && !!id,
  })
}

export function useCreateEpic() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<Epic, Error, { title: string; description: string; priority: string }>({
    mutationFn: (data) => api.createEpic({ ...data, projectId: projectId ?? undefined }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['epics'] })
      toast.success('Epic created')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useAddEpicIssue() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<{ epicId: string; issueId: string }, Error, { epicId: string; issueId: string }>({
    mutationFn: ({ epicId, issueId }) => api.addEpicIssue(epicId, issueId, projectId),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({ queryKey: ['epics'] })
      queryClient.invalidateQueries({ queryKey: ['epics', variables.epicId] })
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      toast.success('Issue added to Epic')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useRemoveEpicIssue() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<{ epicId: string; issueId: string }, Error, { epicId: string; issueId: string }>({
    mutationFn: ({ epicId, issueId }) => api.removeEpicIssue(epicId, issueId, projectId),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({ queryKey: ['epics'] })
      queryClient.invalidateQueries({ queryKey: ['epics', variables.epicId] })
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      toast.success('Issue removed from Epic')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useMarkEpicDone() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<Epic, Error, string>({
    mutationFn: (id) => api.markEpicDone(id, projectId),
    onSuccess: (_data, id) => {
      queryClient.invalidateQueries({ queryKey: ['epics'] })
      queryClient.invalidateQueries({ queryKey: ['epics', id] })
      toast.success('Epic marked as done')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useCloseEpic() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<Epic, Error, string>({
    mutationFn: (id) => api.closeEpic(id, projectId),
    onSuccess: (_data, id) => {
      queryClient.invalidateQueries({ queryKey: ['epics'] })
      queryClient.invalidateQueries({ queryKey: ['epics', id] })
      toast.success('Epic closed')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}
