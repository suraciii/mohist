import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { api } from '../lib/api'
import type { AgentRuntimeConfig, AgentSessionInfo, GeneralConfig, IssueStageStateResponse, StageExecution, SystemInfo } from '../lib/types'
import { providerApi, type Provider, type ProviderFormData } from '../lib/provider-api'

export function useProjects() {
  return useQuery({
    queryKey: ['projects'],
    queryFn: () => api.getProjects(),
  })
}

export function useCurrentProject() {
  return useQuery({
    queryKey: ['current-project'],
    queryFn: () => api.getCurrentProject(),
    retry: false,
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
  return useQuery({
    queryKey: ['issues', number],
    queryFn: () => api.getIssue(number),
    enabled: number > 0,
  })
}

export function useLabels() {
  return useQuery({
    queryKey: ['labels'],
    queryFn: () => api.getLabels(),
  })
}

export function useIssueDiff(number: number) {
  return useQuery({
    queryKey: ['issues', number, 'diff'],
    queryFn: () => api.getIssueDiff(number),
    enabled: number > 0,
  })
}

export function useTasks(number: number) {
  return useQuery({
    queryKey: ['issues', number, 'tasks'],
    queryFn: () => api.getTasks(number),
    enabled: number > 0,
    refetchInterval: 5000,
  })
}

export function useBuildStatus(number: number) {
  return useQuery({
    queryKey: ['issues', number, 'build-status'],
    queryFn: () => api.getBuildStatus(number),
    enabled: number > 0,
    refetchInterval: 5000,
  })
}

export function useIssueCommits(number: number) {
  return useQuery({
    queryKey: ['issues', number, 'commits'],
    queryFn: () => api.getIssueCommits(number),
    enabled: number > 0,
  })
}

export function useCommitDiff(number: number, hash: string, enabled: boolean = false) {
  return useQuery({
    queryKey: ['issues', number, 'commits', hash, 'diff'],
    queryFn: () => api.getCommitDiff(number, hash),
    enabled: enabled && number > 0 && !!hash,
  })
}

export function useAgentStatus() {
  return useQuery({
    queryKey: ['agent-status'],
    queryFn: () => api.getAgentStatus(),
    refetchInterval: 5000,
  })
}

export function useAgentSessions(params?: { status?: string; limit?: number }) {
  return useQuery<AgentSessionInfo[]>({
    queryKey: ['agent-sessions', params],
    queryFn: () => api.getAgentSessions(params),
  })
}

export function useQuestions(issueId: string) {
  return useQuery({
    queryKey: ['questions', issueId],
    queryFn: () => api.getQuestions(issueId),
    enabled: !!issueId,
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

export function useSendMessage(issueNumber: number) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (message: string) => api.sendMessage(issueNumber, message),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      toast.success('Message sent')
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

export function useExploreSession(id: string) {
  return useQuery({
    queryKey: ['explore', id],
    queryFn: () => api.getExploreSession(id),
    enabled: !!id,
  })
}

export function useExploreSessions(projectId: string) {
  return useQuery({
    queryKey: ['explore-sessions', projectId],
    queryFn: () => api.listExploreSessions(projectId),
    enabled: !!projectId,
  })
}

export function useCreateExploreSession() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (data: { projectId?: string; title?: string; issueId?: string }) => api.createExploreSession(data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['explore-sessions'] })
      toast.success('Explore session created')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useUpdateExploreSessionTitle() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ sessionId, title }: { sessionId: string; title: string }) =>
      api.updateExploreSessionTitle(sessionId, title),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({ queryKey: ['explore-sessions'] })
      queryClient.invalidateQueries({ queryKey: ['explore', variables.sessionId] })
      toast.success('Title updated')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useStatus() {
  return useQuery({
    queryKey: ['status'],
    queryFn: () => api.getStatus(),
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

export function useProviders() {
  return useQuery<Provider[], Error>({
    queryKey: ['providers'],
    queryFn: () => providerApi.getProviders(),
  })
}

export interface SaveProviderVariables {
  id: string
  data: ProviderFormData
}

interface SaveProviderContext {
  previousProviders?: Provider[]
}

export function useSaveProvider() {
  const queryClient = useQueryClient()

  return useMutation<{ id: string; configured: boolean }, Error, SaveProviderVariables, SaveProviderContext>({
    mutationFn: ({ id, data }) => providerApi.saveProvider(id, data),
    onMutate: async ({ id, data }) => {
      await queryClient.cancelQueries({ queryKey: ['providers'] })
      const previousProviders = queryClient.getQueryData<Provider[]>(['providers'])

      queryClient.setQueryData<Provider[]>(['providers'], (old) => {
        if (!old) return old
        return old.map((p) =>
          p.id === id
            ? { ...p, configured: true, apiKeyMasked: maskApiKey(data.apiKey), ...data }
            : p
        )
      })

      return { previousProviders }
    },
    onError: (_err, _variables, context) => {
      if (context?.previousProviders) {
        queryClient.setQueryData(['providers'], context.previousProviders)
      }
      toast.error(_err.message || 'Request failed')
    },
    onSuccess: () => {
      toast.success('Provider saved')
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['providers'] })
    },
  })
}

interface DeleteProviderContext {
  previousProviders?: Provider[]
}

export function useDeleteProvider() {
  const queryClient = useQueryClient()

  return useMutation<{ id: string }, Error, string, DeleteProviderContext>({
    mutationFn: (id) => providerApi.deleteProvider(id),
    onMutate: async (id) => {
      await queryClient.cancelQueries({ queryKey: ['providers'] })
      const previousProviders = queryClient.getQueryData<Provider[]>(['providers'])

      queryClient.setQueryData<Provider[]>(['providers'], (old) => {
        if (!old) return old
        return old.map((p) =>
          p.id === id ? { ...p, configured: false, apiKeyMasked: null } : p
        )
      })

      return { previousProviders }
    },
    onError: (_err, _id, context) => {
      if (context?.previousProviders) {
        queryClient.setQueryData(['providers'], context.previousProviders)
      }
      toast.error(_err.message || 'Request failed')
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['providers'] })
      toast.success('Provider deleted')
    },
  })
}

export interface TestProviderVariables {
  data: ProviderFormData & { id?: string }
}

export function useTestProvider() {
  return useMutation<{ success: boolean }, Error, TestProviderVariables>({
    mutationFn: ({ data }) => providerApi.testProvider(data),
    onSuccess: () => {
      toast.success('Provider test passed')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useWorktreeStatus(issueNumber: number, enabled: boolean) {
  return useQuery({
    queryKey: ['issues', issueNumber, 'worktree-status'],
    queryFn: () => api.getWorktreeStatus(issueNumber),
    enabled: enabled && issueNumber > 0,
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
  return useMutation({
    mutationFn: (number: number) => api.unarchiveIssue(number),
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

export function useOpencodeModels() {
  return useQuery<string[]>({
    queryKey: ['opencode-models'],
    queryFn: () => api.getOpencodeModels(),
  })
}

export function useRebuildSystem() {
  const queryClient = useQueryClient()
  return useMutation<{ success: boolean }, Error, void>({
    mutationFn: () => api.rebuildSystem(),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['status'] })
      toast.success('Rebuild started')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

function maskApiKey(apiKey: string): string {
  if (apiKey.length <= 8) return '********'
  return apiKey.slice(0, 4) + '*'.repeat(apiKey.length - 8) + apiKey.slice(-4)
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
      toast.success('Agent runtime updated')
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

export function useIssueExecutions(number: number) {
  return useQuery<StageExecution[]>({
    queryKey: ['issues', number, 'executions'],
    queryFn: () => api.getIssueExecutions(number),
    enabled: number > 0,
  })
}

export function useIssueStageState(number: number) {
  return useQuery<IssueStageStateResponse>({
    queryKey: ['issues', number, 'stage-state'],
    queryFn: () => api.getIssueStageState(number),
    enabled: number > 0,
    refetchInterval: 5000,
  })
}

export function useWorkflowRun(number: number) {
  return useQuery<import('../lib/types').WorkflowRun>({
    queryKey: ['issues', number, 'workflow-run'],
    queryFn: () => api.getWorkflowRun(number),
    enabled: number > 0,
    refetchInterval: 5000,
  })
}
