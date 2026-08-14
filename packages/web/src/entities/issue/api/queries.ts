import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { QueryFunctionContext } from '@tanstack/react-query'
import { ApiError } from '../../../shared/api/client'
import { toast } from 'sonner'
import { useProject } from '../../project/@x/project-context'
import type {
  ApprovalFeedback,
  Issue,
  IssueListItem,
  IssueWorkflowProfileYamlResponse,
  TaskLogPage,
} from '../model/types'
import {
  issueArtifactKeys,
  issueCandidateKeys,
  issueDetailKeys,
  issueListKeys,
  issueWorkflowKeys,
  workflowRunKeys,
  type IssueListParams,
} from './query-keys'
import type {
  CreateFeedbackRequest,
  IssueWorkflowArtifactContentOptions,
  IssueWorkflowArtifactListParams,
  IssueWorkflowTaskLogParams,
} from './client'
import {
  deleteIssueWorkflowProfileTemplate,
  getCommitDiff,
  getIssue,
  getIssueCommits,
  getIssueDiff,
  getIssueEvents,
  getIssues,
  getIssueWorkflowArtifactContent,
  getIssueWorkflowArtifacts,
  getIssueWorkflowProfileYaml,
  getIssueWorkflowTaskLog,
  getLabels,
  getParentIssueCandidates,
  getWorkflowRunDetail,
  getWorkflowTimeline,
  getWorkflowYaml,
  getWorkspaceStatus,
  requestChangesIssue,
  unarchiveIssue,
  updateIssue,
  updateIssueWorkflowProfileYaml,
} from './client'
import { invalidateApprovalWait } from './approval-wait'

const EMPTY_TASK_LOG_PAGE: TaskLogPage = { lines: [], nextCursor: null, truncated: false }

async function fetchIssueWorkflowTaskLogOrEmpty(
  issueNumber: number,
  taskId: string,
  params: IssueWorkflowTaskLogParams,
  projectId: string,
  signal?: AbortSignal,
): Promise<TaskLogPage> {
  try {
    return await getIssueWorkflowTaskLog(issueNumber, taskId, params, projectId, signal)
  } catch (err) {
    if (err instanceof ApiError && err.status === 404 && isMissingTaskLogEndpoint(err)) {
      return EMPTY_TASK_LOG_PAGE
    }
    throw err
  }
}

function isMissingTaskLogEndpoint(err: ApiError): boolean {
  return err.message.startsWith('Empty response from ') || err.message.startsWith('Invalid JSON response from ')
}

export function issueWorkflowTaskLogQueryOptions(
  projectId: string | null | undefined,
  issueNumber: number,
  taskId: string | null | undefined,
  params: IssueWorkflowTaskLogParams = {},
  enabled: boolean = true,
  workflowRunId?: string | null,
) {
  const safeTaskId = typeof taskId === 'string' && taskId.length > 0 ? taskId : null
  return {
    queryKey: issueWorkflowKeys.taskLog(projectId, issueNumber, safeTaskId, workflowRunId, params),
    queryFn: (context?: QueryFunctionContext) =>
      fetchIssueWorkflowTaskLogOrEmpty(issueNumber, safeTaskId!, params, projectId!, context?.signal),
    enabled: enabled && issueNumber > 0 && !!safeTaskId && !!projectId,
  } as const
}

export function useIssueWorkflowTaskLog(
  issueNumber: number,
  taskId: string | null | undefined,
  params: IssueWorkflowTaskLogParams = {},
  enabled: boolean = true,
  workflowRunId?: string | null,
) {
  const { projectId } = useProject()
  return useQuery(issueWorkflowTaskLogQueryOptions(projectId, issueNumber, taskId, params, enabled, workflowRunId))
}

export function issueWorkflowArtifactsQueryOptions(
  projectId: string | null | undefined,
  issueNumber: number,
  params: IssueWorkflowArtifactListParams = {},
  enabled: boolean = true,
  workflowRunId?: string | null,
) {
  return {
    queryKey: issueArtifactKeys.list(projectId, issueNumber, workflowRunId, params),
    queryFn: ({ signal }: QueryFunctionContext) => getIssueWorkflowArtifacts(issueNumber, params, projectId, signal),
    enabled: enabled && issueNumber > 0 && !!projectId,
  } as const
}

export function useIssueWorkflowArtifacts(
  issueNumber: number,
  params: IssueWorkflowArtifactListParams = {},
  enabled: boolean = true,
  workflowRunId?: string | null,
) {
  const { projectId } = useProject()
  return useQuery(issueWorkflowArtifactsQueryOptions(projectId, issueNumber, params, enabled, workflowRunId))
}

export function useIssueWorkflowArtifactContent(
  issueNumber: number,
  artifactId: string | null,
  options: IssueWorkflowArtifactContentOptions = {},
  enabled: boolean = true,
) {
  const { projectId } = useProject()
  return useQuery({
    queryKey: issueArtifactKeys.content(projectId, issueNumber, artifactId!, options),
    queryFn: ({ signal }: QueryFunctionContext) =>
      getIssueWorkflowArtifactContent(issueNumber, artifactId!, options, projectId, signal),
    enabled: enabled && issueNumber > 0 && !!artifactId && !!projectId,
  })
}

export function useIssues(params?: IssueListParams) {
  return useQuery<IssueListItem[]>({
    queryKey: issueListKeys.list(params),
    queryFn: ({ signal }: QueryFunctionContext) => getIssues(params, signal),
    enabled: !!params?.projectId,
  })
}

export function useIssue(number: number) {
  const { projectId } = useProject()
  return useQuery({
    queryKey: issueDetailKeys.detail(projectId, number),
    queryFn: ({ signal }: QueryFunctionContext) => getIssue(number, projectId, signal),
    enabled: number > 0 && !!projectId,
  })
}

export function useWorkflowRunDetail(workflowRunId: string | null | undefined) {
  return useQuery({
    queryKey: workflowRunKeys.detail(workflowRunId),
    queryFn: ({ signal }: QueryFunctionContext) => getWorkflowRunDetail(workflowRunId!, signal),
    enabled: !!workflowRunId,
  })
}

export function useParentIssueCandidates(enabled: boolean = true) {
  const { projectId } = useProject()
  return useQuery({
    queryKey: issueCandidateKeys.project(projectId),
    queryFn: ({ signal }: QueryFunctionContext) => getParentIssueCandidates(projectId, signal),
    enabled: enabled && !!projectId,
  })
}

export function useLabels() {
  const { projectId } = useProject()
  return useQuery({
    queryKey: ['labels', projectId],
    queryFn: ({ signal }: QueryFunctionContext) => getLabels(projectId, signal),
    enabled: !!projectId,
  })
}

export function useIssueDiff(number: number, enabled: boolean = true) {
  const { projectId } = useProject()
  return useQuery({
    queryKey: issueWorkflowKeys.diff(projectId, number),
    queryFn: ({ signal }: QueryFunctionContext) => getIssueDiff(number, projectId, signal),
    enabled: enabled && number > 0 && !!projectId,
  })
}

export function issueEventsQueryOptions(projectId: string | null | undefined, number: number, enabled: boolean = true) {
  return {
    queryKey: issueWorkflowKeys.events(projectId, number),
    queryFn: (context?: QueryFunctionContext) => getIssueEvents(number, projectId, context?.signal),
    enabled: enabled && number > 0 && !!projectId,
  } as const
}

export function useIssueEvents(number: number, enabled: boolean = true) {
  const { projectId } = useProject()
  return useQuery(issueEventsQueryOptions(projectId, number, enabled))
}

export function useIssueCommits(number: number, enabled: boolean = true) {
  const { projectId } = useProject()
  return useQuery({
    queryKey: issueWorkflowKeys.commits(projectId, number),
    queryFn: ({ signal }: QueryFunctionContext) => getIssueCommits(number, projectId, signal),
    enabled: enabled && number > 0 && !!projectId,
  })
}

export function useCommitDiff(number: number, hash: string, enabled: boolean = false) {
  const { projectId } = useProject()
  return useQuery({
    queryKey: issueWorkflowKeys.commits(projectId, number, hash),
    queryFn: ({ signal }: QueryFunctionContext) => getCommitDiff(number, hash, projectId, signal),
    enabled: enabled && number > 0 && !!hash && !!projectId,
  })
}

export function useWorkflowTimeline(issueNumber: number, enabled: boolean = true) {
  const { projectId } = useProject()
  return useQuery({
    queryKey: issueWorkflowKeys.timeline(projectId, issueNumber),
    queryFn: ({ signal }: QueryFunctionContext) => getWorkflowTimeline(issueNumber, projectId, signal),
    enabled: enabled && issueNumber > 0 && !!projectId,
    refetchInterval: false,
  })
}

export function useWorkflowYaml(workflowRunId: string | null | undefined, enabled: boolean = true) {
  return useQuery({
    queryKey: ['workflow-runs', workflowRunId, 'yaml'],
    queryFn: ({ signal }: QueryFunctionContext) => getWorkflowYaml(workflowRunId!, signal),
    enabled: enabled && !!workflowRunId,
  })
}

export function workspaceStatusQueryOptions(
  projectId: string | null | undefined,
  issueNumber: number,
  enabled: boolean = true,
) {
  return {
    queryKey: issueWorkflowKeys.workspace(projectId, issueNumber),
    queryFn: ({ signal }: QueryFunctionContext) => getWorkspaceStatus(issueNumber, projectId, signal),
    enabled: enabled && issueNumber > 0 && !!projectId,
    refetchInterval: (query: { state: { data?: unknown } }) => {
      const data = query.state.data as
        | { exists?: boolean; reason?: string; ahead?: number; behind?: number }
        | undefined
      const missingAheadBehind =
        data?.exists === true && (typeof data.ahead !== 'number' || typeof data.behind !== 'number')
      return !!data?.reason || missingAheadBehind ? 5_000 : 30_000
    },
  } as const
}

export function useWorkspaceStatus(issueNumber: number, enabled: boolean = true) {
  const { projectId } = useProject()
  return useQuery(workspaceStatusQueryOptions(projectId, issueNumber, enabled))
}

export function useArchivedIssues(params?: { projectId?: string }) {
  return useQuery({
    queryKey: issueListKeys.archived(params?.projectId),
    queryFn: async ({ signal }: QueryFunctionContext) => {
      const issues = await getIssues({ ...params, archived: true }, signal)
      return issues.filter((i) => i.archivedAt != null)
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
      queryClient.invalidateQueries({ queryKey: issueListKeys.project(projectId) })
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
    queryKey: issueWorkflowKeys.profileYaml(projectId, issueNumber),
    queryFn: ({ signal }: QueryFunctionContext) => getIssueWorkflowProfileYaml(issueNumber, projectId!, signal),
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
      queryClient.invalidateQueries({
        queryKey: issueWorkflowKeys.profileYaml(projectId, data.issueNumber),
        exact: true,
      })
    },
  })
}

export function useUpdateIssueWorkflowProfile() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<Issue, Error, { issueNumber: number; workflowProfileId: string | null }>({
    mutationFn: ({ issueNumber, workflowProfileId }) => updateIssue(issueNumber, { workflowProfileId }, projectId),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({ queryKey: issueListKeys.project(projectId) })
      queryClient.invalidateQueries({ queryKey: issueDetailKeys.detail(projectId, variables.issueNumber), exact: true })
      queryClient.invalidateQueries({
        queryKey: issueWorkflowKeys.profileYaml(projectId, variables.issueNumber),
        exact: true,
      })
    },
  })
}

export function useDeleteIssueWorkflowProfileTemplate() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<IssueWorkflowProfileYamlResponse, Error, { issueNumber: number }>({
    mutationFn: ({ issueNumber }) => deleteIssueWorkflowProfileTemplate(issueNumber, projectId!),
    onSuccess: (data) => {
      queryClient.invalidateQueries({
        queryKey: issueWorkflowKeys.profileYaml(projectId, data.issueNumber),
        exact: true,
      })
    },
  })
}

export function useRequestChangesIssue() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<ApprovalFeedback, Error, { issueNumber: number; data: CreateFeedbackRequest }>({
    mutationFn: ({ issueNumber, data }) => requestChangesIssue(issueNumber, data, projectId),
    onSuccess: (_feedback, variables) => {
      queryClient.invalidateQueries({ queryKey: issueListKeys.project(projectId) })
      queryClient.invalidateQueries({ queryKey: issueDetailKeys.detail(projectId, variables.issueNumber), exact: true })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      queryClient.invalidateQueries({ queryKey: ['agent-activity'] })
      queryClient.invalidateQueries({
        queryKey: issueWorkflowKeys.timeline(projectId, variables.issueNumber),
        exact: true,
      })
      invalidateApprovalWait(queryClient)
    },
  })
}
