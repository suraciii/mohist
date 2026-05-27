import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { api } from '../../../shared/api/client'
import { useProject } from '../../project/@x/project-context'

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

export function useWorkflowTimeline(issueNumber: number, enabled: boolean = true) {
  const { projectId } = useProject()
  return useQuery({
    queryKey: ['issues', issueNumber, projectId, 'workflow-timeline'],
    queryFn: () => api.getWorkflowTimeline(issueNumber, projectId),
    enabled: enabled && issueNumber > 0 && !!projectId,
    refetchInterval: enabled ? 5000 : false,
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
