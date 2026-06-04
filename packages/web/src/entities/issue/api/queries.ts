import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { useProject } from '../../project/@x/project-context'
import type { IssueWorkflowProfileYamlResponse } from '../model/types'
import { getCommitDiff, getIssue, getIssueCommits, getIssueDiff, getIssues, getLabels, getWorkflowTimeline, getWorkflowYaml, getWorktreeStatus, unarchiveIssue, getIssueWorkflowProfileYaml, updateIssueWorkflowProfileYaml, deleteIssueWorkflowProfileTemplate } from './client'

export function useIssues(params?: { stage?: string; label?: string; projectId?: string }) {
  return useQuery({
    queryKey: ['issues', params],
    queryFn: () => getIssues(params),
    enabled: !!params?.projectId,
  })
}

export function useIssue(number: number) {
  const { projectId } = useProject()
  return useQuery({
    queryKey: ['issues', number, projectId],
    queryFn: () => getIssue(number, projectId),
    enabled: number > 0 && !!projectId,
  })
}

export function useLabels() {
  return useQuery({
    queryKey: ['labels'],
    queryFn: () => getLabels(),
  })
}

export function useIssueDiff(number: number) {
  const { projectId } = useProject()
  return useQuery({
    queryKey: ['issues', number, projectId, 'diff'],
    queryFn: () => getIssueDiff(number, projectId),
    enabled: number > 0 && !!projectId,
  })
}

export function useIssueCommits(number: number) {
  const { projectId } = useProject()
  return useQuery({
    queryKey: ['issues', number, projectId, 'commits'],
    queryFn: () => getIssueCommits(number, projectId),
    enabled: number > 0 && !!projectId,
  })
}

export function useCommitDiff(number: number, hash: string, enabled: boolean = false) {
  const { projectId } = useProject()
  return useQuery({
    queryKey: ['issues', number, projectId, 'commits', hash, 'diff'],
    queryFn: () => getCommitDiff(number, hash, projectId),
    enabled: enabled && number > 0 && !!hash && !!projectId,
  })
}

export function useWorkflowTimeline(issueNumber: number, enabled: boolean = true) {
  const { projectId } = useProject()
  return useQuery({
    queryKey: ['issues', issueNumber, projectId, 'workflow-timeline'],
    queryFn: () => getWorkflowTimeline(issueNumber, projectId),
    enabled: enabled && issueNumber > 0 && !!projectId,
    refetchInterval: enabled ? 5000 : false,
  })
}

export function useWorkflowYaml(workflowRunId: string | null | undefined, enabled: boolean = true) {
  return useQuery({
    queryKey: ['workflow-runs', workflowRunId, 'yaml'],
    queryFn: () => getWorkflowYaml(workflowRunId!),
    enabled: enabled && !!workflowRunId,
  })
}

export function useWorktreeStatus(issueNumber: number, enabled: boolean) {
  const { projectId } = useProject()
  return useQuery({
    queryKey: ['issues', issueNumber, projectId, 'worktree-status'],
    queryFn: () => getWorktreeStatus(issueNumber, projectId),
    enabled: enabled && issueNumber > 0 && !!projectId,
    refetchInterval: 30_000,
  })
}

export function useArchivedIssues(params?: { projectId?: string }) {
  return useQuery({
    queryKey: ['archived-issues', params],
    queryFn: async () => {
      const issues = await getIssues({ ...params })
      return issues.filter(i => i.archivedAt != null)
    },
    enabled: !!params?.projectId,
  })
}

export function useUnarchiveIssue() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation({
    mutationFn: (number: number) => unarchiveIssue(number, projectId),
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

export function useIssueWorkflowProfileYaml(issueNumber: number, enabled: boolean = true) {
  const { projectId } = useProject()
  return useQuery({
    queryKey: ['issues', issueNumber, projectId, 'workflow-profile-yaml'],
    queryFn: () => getIssueWorkflowProfileYaml(issueNumber, projectId!),
    enabled: enabled && issueNumber > 0 && !!projectId,
  })
}

export function useUpdateIssueWorkflowProfileYaml() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation({
    mutationFn: ({ issueNumber, yaml }: { issueNumber: number; yaml: string }) =>
      updateIssueWorkflowProfileYaml(issueNumber, yaml, projectId!),
    onSuccess: (data) => {
      queryClient.invalidateQueries({ queryKey: ['issues', data.issueNumber, projectId, 'workflow-profile-yaml'] })
    },
  })
}

export function useDeleteIssueWorkflowProfileTemplate() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<IssueWorkflowProfileYamlResponse, Error, { issueNumber: number }>({
    mutationFn: ({ issueNumber }) => deleteIssueWorkflowProfileTemplate(issueNumber, projectId!),
    onSuccess: (data) => {
      queryClient.invalidateQueries({ queryKey: ['issues', data.issueNumber, projectId, 'workflow-profile-yaml'] })
    },
  })
}
