import { request, withProject, ApiError } from '../../../shared/api/client'
import type { CommitDiffResponse, Comment, Issue, IssueCommitsResponse, IssueDiffResponse, WorkflowTimeline } from '../model/types'

export function getIssues(params?: { stage?: string; label?: string; projectId?: string }) {
  const search = new URLSearchParams()
  if (params?.projectId) search.set('projectId', params.projectId)
  if (params?.stage) search.set('stage', params.stage)
  if (params?.label) search.set('label', params.label)
  const qs = search.toString()
  return request<Issue[]>(`/issues${qs ? `?${qs}` : ''}`)
}

export function getIssue(number: number, projectId?: string | null) {
  return request<Issue>(withProject(`/issues/${number}`, projectId))
}

export function createIssue(data: { title: string; body?: string; labels?: string[]; model?: string; agentConfig?: Record<string, unknown>; priority?: string; projectId?: string; repositoryName?: string }) {
  return request<Issue>('/issues', {
    method: 'POST',
    body: JSON.stringify(data),
  })
}

export function updateIssue(number: number, data: { title?: string; body?: string; addLabels?: string[]; removeLabels?: string[]; model?: string | null; agentConfig?: Record<string, unknown> | null; stageModels?: Record<string, string> | null; priority?: string | null }, projectId?: string | null) {
  return request<Issue>(withProject(`/issues/${number}`, projectId), {
    method: 'PATCH',
    body: JSON.stringify(data),
  })
}

export function startIssue(number: number, projectId?: string | null) {
  return request<{ issue: Issue; message: string }>(withProject(`/issues/${number}/start`, projectId), { method: 'POST' })
}

export function closeIssue(number: number, projectId?: string | null) {
  return request<{ issue: Issue; message: string }>(withProject(`/issues/${number}/close`, projectId), { method: 'POST' })
}

export function reopenIssue(number: number, projectId?: string | null) {
  return request<{ issue: Issue; message: string }>(withProject(`/issues/${number}/reopen`, projectId), { method: 'POST' })
}

export function resumeIssue(number: number, projectId?: string | null) {
  return request<{ issue: Issue; message: string }>(withProject(`/issues/${number}/resume`, projectId), { method: 'POST' })
}

export function retryIssue(number: number, projectId?: string | null) {
  return request<{ issue: Issue; message: string }>(withProject(`/issues/${number}/retry`, projectId), { method: 'POST' })
}

export function approveIssue(number: number, projectId?: string | null) {
  return request<{ issue: Issue; context: string | null; message: string }>(withProject(`/issues/${number}/approve`, projectId), { method: 'POST' })
}

export function rejectIssue(number: number, data: { message?: string }, projectId?: string | null) {
  return request<{ issue: Issue; message: string }>(withProject(`/issues/${number}/reject`, projectId), {
    method: 'POST',
    body: JSON.stringify(data),
  })
}

export function getIssueDiff(number: number, projectId?: string | null) {
  return request<IssueDiffResponse>(withProject(`/issues/${number}/diff`, projectId))
}

export function getIssueCommits(number: number, projectId?: string | null) {
  return request<IssueCommitsResponse>(withProject(`/issues/${number}/commits`, projectId))
}

export function getCommitDiff(number: number, hash: string, projectId?: string | null) {
  return request<CommitDiffResponse>(withProject(`/issues/${number}/commits/${hash}/diff`, projectId))
}

export function getFileContent(number: number, filePath: string, projectId?: string | null) {
  return request<{ base: string; head: string }>(withProject(`/issues/${number}/file-content?path=${encodeURIComponent(filePath)}`, projectId))
}

export function addComment(issueNumber: number, body: string, projectId?: string | null) {
  return request<Comment>(withProject(`/issues/${issueNumber}/comments`, projectId), {
    method: 'POST',
    body: JSON.stringify({ body }),
  })
}

export function deleteComment(issueNumber: number, commentId: string, projectId?: string | null) {
  return request<{ message: string }>(withProject(`/issues/${issueNumber}/comments/${commentId}`, projectId), {
    method: 'DELETE',
  })
}

export function getLabels() {
  return request<string[]>('/labels')
}

export function getWorkflowYaml(number: number, projectId?: string | null) {
  return request<{ issueNumber: number; workflowRunId: string; yaml: string }>(withProject(`/issues/${number}/workflow/yaml`, projectId))
}

export function getWorkflowTimeline(number: number, projectId?: string | null) {
  return request<WorkflowTimeline>(withProject(`/issues/${number}/workflow/timeline`, projectId))
}

export function getIssueWorkflowDefinitionVar(number: number, name: string, projectId?: string | null) {
  return request<{ issueNumber: number; workflowRunId: string; name: string; value: unknown }>(withProject(`/issues/${number}/workflow/vars/${encodeURIComponent(name)}`, projectId))
}

export function patchIssueWorkflowDefinitionVar(number: number, name: string, value: unknown, projectId?: string | null) {
  return request<{ issueNumber: number; workflowRunId: string; affected: string; vars: Record<string, unknown>; stageVars?: Record<string, unknown> | null }>(withProject(`/issues/${number}/workflow/vars/${encodeURIComponent(name)}`, projectId), {
    method: 'PATCH',
    body: JSON.stringify(value),
  })
}

export function patchIssueWorkflowStageDefinitionVar(number: number, stage: string, name: string, value: unknown, projectId?: string | null) {
  return request<{ issueNumber: number; workflowRunId: string; affected: string; vars: Record<string, unknown>; stageVars?: Record<string, unknown> | null }>(withProject(`/issues/${number}/workflow/stages/${encodeURIComponent(stage)}/vars/${encodeURIComponent(name)}`, projectId), {
    method: 'PATCH',
    body: JSON.stringify(value),
  })
}

export async function rebaseIssue(number: number, projectId?: string | null) {
  try {
    return await request<{ rebased: boolean; rePlan?: boolean; conflicts?: string[]; buildPassed?: boolean; message: string; status?: 'queued'; workflowRunId?: string; taskId?: string; stage?: string; baseBranch?: string }>(withProject(`/issues/${number}/rebase`, projectId), { method: 'POST' })
  } catch (err) {
    if (err instanceof ApiError && err.data && typeof err.data === 'object') {
      return err.data as { rebased: boolean; rePlan?: boolean; conflicts?: string[]; buildPassed?: boolean; message: string; status?: 'queued'; workflowRunId?: string; taskId?: string; stage?: string; baseBranch?: string }
    }
    throw err
  }
}

export function rerunIssue(number: number, projectId?: string | null) {
  return request<{ issue: Issue; message: string }>(withProject(`/issues/${number}/rerun`, projectId), { method: 'POST' })
}

export function forceStopIssue(number: number, projectId?: string | null) {
  return request<{ ok: boolean; issueNumber: number }>(withProject(`/issues/${number}/force-stop`, projectId), { method: 'POST' })
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
  }>(withProject(`/issues/${number}/worktree-status`, projectId))
}

export function cleanupIssueWorktree(number: number, projectId?: string | null) {
  return request<{
    removed: boolean
    message: string
    resources: Array<{ type: string; status: string; path?: string | null; reason?: string | null }>
  }>(withProject(`/issues/${number}/cleanup`, projectId), { method: 'POST' })
}

export function archiveIssue(number: number, projectId?: string | null) {
  return request<{ issue: Issue; message: string; warning?: string }>(withProject(`/issues/${number}/archive`, projectId), { method: 'POST' })
}

export function unarchiveIssue(number: number, projectId?: string | null) {
  return request<{ issue: Issue; message: string }>(withProject(`/issues/${number}/unarchive`, projectId), { method: 'POST' })
}

export function archiveAllCompleted(projectId?: string | null) {
  return request<{ archived: number; skipped: number; skippedNumbers: number[]; message: string }>(withProject('/issues/archive-completed', projectId), { method: 'POST' })
}

export function addPrerequisite(number: number, prerequisiteNumber: number, projectId?: string | null) {
  return request<{ issue: Issue; message: string }>(withProject(`/issues/${number}/prerequisites`, projectId), {
    method: 'POST',
    body: JSON.stringify({ prerequisiteNumber }),
  })
}

export function removePrerequisite(number: number, prerequisiteNumber: number, projectId?: string | null) {
  return request<{ issue: Issue; message: string }>(withProject(`/issues/${number}/prerequisites/${prerequisiteNumber}`, projectId), {
    method: 'DELETE',
  })
}
