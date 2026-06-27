import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { useProject } from '../../project/@x/project-context'
import type { ApprovalFeedback, Issue, IssueWorkflowProfileYamlResponse } from '../model/types'
import type { CreateFeedbackRequest, IssueWorkflowArtifactListParams } from './client'
import { deleteIssueWorkflowProfileTemplate, getCommitDiff, getIssue, getIssueCommits, getIssueDiff, getIssueEvents, getIssues, getIssueWorkflowArtifactContent, getIssueWorkflowArtifacts, getIssueWorkflowProfileYaml, getLabels, getWorkflowTimeline, getWorkflowYaml, getWorkspaceStatus, requestChangesIssue, unarchiveIssue, updateIssue, updateIssueWorkflowProfileYaml } from './client'
import { invalidateApprovalWait } from './approval-wait'

export function useIssueWorkflowArtifacts(issueNumber: number, params: IssueWorkflowArtifactListParams = {}, enabled: boolean = true) {
  const { projectId } = useProject()
  return useQuery({
    queryKey: ['issues', issueNumber, projectId, 'workflow-artifacts', params],
    queryFn: () => getIssueWorkflowArtifacts(issueNumber, params, projectId),
    enabled: enabled && issueNumber > 0 && !!projectId,
  })
}

export function useIssueWorkflowArtifactContent(issueNumber: number, artifactId: string | null, options: { file?: string } = {}, enabled: boolean = true) {
  const { projectId } = useProject()
  return useQuery({
    queryKey: ['issues', issueNumber, projectId, 'workflow-artifacts', artifactId, 'content', options],
    queryFn: () => getIssueWorkflowArtifactContent(issueNumber, artifactId!, options, projectId),
    enabled: enabled && issueNumber > 0 && !!artifactId && !!projectId,
  })
}

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
  const { projectId } = useProject()
  return useQuery({
    queryKey: ['labels', projectId],
    queryFn: () => getLabels(projectId),
    enabled: !!projectId,
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

export function useIssueEvents(number: number, enabled: boolean = true) {
  const { projectId } = useProject()
  return useQuery({
    queryKey: ['issue-events', number, projectId],
    queryFn: () => getIssueEvents(number, projectId),
    enabled: enabled && number > 0 && !!projectId,
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

export function useWorkspaceStatus(issueNumber: number, enabled: boolean = true) {
  const { projectId } = useProject()
  return useQuery({
    queryKey: ['issues', issueNumber, projectId, 'workspace-status'],
    queryFn: () => getWorkspaceStatus(issueNumber, projectId),
    enabled: enabled && issueNumber > 0 && !!projectId,
    refetchInterval: (query) => {
      const data = query.state.data as { exists?: boolean; reason?: string; ahead?: number; behind?: number } | undefined
      const missingAheadBehind = data?.exists === true && (typeof data.ahead !== 'number' || typeof data.behind !== 'number')
      return !!data?.reason || missingAheadBehind ? 5_000 : 30_000
    },
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

export function useUpdateIssueWorkflowProfile() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<Issue, Error, { issueNumber: number; workflowProfileId: string | null }>({
    mutationFn: ({ issueNumber, workflowProfileId }) =>
      updateIssue(issueNumber, { workflowProfileId }, projectId),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['issues', variables.issueNumber] })
      queryClient.invalidateQueries({ queryKey: ['issues', variables.issueNumber, projectId] })
      queryClient.invalidateQueries({ queryKey: ['issues', variables.issueNumber, projectId, 'workflow-profile-yaml'] })
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

export function useRequestChangesIssue() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<ApprovalFeedback, Error, { issueNumber: number; data: CreateFeedbackRequest }>({
    mutationFn: ({ issueNumber, data }) => requestChangesIssue(issueNumber, data, projectId),
    onSuccess: (_feedback, variables) => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['issues', variables.issueNumber, projectId] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      queryClient.invalidateQueries({ queryKey: ['agent-activity'] })
      queryClient.invalidateQueries({ queryKey: ['issues', variables.issueNumber, projectId, 'workflow-timeline'] })
      invalidateApprovalWait(queryClient)
    },
  })
}
