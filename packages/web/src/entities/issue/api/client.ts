import { request, ApiError, projectApiPath } from '../../../shared/api/client'
import type { CommitDiffResponse, Comment, Issue, IssueCommitsResponse, IssueDiffResponse, WorkflowArtifact, WorkflowArtifactDirectory, WorkflowArtifactDirectoryEntry, WorkflowTimeline, IssueWorkflowProfileYamlResponse } from '../model/types'

export interface IssueWorkflowVariables {
  vars?: Record<string, unknown> | null
  stages?: Record<string, { vars?: Record<string, unknown> | null } | null> | null
}

export function getIssues(params?: { stage?: string; label?: string; projectId?: string }) {
  const search = new URLSearchParams()
  if (params?.stage) search.set('stage', params.stage)
  if (params?.label) search.set('label', params.label)
  const qs = search.toString()
  return request<Issue[]>(projectApiPath(params?.projectId, `/issues${qs ? `?${qs}` : ''}`))
}

export function getIssue(number: number, projectId?: string | null) {
  return request<Issue>(projectApiPath(projectId, `/issues/${number}`))
}

export function createIssue(data: { title: string; body?: string; labels?: string[]; model?: string; agentConfig?: Record<string, unknown>; priority?: string; projectId?: string; repositoryName?: string }) {
  const { projectId, ...body } = data
  return request<Issue>(projectApiPath(projectId, '/issues'), {
    method: 'POST',
    body: JSON.stringify(body),
  })
}

export function updateIssue(number: number, data: { title?: string; body?: string; addLabels?: string[]; removeLabels?: string[]; model?: string | null; agentConfig?: Record<string, unknown> | null; stageModels?: Record<string, string> | null; priority?: string | null }, projectId?: string | null) {
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

export function getIssueDiff(number: number, projectId?: string | null) {
  return request<IssueDiffResponse>(projectApiPath(projectId, `/issues/${number}/diff`))
}

export function getIssueCommits(number: number, projectId?: string | null) {
  return request<IssueCommitsResponse>(projectApiPath(projectId, `/issues/${number}/commits`))
}

export function getCommitDiff(number: number, hash: string, projectId?: string | null) {
  return request<CommitDiffResponse>(projectApiPath(projectId, `/issues/${number}/commits/${hash}/diff`))
}

export function getFileContent(number: number, filePath: string, projectId?: string | null) {
  return request<{ base: string; head: string }>(projectApiPath(projectId, `/issues/${number}/file-content?path=${encodeURIComponent(filePath)}`))
}

export function addComment(issueNumber: number, body: string, projectId?: string | null) {
  return request<Comment>(projectApiPath(projectId, `/issues/${issueNumber}/comments`), {
    method: 'POST',
    body: JSON.stringify({ body }),
  })
}

export function deleteComment(issueNumber: number, commentId: string, projectId?: string | null) {
  return request<{ message: string }>(projectApiPath(projectId, `/issues/${issueNumber}/comments/${commentId}`), {
    method: 'DELETE',
  })
}

export function getLabels() {
  return request<string[]>('/labels')
}

export function getWorkflowYaml(workflowRunId: string) {
  return request<{ workflowRunId: string; yaml: string }>(`/workflow-runs/${encodeURIComponent(workflowRunId)}/yaml`)
}

export function getIssueWorkflowProfileYaml(number: number, projectId: string) {
  return request<IssueWorkflowProfileYamlResponse>(projectApiPath(projectId, `/issues/${number}/workflow-profile`))
}

export interface IssueWorkflowArtifactListParams {
  path?: string
  history?: boolean
  taskRunId?: string
}

export function getIssueWorkflowArtifacts(number: number, params: IssueWorkflowArtifactListParams = {}, projectId?: string | null) {
  const search = new URLSearchParams()
  if (params.path) search.set('path', params.path)
  if (params.history) search.set('history', 'true')
  if (params.taskRunId) search.set('taskRunId', params.taskRunId)
  const qs = search.toString()
  return request<(WorkflowArtifact | WorkflowArtifactDirectory)[]>(projectApiPath(projectId, `/issues/${number}/workflow/artifacts${qs ? `?${qs}` : ''}`))
}

export function issueWorkflowArtifactContentPath(number: number, artifactId: string, projectId?: string | null) {
  return projectApiPath(projectId, `/issues/${number}/workflow/artifacts/${encodeURIComponent(artifactId)}/content`)
}

export type WorkflowArtifactContentResult =
  | { kind: 'text'; content: string; contentType: string | null }
  | { kind: 'directory'; entries: WorkflowArtifactDirectoryEntry[]; totalSize: number }

export async function getIssueWorkflowArtifactContent(
  number: number,
  artifactId: string,
  options: { file?: string } = {},
  projectId?: string | null,
): Promise<WorkflowArtifactContentResult> {
  const path = issueWorkflowArtifactContentPath(number, artifactId, projectId)
  const search = new URLSearchParams()
  if (options.file) search.set('file', options.file)
  const qs = search.toString()
  const res = await fetch(`/api${path}${qs ? `?${qs}` : ''}`)
  if (!res.ok) {
    const text = await res.text().catch(() => 'Unknown error')
    throw new ApiError(`Failed to fetch artifact content: ${text}`, res.status)
  }

  const contentType = res.headers.get('content-type')
  if (!options.file && contentType?.includes('application/json')) {
    const directory = await res.json() as WorkflowArtifactDirectory
    return { kind: 'directory', entries: directory.entries ?? [], totalSize: directory.totalSize ?? 0 }
  }

  const content = await res.text()
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

export function getWorkflowTimeline(number: number, projectId?: string | null) {
  return request<{ workflow: WorkflowTimeline | null }>(projectApiPath(projectId, `/issues/${number}/workflow/status`))
    .then(response => response.workflow)
}

export function getIssueWorkflowVariables(number: number, projectId: string) {
  return request<IssueWorkflowVariables>(projectApiPath(projectId, `/issues/${number}/workflow-profile/variables`))
}

export function getIssueWorkflowDefinitionVar(number: number, _name: string, projectId: string) {
  return getIssueWorkflowVariables(number, projectId)
}

export function patchIssueWorkflowDefinitionVar(number: number, name: string, value: unknown, projectId: string) {
  return request<IssueWorkflowVariables>(projectApiPath(projectId, `/issues/${number}/workflow-profile/variables`), {
    method: 'PATCH',
    body: JSON.stringify({ vars: { [name]: value } }),
  })
}

export function patchIssueWorkflowStageDefinitionVar(number: number, stage: string, name: string, value: unknown, projectId: string) {
  return request<IssueWorkflowVariables>(projectApiPath(projectId, `/issues/${number}/workflow-profile/variables`), {
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

export function getWorkspaceStatus(number: number, projectId?: string | null) {
  return request<{
    exists: boolean
    branch?: string
    baseBranch?: string
    ahead?: number
    behind?: number
    rebaseInProgress?: boolean
    conflictingFiles?: string[]
  }>(projectApiPath(projectId, `/issues/${number}/workspace-status`))
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
