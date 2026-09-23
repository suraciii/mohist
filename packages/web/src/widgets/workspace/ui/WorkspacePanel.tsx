import { useState, useEffect } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ApiError } from '../../../shared/api/client'
import {
  cleanupIssueWorkspace,
  issueDetailKeys,
  issueListKeys,
  issueWorkflowKeys,
  onRebaseEvent,
  rebaseIssue,
  useLiveTask,
  useWorkspaceStatus,
} from '../../../entities/issue'
import { useProject } from '../../../entities/project'
import { Button } from '@/shared/ui/components/button'
import { AlertDialog } from '@/shared/ui/components/alert-dialog'

interface WorkspacePanelProps {
  issueNumber: number
  isAgentRunning: boolean
  isDone?: boolean
  workspaceStatusHook?: typeof useWorkspaceStatus
}

type RebaseResult = {
  type: 'success' | 'info' | 'error' | 'queued'
  message: string
  conflicts?: string[]
}

type CleanupResult = {
  type: 'success' | 'info' | 'error'
  message: string
}

type RebaseStep = 'fetching' | 'checking' | 'rebasing' | 'verifying'

const STEP_LABELS: Record<RebaseStep, string> = {
  fetching: 'Fetching latest...',
  checking: 'Checking fast-forward...',
  rebasing: 'Rebasing onto master...',
  verifying: 'Verifying build...',
}

const DIRECTORY_LABELS = {
  removed: 'Removed',
  already_absent: 'Already absent',
  in_use: 'In use',
  unsafe: 'Unable to inspect safely',
  deletion_failed: 'Deletion failed',
  unknown: 'Result pending confirmation',
} as const

const DIRECTORY_GUIDANCE = {
  removed: 'Local files were removed. Remote work and issue history remain.',
  already_absent: 'The directory was already absent when checked; no space is attributed to this cleanup.',
  in_use: 'Work is still using this Home. Retry cleanup after that work finishes.',
  unsafe:
    'The Runner cannot verify that this Home is safe to remove. Check the Runner and retry after the cause is resolved.',
  deletion_failed: 'Deletion may have removed some files. Inspect the Home before retrying or resuming work.',
  unknown: 'The request may have run, but its result is unconfirmed. Check the Home on the Runner before retrying.',
} as const

export function WorkspacePanel({
  issueNumber,
  isAgentRunning,
  isDone,
  workspaceStatusHook = useWorkspaceStatus,
}: WorkspacePanelProps) {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  const { data: status, isLoading } = workspaceStatusHook(issueNumber, true)
  const { rebaseConflict } = useLiveTask()
  const [rebaseResult, setRebaseResult] = useState<RebaseResult | null>(null)
  const [cleanupResult, setCleanupResult] = useState<CleanupResult | null>(null)
  const [confirmCleanup, setConfirmCleanup] = useState(false)
  const [rebaseStep, setRebaseStep] = useState<RebaseStep | null>(null)

  useEffect(() => {
    return onRebaseEvent((event) => {
      if (event.issueNumber !== issueNumber) return
      if (event.type === 'rebase_started') {
        setRebaseResult(null)
        setRebaseStep(null)
      } else if (event.type === 'rebase_progress') {
        setRebaseStep(event.step)
      } else if (event.type === 'rebase_completed') {
        setRebaseStep(null)
        if (event.rebased) {
          setRebaseResult({ type: 'success', message: 'Rebase successful' })
        } else {
          setRebaseResult({ type: 'info', message: 'Already up to date' })
        }
      } else if (event.type === 'rebase_conflict') {
        setRebaseStep(null)
        if (event.status === 'resolving') {
          setRebaseResult({
            type: 'info',
            message: 'Conflicts detected, resolving via agent...',
            conflicts: event.conflicts,
          })
        } else {
          setRebaseResult({ type: 'error', message: 'Rebase aborted due to conflicts', conflicts: event.conflicts })
        }
      }
    })
  }, [issueNumber])

  const rebaseMutation = useMutation({
    mutationFn: () => rebaseIssue(issueNumber, projectId),
    onSuccess: (data) => {
      if (data.status === 'queued') {
        setRebaseResult({ type: 'queued', message: 'Rebase task queued' })
      } else if (data.conflicts && data.conflicts.length > 0) {
        setRebaseResult({ type: 'error', message: 'Rebase aborted due to conflicts', conflicts: data.conflicts })
      } else if (data.rebased) {
        setRebaseResult({ type: 'success', message: 'Rebase successful' })
      } else {
        setRebaseResult({ type: 'info', message: 'Already up to date' })
      }
      queryClient.invalidateQueries({ queryKey: issueListKeys.project(projectId) })
      queryClient.invalidateQueries({ queryKey: issueDetailKeys.detail(projectId, issueNumber), exact: true })
      queryClient.invalidateQueries({ queryKey: issueWorkflowKeys.workspace(projectId, issueNumber), exact: true })
    },
    onError: (error: Error) => {
      if (error instanceof ApiError && error.code === 'rebase_already_pending') {
        setRebaseResult({ type: 'queued', message: 'Rebase task already queued' })
        return
      }
      if (error instanceof ApiError && error.data && typeof error.data === 'object') {
        const d = error.data as { conflicts?: string[] }
        if (d.conflicts && d.conflicts.length > 0) {
          setRebaseResult({ type: 'error', message: 'Rebase aborted due to conflicts', conflicts: d.conflicts })
          return
        }
      }
      setRebaseResult({ type: 'error', message: error.message })
    },
  })

  const cleanupMutation = useMutation({
    mutationFn: () => cleanupIssueWorkspace(issueNumber, projectId),
    onSuccess: (data) => {
      setCleanupResult({
        type: data.removed ? 'success' : 'info',
        message: data.message,
      })
      queryClient.invalidateQueries({ queryKey: issueListKeys.project(projectId) })
      queryClient.invalidateQueries({ queryKey: issueDetailKeys.detail(projectId, issueNumber), exact: true })
      queryClient.invalidateQueries({ queryKey: issueWorkflowKeys.workspace(projectId, issueNumber), exact: true })
    },
    onError: (error: Error) => {
      setCleanupResult({ type: 'error', message: error.message })
    },
    onSettled: () => {
      setConfirmCleanup(false)
      queryClient.invalidateQueries({ queryKey: issueWorkflowKeys.workspace(projectId, issueNumber), exact: true })
    },
  })

  if (!status?.exists && !status?.directory && !status?.homeRunnerId) return null
  if (isLoading) return null

  const isBehind = (status.behind ?? 0) > 0
  const isAhead = (status.ahead ?? 0) > 0
  const isUpToDate = !isBehind && !isAhead
  const isConflictResolving = rebaseConflict?.issueNumber === issueNumber && rebaseConflict.status === 'resolving'
  const isConflictFailed = rebaseConflict?.issueNumber === issueNumber && rebaseConflict.status === 'failed'
  const isRebasing = rebaseMutation.isPending || status.rebaseInProgress === true || isConflictResolving
  const isUpstreamUnknown = status.reason === 'fetch_failed'
  const canCleanup = (status.cleanupEligible ?? Boolean(isDone)) && !isAgentRunning && !isRebasing
  const cleanupComplete = status.directory?.outcome === 'removed' || status.directory?.outcome === 'already_absent'

  return (
    <div className="rounded-lg border border-gray-200 bg-white p-4">
      <h2 className="text-sm font-semibold text-gray-700 mb-1">Workspace</h2>
      {status.homeRunnerId && (
        <p className="text-xs text-gray-500 mb-2">
          issue-{issueNumber} on {status.homeRunnerId}
        </p>
      )}
      {isDone && status.exists && (
        <p className="text-xs text-gray-400 mb-2">Local files remain until cleanup removes them.</p>
      )}

      {status.directory && (
        <div className="mb-3 text-xs text-gray-600">
          <div className="font-medium text-gray-800">Directory: {DIRECTORY_LABELS[status.directory.outcome]}</div>
          <div>
            Observed {new Date(status.directory.observedAt).toLocaleString()} on {status.directory.runnerId}
          </div>
          {status.directory.reason && <div>{status.directory.reason.replaceAll('_', ' ')}</div>}
          <div>{DIRECTORY_GUIDANCE[status.directory.outcome]}</div>
          {status.reason === 'runner_unavailable' && (
            <div>Runner is offline. This is the last recorded observation, not a current directory check.</div>
          )}
          {status.directory.estimatedBytes != null && status.directory.measuredAt && (
            <div>
              Estimated size: {status.directory.estimatedBytes.toLocaleString()} bytes, measured{' '}
              {new Date(status.directory.measuredAt).toLocaleString()}
            </div>
          )}
        </div>
      )}
      {status.reason === 'runner_unavailable' && !status.directory && (
        <p className="mb-3 text-xs text-amber-700">
          Runner is offline. The Home cannot be checked now; retry when the Runner is available.
        </p>
      )}

      {status.branch && <div className="text-xs text-gray-500 mb-2 font-mono">{status.branch}</div>}

      {status.exists && (
        <div className="mb-3">
          {isUpstreamUnknown && !isRebasing && (
            <span className="inline-flex items-center gap-1.5 text-xs text-gray-500">Unable to check upstream</span>
          )}
          {isUpToDate && !isUpstreamUnknown && (
            <span className="inline-flex items-center gap-1.5 text-xs text-green-700">
              <svg className="h-3.5 w-3.5" viewBox="0 0 20 20" fill="currentColor">
                <path
                  fillRule="evenodd"
                  d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.857-9.809a.75.75 0 00-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 10-1.06 1.061l2.5 2.5a.75.75 0 001.137-.089l4-5.5z"
                  clipRule="evenodd"
                />
              </svg>
              Up to date
            </span>
          )}
          {isBehind && !isAhead && !isUpstreamUnknown && (
            <span className="inline-flex items-center gap-1.5 text-xs text-amber-700">
              <svg className="h-3.5 w-3.5" viewBox="0 0 20 20" fill="currentColor">
                <path
                  fillRule="evenodd"
                  d="M8.485 2.495c.673-1.167 2.357-1.167 3.03 0l6.28 10.875c.673 1.167-.168 2.625-1.516 2.625H3.72c-1.347 0-2.189-1.458-1.515-2.625L8.485 2.495zM10 6a.75.75 0 01.75.75v3.5a.75.75 0 01-1.5 0v-3.5A.75.75 0 0110 6zm0 9a1 1 0 100-2 1 1 0 000 2z"
                  clipRule="evenodd"
                />
              </svg>
              {status.behind} {status.behind === 1 ? 'commit' : 'commits'} behind master
            </span>
          )}
          {isAhead && !isBehind && !isUpstreamUnknown && (
            <span className="inline-flex items-center gap-1.5 text-xs text-gray-600">
              {status.ahead} {status.ahead === 1 ? 'commit' : 'commits'} ahead of master
            </span>
          )}
          {isAhead && isBehind && !isUpstreamUnknown && (
            <span className="inline-flex items-center gap-1.5 text-xs text-amber-700">
              <svg className="h-3.5 w-3.5" viewBox="0 0 20 20" fill="currentColor">
                <path
                  fillRule="evenodd"
                  d="M8.485 2.495c.673-1.167 2.357-1.167 3.03 0l6.28 10.875c.673 1.167-.168 2.625-1.516 2.625H3.72c-1.347 0-2.189-1.458-1.515-2.625L8.485 2.495zM10 6a.75.75 0 01.75.75v3.5a.75.75 0 01-1.5 0v-3.5A.75.75 0 0110 6zm0 9a1 1 0 100-2 1 1 0 000 2z"
                  clipRule="evenodd"
                />
              </svg>
              {status.ahead} ahead, {status.behind} behind master
            </span>
          )}
        </div>
      )}

      {isRebasing && !rebaseStep && isConflictResolving && (
        <div className="mb-3 flex items-center gap-2 text-xs text-blue-700">
          <svg className="h-3.5 w-3.5 animate-spin" viewBox="0 0 24 24" fill="none">
            <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
            <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
          </svg>
          Resolving conflicts via agent...
        </div>
      )}

      {isRebasing && rebaseStep && (
        <div className="mb-3 flex items-center gap-2 text-xs text-blue-700">
          <svg className="h-3.5 w-3.5 animate-spin" viewBox="0 0 24 24" fill="none">
            <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
            <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
          </svg>
          {STEP_LABELS[rebaseStep]}
        </div>
      )}

      {rebaseResult && !isConflictFailed && (
        <div
          className={`mb-3 rounded-md px-3 py-2 text-xs ${
            rebaseResult.type === 'success'
              ? 'bg-green-50 text-green-700'
              : rebaseResult.type === 'error'
                ? 'bg-red-50 text-red-700'
                : 'bg-blue-50 text-blue-700'
          }`}
        >
          <div>{rebaseResult.message}</div>
          {rebaseResult.conflicts && rebaseResult.conflicts.length > 0 && (
            <ul className="mt-1 list-disc list-inside text-red-600">
              {rebaseResult.conflicts.map((f) => (
                <li key={f}>{f}</li>
              ))}
            </ul>
          )}
        </div>
      )}

      {isConflictFailed && (
        <div className="mb-3 rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
          Conflict resolution failed{rebaseConflict?.error ? `: ${rebaseConflict.error}` : ''}
        </div>
      )}

      {cleanupResult && (
        <div
          className={`mb-3 rounded-md px-3 py-2 text-xs ${
            cleanupResult.type === 'success'
              ? 'bg-green-50 text-green-700'
              : cleanupResult.type === 'error'
                ? 'bg-red-50 text-red-700'
                : 'bg-blue-50 text-blue-700'
          }`}
        >
          {cleanupResult.message}
        </div>
      )}

      {status.exists && (isRebasing || !isUpstreamUnknown) && (
        <Button
          variant="outline"
          onClick={() => {
            setRebaseResult(null)
            rebaseMutation.mutate()
          }}
          disabled={isRebasing}
          className={`h-auto w-full px-3 py-2 ${
            isBehind
              ? 'border-amber-300 bg-amber-50 text-amber-800 hover:bg-amber-100'
              : 'border-gray-300 bg-white text-gray-700 hover:bg-gray-50'
          }`}
        >
          {isRebasing ? (
            <>
              <svg className="h-4 w-4 animate-spin" viewBox="0 0 24 24" fill="none">
                <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
              </svg>
              Rebasing...
            </>
          ) : (
            'Rebase onto master'
          )}
        </Button>
      )}

      {!cleanupComplete && (
        <Button
          variant="outline"
          onClick={() => {
            setCleanupResult(null)
            setConfirmCleanup(true)
          }}
          disabled={!canCleanup || cleanupMutation.isPending}
          className="mt-2 h-auto w-full border-gray-300 bg-white px-3 py-2 text-gray-700 hover:bg-gray-50"
        >
          {cleanupMutation.isPending
            ? 'Cleaning up workspace...'
            : isAgentRunning
              ? 'Clean up after completion'
              : 'Clean up workspace'}
        </Button>
      )}
      <AlertDialog
        open={confirmCleanup}
        onOpenChange={setConfirmCleanup}
        title={`Clean up issue-${issueNumber}?`}
        description={`Remove local files on Runner ${status.homeRunnerId ?? status.directory?.runnerId ?? 'unknown'}? Unpushed commits and unuploaded work may be lost. Issue history, remote branches, and uploaded artifacts remain.`}
        confirmLabel="Remove local files"
        tone="destructive"
        loading={cleanupMutation.isPending}
        onConfirm={() => cleanupMutation.mutate()}
        data-testid="workspace-cleanup-confirm"
      />
    </div>
  )
}
