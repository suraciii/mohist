import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../lib/api'

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
    mutationFn: (data: { projectId?: string; title?: string }) => api.createExploreSession(data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['explore-sessions'] })
    },
  })
}
