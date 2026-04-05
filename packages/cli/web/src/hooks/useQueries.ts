import { useQuery } from '@tanstack/react-query'
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
