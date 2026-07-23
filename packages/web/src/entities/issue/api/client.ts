import { request, ApiError, projectApiPath } from '../../../shared/api/client'
import type { ApprovalFeedback, CommitDiffResponse, Comment, Issue, IssueCommitsResponse, IssueDiffResponse, IssueListItem, IssueParentCandidate, StoredCloudEventDto, TaskLogPage, WorkflowArtifact, WorkflowArtifactDirectory, WorkflowArtifactDirectoryEntry, WorkflowTimeline, IssueWorkflowProfileYamlResponse } from '../model/types'
import type { IssueListParams } from './query-keys'

export interface IssueWorkflowVariables {
  vars?: Record<string, unknown> | null
  stages?: Record<string, { vars?: Record<string, unknown> | null } | null> | null
}

export function getIssues(params?: IssueListParams, signal?: AbortSignal) {
  const search = new URLSearchParams()
  if (params?.stage) search.set('stage', params.stage)
  if (params?.label) search.set('label', params.label)
  if (params?.archived !== undefined) search.set('archived', String(params.archived))
  if (params?.all !== undefined) search.set('all', String(params.all))
  if (params?.repository) search.set('repository', params.repository)
  if (params?.parent !== undefined) search.set('parent', String(params.parent))
  const qs = search.toString()
  return request<IssueListItem[]>(projectApiPath(params?.projectId, `/issues${qs ? `?${qs}` : ''}`), { signal })
}

export function getIssue(number: number, projectId?: string | null, signal?: AbortSignal) {
  return request<Issue>(projectApiPath(projectId, `/issues/${number}`), { signal })
}

export function getParentIssueCandidates(projectId?: string | null, signal?: AbortSignal) {
  return request<IssueParentCandidate[]>(projectApiPath(projectId, '/issues/parent-candidates'), { signal })
}

export interface CreateIssueInput {
  title: string
  body?: string
  attachmentIds?: string[]
  labels?: Record<string, string>
  model?: string
  modelVariant?: string
  agentConfig?: Record<string, unknown>
  priority?: string
  risk?: string | null
  workflowProfileId?: string | null
  projectId?: string
  repositoryName?: string
  prerequisiteNumbers?: number[]
  parentIssueNumber?: number
}

export function createIssue(data: CreateIssueInput) {
  const { projectId, ...body } = data
  return request<Issue>(projectApiPath(projectId, '/issues'), {
    method: 'POST',
    body: JSON.stringify(body),
  })
}

export interface UpdateIssueOptions {
  title?: string
  body?: string
  attachmentIds?: string[]
  labels?: Record<string, string>
  model?: string | null
  modelVariant?: string | null
  agentConfig?: Record<string, unknown> | null
  stageModels?: Record<string, string> | null
  stageModelVariants?: Record<string, string> | null
  priority?: string | null
  isDraft?: boolean
  workflowProfileId?: string | null
}

export function updateIssue(number: number, data: UpdateIssueOptions, projectId?: string | null) {
  return request<Issue>(projectApiPath(projectId, `/issues/${number}`), {
    method: 'PATCH',
    body: JSON.stringify(data),
  })
}

export function startIssue(number: number, projectId?: string | null) {
  return request<{ issue: Issue; message: string }>(projectApiPath(projectId, `/issues/${number}/start`), { method: 'POST' })
}

export function closeIssue(number: number, projectId?: string | null) {
  return request<{ issue: Issue; message: string }>(projectApiPath(projectId, `/issues/${number}/close`), { method: 'POST' })
}

export function markIssueDone(number: number, projectId?: string | null) {
  return request<void>(projectApiPath(projectId, `/issues/${number}/done`), { method: 'POST' })
}

export function reopenIssue(number: number, projectId?: string | null) {
  return request<{ issue: Issue; message: string }>(projectApiPath(projectId, `/issues/${number}/reopen`), { method: 'POST' })
}

export function resumeIssue(number: number, projectId?: string | null) {
  return request<{ issue: Issue; message: string }>(projectApiPath(projectId, `/issues/${number}/resume`), { method: 'POST' })
}

export function retryIssue(number: number, projectId?: string | null) {
  return request<{ issue: Issue; message: string }>(projectApiPath(projectId, `/issues/${number}/retry`), { method: 'POST' })
}

export function approveIssue(number: number, projectId?: string | null) {
  return request<{ issue: Issue; context: string | null; message: string }>(projectApiPath(projectId, `/issues/${number}/approve`), { method: 'POST' })
}

export function rejectIssue(number: number, data: { message?: string }, projectId?: string | null) {
  return request<{ issue: Issue; message: string }>(projectApiPath(projectId, `/issues/${number}/reject`), {
    method: 'POST',
    body: JSON.stringify(data),
  })
}

export interface CreateFeedbackRequest {
  stage: string
  body: string
}

export function requestChangesIssue(number: number, data: CreateFeedbackRequest, projectId?: string | null) {
  return request<{ success?: boolean; data: ApprovalFeedback }>(projectApiPath(projectId, `/issues/${number}/feedback`), {
    method: 'POST',
    body: JSON.stringify(data),
  }).then((response) => response.data)
}

export function listIssueFeedback(number: number, params: { stage?: string } = {}, projectId?: string | null) {
  const search = new URLSearchParams()
  if (params.stage) search.set('stage', params.stage)
  const qs = search.toString()
  return request<ApprovalFeedback[]>(projectApiPath(projectId, `/issues/${number}/feedback${qs ? `?${qs}` : ''}`))
}

export function getIssueFeedback(number: number, feedbackId: string, projectId?: string | null) {
  return request<ApprovalFeedback>(projectApiPath(projectId, `/issues/${number}/feedback/${encodeURIComponent(feedbackId)}`))
}

export function getIssueDiff(number: number, projectId?: string | null, signal?: AbortSignal) {
  return request<IssueDiffResponse>(projectApiPath(projectId, `/issues/${number}/diff`), { signal })
}

export function getIssueEvents(number: number, projectId?: string | null, signal?: AbortSignal) {
  return request<StoredCloudEventDto[]>(projectApiPath(projectId, `/issues/${number}/events`), { signal })
}

export function getIssueCommits(number: number, projectId?: string | null, signal?: AbortSignal) {
  return request<IssueCommitsResponse>(projectApiPath(projectId, `/issues/${number}/commits`), { signal })
}

export function getCommitDiff(number: number, hash: string, projectId?: string | null, signal?: AbortSignal) {
  return request<CommitDiffResponse>(projectApiPath(projectId, `/issues/${number}/commits/${hash}/diff`), { signal })
}

export function getFileContent(number: number, filePath: string, projectId?: string | null) {
  return request<{ base: string; head: string }>(projectApiPath(projectId, `/issues/${number}/file-content?path=${encodeURIComponent(filePath)}`))
}

export function addComment(issueNumber: number, author: string, body: string, projectId?: string | null, attachmentIds?: string[]) {
  return request<Comment>(projectApiPath(projectId, `/issues/${issueNumber}/comments`), {
    method: 'POST',
    body: JSON.stringify({ author, body, ...(attachmentIds?.length ? { attachmentIds } : {}) }),
  })
}

export function issueAttachmentContentPath(issueNumber: number, attachmentId: string, projectId?: string | null) {
  return projectApiPath(projectId, `/issues/${issueNumber}/attachments/${encodeURIComponent(attachmentId)}/content`)
}

export function commentAttachmentContentPath(issueNumber: number, commentId: string, attachmentId: string, projectId?: string | null) {
  return projectApiPath(projectId, `/issues/${issueNumber}/comments/${encodeURIComponent(commentId)}/attachments/${encodeURIComponent(attachmentId)}/content`)
}

export function extractAttachmentIds(markdown: string) {
  return Array.from(new Set(Array.from(markdown.matchAll(/\batt:([A-Za-z0-9_-]+)/g), (match) => match[1])))
}

export function deleteComment(issueNumber: number, commentId: string, projectId?: string | null) {
  return request<{ message: string }>(projectApiPath(projectId, `/issues/${issueNumber}/comments/${commentId}`), {
    method: 'DELETE',
  })
}

export function getLabels(projectId?: string | null, signal?: AbortSignal) {
  return request<string[]>(projectApiPath(projectId, '/labels'), { signal })
}

export function getWorkflowYaml(workflowRunId: string, signal?: AbortSignal) {
  return request<{ workflowRunId: string; yaml: string }>(`/workflow-runs/${encodeURIComponent(workflowRunId)}/yaml`, { signal })
}

export function getIssueWorkflowProfileYaml(number: number, projectId: string, signal?: AbortSignal) {
  return request<IssueWorkflowProfileYamlResponse>(projectApiPath(projectId, `/issues/${number}/workflow-profile`), { signal })
}

export interface IssueWorkflowArtifactListParams {
  path?: string
  history?: boolean
  taskRunId?: string
}

export function getIssueWorkflowArtifacts(number: number, params: IssueWorkflowArtifactListParams = {}, projectId?: string | null, signal?: AbortSignal) {
  const search = new URLSearchParams()
  if (params.path) search.set('path', params.path)
  if (params.history) search.set('history', 'true')
  if (params.taskRunId) search.set('taskRunId', params.taskRunId)
  const qs = search.toString()
  return request<(WorkflowArtifact | WorkflowArtifactDirectory)[]>(projectApiPath(projectId, `/issues/${number}/workflow/artifacts${qs ? `?${qs}` : ''}`), { signal })
}

export function issueWorkflowArtifactContentPath(number: number, artifactId: string, projectId?: string | null) {
  return projectApiPath(projectId, `/issues/${number}/workflow/artifacts/${encodeURIComponent(artifactId)}/content`)
}

export type WorkflowArtifactContentResult =
  | { kind: 'text'; content: string; contentType: string | null }
  | { kind: 'directory'; entries: WorkflowArtifactDirectoryEntry[]; totalSize: number }

export interface IssueWorkflowArtifactContentOptions {
  file?: string
  artifactKind?: WorkflowArtifact['kind']
}

export async function getIssueWorkflowArtifactContent(
  number: number,
  artifactId: string,
  options: IssueWorkflowArtifactContentOptions = {},
  projectId?: string | null,
  signal?: AbortSignal,
): Promise<WorkflowArtifactContentResult> {
  const path = issueWorkflowArtifactContentPath(number, artifactId, projectId)
  const search = new URLSearchParams()
  if (options.file) search.set('file', options.file)
  const qs = search.toString()
  const res = await fetch(`/api${path}${qs ? `?${qs}` : ''}`, { signal })
  if (!res.ok) {
    const text = await res.text().catch(() => 'Unknown error')
    throw new ApiError(`Failed to fetch artifact content: ${text}`, res.status)
  }

  const contentType = res.headers.get('content-type')
  const content = await res.text()
  if (!options.file && options.artifactKind === 'directory') {
    const directory = JSON.parse(content) as WorkflowArtifactDirectory
    return { kind: 'directory', entries: directory.entries ?? [], totalSize: directory.totalSize ?? 0 }
  }

  return { kind: 'text', content, contentType }
}

export function updateIssueWorkflowProfileYaml(number: number, yaml: string, projectId: string) {
  return request<IssueWorkflowProfileYamlResponse>(projectApiPath(projectId, `/issues/${number}/workflow-profile/template`), {
    method: 'PUT',
    body: JSON.stringify({ yaml }),
  })
}

export function deleteIssueWorkflowProfileTemplate(number: number, projectId: string) {
  return request<IssueWorkflowProfileYamlResponse>(projectApiPath(projectId, `/issues/${number}/workflow-profile/template`), {
    method: 'DELETE',
  })
}

export function getWorkflowTimeline(number: number, projectId?: string | null, signal?: AbortSignal) {
  return request<{ workflow: WorkflowTimeline | null }>(projectApiPath(projectId, `/issues/${number}/workflow/status`), { signal })
    .then(response => response.workflow)
}

export function getIssueWorkflowVariables(number: number, projectId: string) {
  return request<IssueWorkflowVariables>(projectApiPath(projectId, `/issues/${number}/variables`))
}

export function getIssueWorkflowDefinitionVar(number: number, _name: string, projectId: string) {
  return getIssueWorkflowVariables(number, projectId)
}

export function patchIssueWorkflowDefinitionVar(number: number, name: string, value: unknown, projectId: string) {
  return request<IssueWorkflowVariables>(projectApiPath(projectId, `/issues/${number}/variables`), {
    method: 'PATCH',
    body: JSON.stringify({ vars: { [name]: value } }),
  })
}

export function patchIssueWorkflowStageDefinitionVar(number: number, stage: string, name: string, value: unknown, projectId: string) {
  return request<IssueWorkflowVariables>(projectApiPath(projectId, `/issues/${number}/variables`), {
    method: 'PATCH',
    body: JSON.stringify({ stages: { [stage]: { vars: { [name]: value } } } }),
  })
}

export async function rebaseIssue(number: number, projectId?: string | null) {
  try {
    return await request<{ rebased: boolean; rePlan?: boolean; conflicts?: string[]; buildPassed?: boolean; message: string; status?: 'queued'; workflowRunId?: string; taskId?: string; stage?: string; baseBranch?: string }>(projectApiPath(projectId, `/issues/${number}/rebase`), { method: 'POST' })
  } catch (err) {
    if (err instanceof ApiError && err.data && typeof err.data === 'object') {
      return err.data as { rebased: boolean; rePlan?: boolean; conflicts?: string[]; buildPassed?: boolean; message: string; status?: 'queued'; workflowRunId?: string; taskId?: string; stage?: string; baseBranch?: string }
    }
    throw err
  }
}

export function rerunIssue(number: number, projectId?: string | null) {
  return request<{ issue: Issue; message: string }>(projectApiPath(projectId, `/issues/${number}/rerun`), { method: 'POST' })
}

export function forceStopIssue(number: number, projectId?: string | null) {
  return request<{ ok: boolean; issueNumber: number }>(projectApiPath(projectId, `/issues/${number}/force-stop`), { method: 'POST' })
}

export function stopIssue(number: number, projectId?: string | null) {
  return request<{ ok: boolean; issueNumber: number }>(projectApiPath(projectId, `/issues/${number}/stop`), { method: 'POST' })
}

export function getWorkspaceStatus(number: number, projectId?: string | null, signal?: AbortSignal) {
  return request<{
    exists: boolean
    reason?: string
    branch?: string
    baseBranch?: string
    ahead?: number
    behind?: number
    rebaseInProgress?: boolean
    conflictingFiles?: string[]
  }>(projectApiPath(projectId, `/issues/${number}/workspace-status`), { signal })
}

export function cleanupIssueWorkspace(number: number, projectId?: string | null) {
  return request<{
    removed: boolean
    message: string
    resources: Array<{ type: string; status: string; path?: string | null; reason?: string | null }>
  }>(projectApiPath(projectId, `/issues/${number}/cleanup`), { method: 'POST' })
}

export function archiveIssue(number: number, projectId?: string | null) {
  return request<{ issue: Issue; message: string; warning?: string }>(projectApiPath(projectId, `/issues/${number}/archive`), { method: 'POST' })
}

export function unarchiveIssue(number: number, projectId?: string | null) {
  return request<{ issue: Issue; message: string }>(projectApiPath(projectId, `/issues/${number}/unarchive`), { method: 'POST' })
}

export function archiveAllCompleted(projectId?: string | null) {
  return request<{ archived: number; skipped: number; skippedNumbers: number[]; message: string }>(projectApiPath(projectId, '/issues/archive-completed'), { method: 'POST' })
}

export function addPrerequisite(number: number, prerequisiteNumber: number, projectId?: string | null) {
  return request<{ issue: Issue; message: string }>(projectApiPath(projectId, `/issues/${number}/prerequisites`), {
    method: 'POST',
    body: JSON.stringify({ prerequisiteNumber }),
  })
}

export function removePrerequisite(number: number, prerequisiteNumber: number, projectId?: string | null) {
  return request<{ issue: Issue; message: string }>(projectApiPath(projectId, `/issues/${number}/prerequisites/${prerequisiteNumber}`), {
    method: 'DELETE',
  })
}

export interface IssueWorkflowTaskLogParams {
  cursor?: number | null
  limit?: number | null
}

export function getIssueWorkflowTaskLog(number: number, taskId: string, params: IssueWorkflowTaskLogParams = {}, projectId?: string | null, signal?: AbortSignal) {
  const search = new URLSearchParams()
  if (params.cursor != null) search.set('cursor', String(params.cursor))
  if (params.limit != null) search.set('limit', String(params.limit))
  const qs = search.toString()
  return request<TaskLogPage>(projectApiPath(projectId, `/issues/${number}/workflow/tasks/${encodeURIComponent(taskId)}/logs${qs ? `?${qs}` : ''}`), { signal })
}
