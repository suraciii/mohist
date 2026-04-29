import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../lib/api'
import type { GeneralConfig } from '../lib/types'
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
    },
  })
}

export function useDeleteProject() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (name: string) => api.deleteProject(name),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['projects'] })
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
    },
  })
}

export function useUseProject() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (name: string) => api.useProject(name),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['projects'] })
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

export function useUpdateConfig() {
  const queryClient = useQueryClient()

  return useMutation<GeneralConfig, Error, { key: string; value: number }, UpdateConfigContext>({
    mutationFn: ({ key, value }) => api.updateConfig(key, value),
    onMutate: async ({ key, value }) => {
      await queryClient.cancelQueries({ queryKey: ['config'] })
      const previousConfig = queryClient.getQueryData<GeneralConfig>(['config'])

      queryClient.setQueryData<GeneralConfig>(['config'], (old) => {
        if (!old) return old
        return { ...old, [key]: value }
      })

      return { previousConfig }
    },
    onError: (_err, _variables, context) => {
      if (context?.previousConfig) {
        queryClient.setQueryData(['config'], context.previousConfig)
      }
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
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['providers'] })
    },
  })
}

export interface TestProviderVariables {
  data: ProviderFormData & { id?: string }
}

export function useTestProvider() {
  return useMutation<{ success: boolean }, Error, TestProviderVariables>({
    mutationFn: ({ data }) => providerApi.testProvider(data),
  })
}

function maskApiKey(apiKey: string): string {
  if (apiKey.length <= 8) return '********'
  return apiKey.slice(0, 4) + '*'.repeat(apiKey.length - 8) + apiKey.slice(-4)
}
