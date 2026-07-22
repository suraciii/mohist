import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { issueDetailKeys, issueListKeys, issueWorkflowKeys, rebaseIssue, useLiveTask, useWorkspaceStatus } from '../../../entities/issue'
import { useProject } from '../../../entities/project'

export interface RebaseRecoveryWorkspaceStatus {
  exists?: boolean
  reason?: string
  branch?: string
  baseBranch?: string
  ahead?: number
  behind?: number
  rebaseInProgress?: boolean
  conflictingFiles?: string[]
}

export interface RebaseRecoveryWorkspaceView {
  data: RebaseRecoveryWorkspaceStatus | undefined
  isLoading: boolean
  isChecking: boolean
  hasAheadBehind: boolean
  isUpstreamUnknown: boolean
  isBehind: boolean
  ahead: number
  behind: number
  branch: string
  baseBranch: string
}

export interface RebaseRecoveryResult {
  trigger: () => void
  isPending: boolean
  isQueued: boolean
  isRebasing: boolean
  isConflictResolving: boolean
  isConflictFailed: boolean
  canRequest: boolean
  hasConflicts: string[] | null
  error: Error | null
  rebaseConflict: { issueNumber: number; status: string; error?: string } | null
  workspace: RebaseRecoveryWorkspaceView
}

export function useRebaseRecovery(issueNumber: number, enabled: boolean = true): RebaseRecoveryResult {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  const { rebaseConflict } = useLiveTask()
  const [rebaseQueued, setRebaseQueued] = useState(false)

  const { data, isLoading } = useWorkspaceStatus(issueNumber, enabled)

  const rebaseMutation = useMutation({
    mutationFn: () => rebaseIssue(issueNumber, projectId),
    onSuccess: (result: { status?: string }) => {
      if (result.status === 'queued') setRebaseQueued(true)
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: issueWorkflowKeys.workspace(projectId, issueNumber), exact: true })
      queryClient.invalidateQueries({ queryKey: issueDetailKeys.detail(projectId, issueNumber), exact: true })
      queryClient.invalidateQueries({ queryKey: issueListKeys.project(projectId) })
    },
  })

  const rebaseResult = rebaseMutation.data as { conflicts?: string[] } | undefined
  const isChecking = isLoading || data === undefined
  const rawAhead = data?.ahead
  const rawBehind = data?.behind
  const hasWorkspaceError = !!data?.reason || data?.exists === false
  const isUpstreamUnknown = !!data?.reason
  const hasAheadBehind = data?.exists === true && !hasWorkspaceError && typeof rawAhead === 'number' && typeof rawBehind === 'number'
  const isBehind = hasAheadBehind && rawBehind > 0
  const isConflictResolving = rebaseConflict?.issueNumber === issueNumber && rebaseConflict.status === 'resolving'
  const isConflictFailed = rebaseConflict?.issueNumber === issueNumber && rebaseConflict.status === 'failed'
  const isRebasing = data?.rebaseInProgress === true || rebaseMutation.isPending || isConflictResolving || rebaseQueued
  const canRequest = hasAheadBehind && !rebaseMutation.isPending && !isConflictResolving && !rebaseQueued
  const hasConflicts = rebaseResult?.conflicts && rebaseResult.conflicts.length > 0
    ? rebaseResult.conflicts
    : data?.conflictingFiles && data.conflictingFiles.length > 0
      ? data.conflictingFiles
      : null

  return {
    trigger: () => rebaseMutation.mutate(),
    isPending: rebaseMutation.isPending,
    isQueued: rebaseQueued,
    isRebasing,
    isConflictResolving,
    isConflictFailed,
    canRequest,
    hasConflicts,
    error: rebaseMutation.error,
    rebaseConflict,
    workspace: {
      data,
      isLoading,
      isChecking,
      hasAheadBehind,
      isUpstreamUnknown,
      isBehind,
      ahead: hasAheadBehind ? rawAhead : 0,
      behind: hasAheadBehind ? rawBehind : 0,
      branch: data?.branch ?? 'workspace',
      baseBranch: data?.baseBranch ?? 'master',
    },
  }
}

export type RebaseRecovery = ReturnType<typeof useRebaseRecovery>
