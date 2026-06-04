import { request, withProject, ApiError } from '../../../shared/api/client'
import type { CommitDiffResponse, Comment, Issue, IssueCommitsResponse, IssueDiffResponse, WorkflowTimeline, IssueWorkflowProfileYamlResponse } from '../model/types'

export function getIssues(params?: { stage?: string; label?: string; projectId?: string }) {
  const search = new URLSearchParams()
  if (params?.stage) search.set('stage', params.stage)
  if (params?.label) search.set('label', params.label)
  const qs = search.toString()
  return request<Issue[]>(`/issues${qs ? `?${qs}` : ''}`, withProject(undefined, params?.projectId))
}

export function getIssue(number: number, projectId?: string | null) {
  return request<Issue>(`/issues/${number}`, withProject(undefined, projectId))
}

export function createIssue(data: { title: string; body?: string; labels?: string[]; model?: string; agentConfig?: Record<string, unknown>; priority?: string; projectId?: string; repositoryName?: string }) {
  const { projectId, ...body } = data
  return request<Issue>('/issues', withProject({
    method: 'POST',
    body: JSON.stringify(body),
  }, projectId))
}

export function updateIssue(number: number, data: { title?: string; body?: string; addLabels?: string[]; removeLabels?: string[]; model?: string | null; agentConfig?: Record<string, unknown> | null; stageModels?: Record<string, string> | null; priority?: string | null }, projectId?: string | null) {
  return request<Issue>(`/issues/${number}`, withProject({
    method: 'PATCH',
    body: JSON.stringify(data),
  }, projectId))
}

export function startIssue(number: number, projectId?: string | null) {
  return request<{ issue: Issue; message: string }>(`/issues/${number}/start`, withProject({ method: 'POST' }, projectId))
}

export function closeIssue(number: number, projectId?: string | null) {
  return request<{ issue: Issue; message: string }>(`/issues/${number}/close`, withProject({ method: 'POST' }, projectId))
}

export function reopenIssue(number: number, projectId?: string | null) {
  return request<{ issue: Issue; message: string }>(`/issues/${number}/reopen`, withProject({ method: 'POST' }, projectId))
}

export function resumeIssue(number: number, projectId?: string | null) {
  return request<{ issue: Issue; message: string }>(`/issues/${number}/resume`, withProject({ method: 'POST' }, projectId))
}

export function retryIssue(number: number, projectId?: string | null) {
  return request<{ issue: Issue; message: string }>(`/issues/${number}/retry`, withProject({ method: 'POST' }, projectId))
}

export function approveIssue(number: number, projectId?: string | null) {
  return request<{ issue: Issue; context: string | null; message: string }>(`/issues/${number}/approve`, withProject({ method: 'POST' }, projectId))
}

export function rejectIssue(number: number, data: { message?: string }, projectId?: string | null) {
  return request<{ issue: Issue; message: string }>(`/issues/${number}/reject`, withProject({
    method: 'POST',
    body: JSON.stringify(data),
  }, projectId))
}

export function getIssueDiff(number: number, projectId?: string | null) {
  return request<IssueDiffResponse>(`/issues/${number}/diff`, withProject(undefined, projectId))
}

export function getIssueCommits(number: number, projectId?: string | null) {
  return request<IssueCommitsResponse>(`/issues/${number}/commits`, withProject(undefined, projectId))
}

export function getCommitDiff(number: number, hash: string, projectId?: string | null) {
  return request<CommitDiffResponse>(`/issues/${number}/commits/${hash}/diff`, withProject(undefined, projectId))
}

export function getFileContent(number: number, filePath: string, projectId?: string | null) {
  return request<{ base: string; head: string }>(`/issues/${number}/workflow/file-content?path=${encodeURIComponent(filePath)}`, withProject(undefined, projectId))
}

export function addComment(issueNumber: number, body: string, projectId?: string | null) {
  return request<Comment>(`/issues/${issueNumber}/comments`, withProject({
    method: 'POST',
    body: JSON.stringify({ body }),
  }, projectId))
}

export function deleteComment(issueNumber: number, commentId: string, projectId?: string | null) {
  return request<{ message: string }>(`/issues/${issueNumber}/comments/${commentId}`, withProject({
    method: 'DELETE',
  }, projectId))
}

export function getLabels() {
  return request<string[]>('/labels')
}

export function getWorkflowYaml(workflowRunId: string) {
  return request<{ workflowRunId: string; yaml: string }>(`/workflow-runs/${encodeURIComponent(workflowRunId)}/yaml`)
}

export function getIssueWorkflowProfileYaml(number: number, projectId: string) {
  return request<IssueWorkflowProfileYamlResponse>(`/projects/${encodeURIComponent(projectId)}/issues/${number}/workflow-profile`)
}

export function updateIssueWorkflowProfileYaml(number: number, yaml: string, projectId: string) {
  return request<IssueWorkflowProfileYamlResponse>(`/projects/${encodeURIComponent(projectId)}/issues/${number}/workflow-profile/template`, {
    method: 'PUT',
    body: JSON.stringify({ yaml }),
  })
}

export function deleteIssueWorkflowProfileTemplate(number: number, projectId: string) {
  return request<IssueWorkflowProfileYamlResponse>(`/projects/${encodeURIComponent(projectId)}/issues/${number}/workflow-profile/template`, {
    method: 'DELETE',
  })
}

export function getWorkflowTimeline(number: number, projectId?: string | null) {
  return request<{ workflow: WorkflowTimeline | null }>(`/issues/${number}/workflow/status`, withProject(undefined, projectId))
    .then(response => response.workflow)
}

export function getIssueWorkflowDefinitionVar(number: number, _name: string, projectId: string) {
  return request<unknown>(`/projects/${encodeURIComponent(projectId)}/issues/${number}/workflow-profile/variables`)
}

export function patchIssueWorkflowDefinitionVar(number: number, name: string, value: unknown, projectId: string) {
  return request<unknown>(`/projects/${encodeURIComponent(projectId)}/issues/${number}/workflow-profile/variables`, {
    method: 'PATCH',
    body: JSON.stringify({ vars: { [name]: value } }),
  })
}

export function patchIssueWorkflowStageDefinitionVar(number: number, stage: string, name: string, value: unknown, projectId: string) {
  return request<unknown>(`/projects/${encodeURIComponent(projectId)}/issues/${number}/workflow-profile/variables`, {
    method: 'PATCH',
    body: JSON.stringify({ stages: { [stage]: { vars: { [name]: value } } } }),
  })
}

export async function rebaseIssue(number: number, projectId?: string | null) {
  try {
    return await request<{ rebased: boolean; rePlan?: boolean; conflicts?: string[]; buildPassed?: boolean; message: string; status?: 'queued'; workflowRunId?: string; taskId?: string; stage?: string; baseBranch?: string }>(`/issues/${number}/rebase`, withProject({ method: 'POST' }, projectId))
  } catch (err) {
    if (err instanceof ApiError && err.data && typeof err.data === 'object') {
      return err.data as { rebased: boolean; rePlan?: boolean; conflicts?: string[]; buildPassed?: boolean; message: string; status?: 'queued'; workflowRunId?: string; taskId?: string; stage?: string; baseBranch?: string }
    }
    throw err
  }
}

export function rerunIssue(number: number, projectId?: string | null) {
  return request<{ issue: Issue; message: string }>(`/issues/${number}/rerun`, withProject({ method: 'POST' }, projectId))
}

export function forceStopIssue(number: number, projectId?: string | null) {
  return request<{ ok: boolean; issueNumber: number }>(`/issues/${number}/force-stop`, withProject({ method: 'POST' }, projectId))
}

export function stopIssue(number: number, projectId?: string | null) {
  return request<{ ok: boolean; issueNumber: number }>(`/issues/${number}/stop`, withProject({ method: 'POST' }, projectId))
}

export function getWorktreeStatus(number: number, projectId?: string | null) {
  return request<{
    exists: boolean
    branch?: string
    baseBranch?: string
    ahead?: number
    behind?: number
    rebaseInProgress?: boolean
    conflictingFiles?: string[]
  }>(`/issues/${number}/worktree-status`, withProject(undefined, projectId))
}

export function cleanupIssueWorktree(number: number, projectId?: string | null) {
  return request<{
    removed: boolean
    message: string
    resources: Array<{ type: string; status: string; path?: string | null; reason?: string | null }>
  }>(`/issues/${number}/cleanup`, withProject({ method: 'POST' }, projectId))
}

export function archiveIssue(number: number, projectId?: string | null) {
  return request<{ issue: Issue; message: string; warning?: string }>(`/issues/${number}/archive`, withProject({ method: 'POST' }, projectId))
}

export function unarchiveIssue(number: number, projectId?: string | null) {
  return request<{ issue: Issue; message: string }>(`/issues/${number}/unarchive`, withProject({ method: 'POST' }, projectId))
}

export function archiveAllCompleted(projectId?: string | null) {
  return request<{ archived: number; skipped: number; skippedNumbers: number[]; message: string }>('/issues/archive-completed', withProject({ method: 'POST' }, projectId))
}

export function addPrerequisite(number: number, prerequisiteNumber: number, projectId?: string | null) {
  return request<{ issue: Issue; message: string }>(`/issues/${number}/prerequisites`, withProject({
    method: 'POST',
    body: JSON.stringify({ prerequisiteNumber }),
  }, projectId))
}

export function removePrerequisite(number: number, prerequisiteNumber: number, projectId?: string | null) {
  return request<{ issue: Issue; message: string }>(`/issues/${number}/prerequisites/${prerequisiteNumber}`, withProject({
    method: 'DELETE',
  }, projectId))
}
