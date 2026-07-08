import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Button } from '@/shared/ui/components/button'
import { WorkflowStage, useLiveTask } from '../../../entities/issue'
import { ApiError } from '../../../shared/api/client'
import { rebaseIssue } from '../../../entities/issue'
import { useWorkspaceStatus } from '../../../entities/issue'
import { useProject } from '../../../entities/project'

interface BranchBarProps {
  issueNumber: number
  stage: string | null
  isAgentRunning: boolean
  baseBranch?: string | null
  allowRebase?: boolean
}

export function BranchBar({ issueNumber, stage, baseBranch: fallbackBaseBranch, allowRebase = false }: BranchBarProps) {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  const { rebaseConflict } = useLiveTask()
  const [rebaseQueued, setRebaseQueued] = useState(false)

  const { data, isLoading } = useWorkspaceStatus(issueNumber, true)

  const rebaseMutation = useMutation({
    mutationFn: () => rebaseIssue(issueNumber, projectId),
    onSuccess: (data) => {
      if (data.status === 'queued') setRebaseQueued(true)
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber, projectId, 'workspace-status'] })
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
      queryClient.invalidateQueries({ queryKey: ['issues'] })
    },
  })

  const rebaseResult = rebaseMutation.data
  const isChecking = isLoading || data === undefined
  if (!isChecking && (!data || !data.exists) && !allowRebase) return null

  const rawAhead = data?.ahead
  const rawBehind = data?.behind
  const hasWorkspaceError = !!data?.reason || data?.exists === false
  const isUpstreamUnknown = !!data?.reason
  const hasAheadBehind = data?.exists === true && !hasWorkspaceError && typeof rawAhead === 'number' && typeof rawBehind === 'number'
  const isBehind = hasAheadBehind && rawBehind > 0
  const isConflictResolving = rebaseConflict?.issueNumber === issueNumber && rebaseConflict.status === 'resolving'
  const isConflictFailed = rebaseConflict?.issueNumber === issueNumber && rebaseConflict.status === 'failed'
  const canRequestRebase = hasAheadBehind && allowRebase && !rebaseMutation.isPending && !isConflictResolving && !rebaseQueued
  const isRebasing = data?.rebaseInProgress === true || rebaseMutation.isPending || isConflictResolving || rebaseQueued
  const hasConflicts = rebaseResult?.conflicts && rebaseResult.conflicts.length > 0
    ? rebaseResult.conflicts
    : data?.conflictingFiles && data.conflictingFiles.length > 0
      ? data.conflictingFiles
      : null

  const ahead = hasAheadBehind ? rawAhead : 0
  const behind = hasAheadBehind ? rawBehind : 0
  const branch = data?.branch ?? 'workspace'
  const baseBranch = data?.baseBranch ?? fallbackBaseBranch ?? 'master'
  const isDone = stage === WorkflowStage.Done

  if (isRebasing) {
    return (
      <div className="mb-8" data-testid="branch-bar-frame">
        <div data-testid="branch-bar" className="rounded-lg border border-info-border bg-info-subtle px-4 py-3 space-y-2">
          <div className="flex items-center gap-3">
            {!rebaseQueued && (
              <svg className="h-4 w-4 animate-spin text-info" viewBox="0 0 24 24" fill="none">
                <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
              </svg>
            )}
            <span className="text-sm font-medium text-info">
              {rebaseQueued ? 'Rebase queued' : isConflictResolving ? 'Resolving conflicts...' : 'Rebasing...'}
            </span>
            <span className="text-xs text-info font-mono">{branch}</span>
          </div>
          {hasConflicts && (
            <div className="rounded-md bg-danger-subtle px-3 py-2 text-xs text-danger">
              <span>Conflicting files:</span>
              <ul className="mt-1 ml-3 list-disc">
                {hasConflicts.map((f) => (
                  <li key={f} className="font-mono">{f}</li>
                ))}
              </ul>
            </div>
          )}
        </div>
      </div>
    )
  }

  if (isUpstreamUnknown) {
    return (
      <div className="mb-8" data-testid="branch-bar-frame">
        <div data-testid="branch-bar" className="rounded-lg border border-border bg-muted px-4 py-3">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div className="flex items-center gap-3 min-w-0">
              <span className="text-sm font-mono font-medium text-foreground truncate">{branch}</span>
              <span className="text-xs text-muted-foreground shrink-0">onto</span>
              <span className="text-xs font-mono text-muted-foreground/80 shrink-0">{baseBranch}</span>
            </div>
            <div className="flex w-full flex-wrap items-center gap-3 sm:w-auto sm:shrink-0">
              <span className="text-xs font-medium text-muted-foreground">未能检查上游</span>
              {allowRebase && (
                <Button
                  variant="outline"
                  onClick={() => rebaseMutation.mutate()}
                  disabled={!canRequestRebase}
                  title={
                    !hasAheadBehind
                      ? 'Waiting for workspace status'
                      : isConflictResolving
                        ? 'Conflict resolution in progress'
                        : undefined
                  }
                  className="h-auto w-full rounded-md px-3 py-1.5 text-xs font-medium sm:w-auto"
                >
                  Rebase onto {baseBranch}
                </Button>
              )}
            </div>
          </div>
        </div>
      </div>
    )
  }

  if (isBehind || allowRebase) {
    return (
      <div className="mb-8" data-testid="branch-bar-frame">
        <div data-testid="branch-bar" className="rounded-lg border border-warning-border bg-warning-subtle px-4 py-3 space-y-2">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div className="flex min-w-[14rem] flex-1 items-center gap-3">
              <span className="text-sm font-mono font-medium text-foreground truncate">{branch}</span>
              <span className="text-xs text-muted-foreground shrink-0">onto</span>
              <span className="text-xs font-mono text-muted-foreground/80 shrink-0">{baseBranch}</span>
            </div>
            <div className="flex w-full flex-wrap items-center gap-3 sm:w-auto sm:shrink-0">
              {isChecking || !hasAheadBehind ? (
                <span className="text-xs font-medium text-warning">Checking upstream...</span>
              ) : (
                <span className="text-xs font-medium text-warning">
                  {ahead > 0 && <span className="text-muted-foreground">↑{ahead} </span>}
                  {behind > 0 ? <span>↓{behind} behind</span> : <span>up to date</span>}
                </span>
              )}
              <Button
                variant="warning"
                onClick={() => rebaseMutation.mutate()}
                disabled={!canRequestRebase}
                title={
                  !hasAheadBehind
                    ? 'Waiting for workspace status'
                    : isConflictResolving
                      ? 'Conflict resolution in progress'
                      : undefined
                }
                className="h-auto w-full rounded-md px-3 py-1.5 text-xs font-medium sm:w-auto"
              >
                Rebase onto {baseBranch}
              </Button>
            </div>
          </div>
          {isDone && (
            <p className="text-xs text-warning">
              This Done workflow workspace is retained for review, traceability, diff inspection, and debugging. Archiving will remove the retained workspace.
            </p>
          )}
          {rebaseMutation.isError && (
            <div className="rounded-md bg-danger-subtle px-3 py-2 text-xs text-danger">
              {rebaseMutation.error instanceof ApiError
                ? rebaseMutation.error.message
                : 'Rebase failed'}
            </div>
          )}
          {hasConflicts && !isConflictFailed && (
            <div className="rounded-md bg-danger-subtle px-3 py-2 text-xs text-danger">
              <span>Conflicting files:</span>
              <ul className="mt-1 ml-3 list-disc">
                {hasConflicts.map((f) => (
                  <li key={f} className="font-mono">{f}</li>
                ))}
              </ul>
            </div>
          )}
          {isConflictFailed && (
            <div className="rounded-md bg-danger-subtle px-3 py-2 text-xs text-danger">
              <span>Conflict resolution failed{rebaseConflict?.error ? `: ${rebaseConflict.error}` : ''}</span>
            </div>
          )}
        </div>
      </div>
    )
  }

  return (
    <div className="mb-8" data-testid="branch-bar-frame">
      <div data-testid="branch-bar" className="rounded-lg border bg-background px-4 py-3">
        <div className="flex items-center justify-between gap-3">
          <div className="flex items-center gap-3 min-w-0">
            <span className="text-sm font-mono font-medium text-foreground truncate">{branch}</span>
            <span className="text-xs text-muted-foreground shrink-0">onto</span>
            <span className="text-xs font-mono text-muted-foreground/80 shrink-0">{baseBranch}</span>
          </div>
          <div className="flex items-center gap-3 shrink-0">
            {ahead > 0 && <span className="text-xs text-muted-foreground">↑{ahead} ahead</span>}
            <span className="text-xs font-medium text-success">up to date</span>
          </div>
        </div>
        {isDone && (
          <p className="mt-2 text-xs text-muted-foreground">
            This Done workflow workspace is retained for review, traceability, diff inspection, and debugging. Archiving will remove the retained workspace.
          </p>
        )}
      </div>
    </div>
  )
}
