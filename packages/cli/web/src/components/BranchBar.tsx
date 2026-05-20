import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Stage } from '../lib/types'
import { api, ApiError } from '../lib/api'
import { useWorktreeStatus } from '../hooks/useQueries'
import { useLiveTask } from '../hooks/useSSE'

const BRANCH_BAR_STAGES = new Set<string>([Stage.Plan, Stage.Build, Stage.Check, Stage.Done])

interface BranchBarProps {
  issueNumber: number
  stage: string
  isAgentRunning: boolean
}

export function BranchBar({ issueNumber, stage, isAgentRunning }: BranchBarProps) {
  const queryClient = useQueryClient()
  const { rebaseConflict } = useLiveTask()

  const { data, isLoading } = useWorktreeStatus(issueNumber, BRANCH_BAR_STAGES.has(stage))

  const rebaseMutation = useMutation({
    mutationFn: () => api.rebaseIssue(issueNumber),
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber, 'worktree-status'] })
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber, 'workflow-run'] })
      queryClient.invalidateQueries({ queryKey: ['issues'] })
    },
  })

  if (!BRANCH_BAR_STAGES.has(stage)) return null
  if (isLoading) return null
  if (!data || !data.exists) return null

  const rebaseResult = rebaseMutation.data
  const isBehind = (data.behind ?? 0) > 0
  const isConflictResolving = rebaseConflict?.issueNumber === issueNumber && rebaseConflict.status === 'resolving'
  const isConflictFailed = rebaseConflict?.issueNumber === issueNumber && rebaseConflict.status === 'failed'
  const isRebasing = data.rebaseInProgress === true || rebaseMutation.isPending || isConflictResolving
  const hasConflicts = rebaseResult?.conflicts && rebaseResult.conflicts.length > 0
    ? rebaseResult.conflicts
    : data.conflictingFiles && data.conflictingFiles.length > 0
      ? data.conflictingFiles
      : null

  const ahead = data.ahead ?? 0
  const behind = data.behind ?? 0
  const branch = data.branch ?? `mo/issue-${issueNumber}`
  const baseBranch = data.baseBranch ?? 'master'
  const isDone = stage === Stage.Done

  if (isRebasing) {
    return (
      <div className="rounded-lg border border-blue-200 bg-blue-50 px-4 py-3">
        <div className="flex items-center gap-3">
          <svg className="h-4 w-4 animate-spin text-blue-600" viewBox="0 0 24 24" fill="none">
            <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
            <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
          </svg>
          <span className="text-sm font-medium text-blue-800">{isConflictResolving ? 'Resolving conflicts...' : 'Rebasing...'}</span>
          <span className="text-xs text-blue-600 font-mono">{branch}</span>
        </div>
      </div>
    )
  }

  if (isBehind) {
    return (
      <div className="rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 space-y-2">
        <div className="flex items-center justify-between gap-3">
          <div className="flex items-center gap-3 min-w-0">
            <span className="text-sm font-mono font-medium text-gray-800 truncate">{branch}</span>
            <span className="text-xs text-gray-500 shrink-0">onto</span>
            <span className="text-xs font-mono text-gray-600 shrink-0">{baseBranch}</span>
          </div>
          <div className="flex items-center gap-3 shrink-0">
            <span className="text-xs font-medium text-amber-700">
              {ahead > 0 && <span className="text-gray-500">↑{ahead} </span>}
              <span>↓{behind} behind</span>
            </span>
            <button
              onClick={() => rebaseMutation.mutate()}
              disabled={isAgentRunning || isConflictResolving}
              title={isAgentRunning ? 'Cannot rebase while agent is running' : isConflictResolving ? 'Conflict resolution in progress' : undefined}
              className="rounded-md border border-amber-300 bg-white px-3 py-1.5 text-xs font-medium text-amber-800 hover:bg-amber-50 disabled:opacity-50 transition-colors inline-flex items-center gap-1.5"
            >
              Rebase onto {baseBranch}
            </button>
          </div>
        </div>
        {isAgentRunning && (
          <p className="text-xs text-amber-500">Cannot rebase while agent is running</p>
        )}
        {isDone && (
          <p className="text-xs text-amber-600">
            This Done worktree is retained for review, traceability, diff inspection, and debugging. Archiving will remove the retained worktree.
          </p>
        )}
        {rebaseMutation.isError && (
          <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
            {rebaseMutation.error instanceof ApiError
              ? rebaseMutation.error.message
              : 'Rebase failed'}
          </div>
        )}
        {hasConflicts && !isConflictFailed && (
          <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
            <span>Conflicting files:</span>
            <ul className="mt-1 ml-3 list-disc">
              {hasConflicts.map((f) => (
                <li key={f} className="font-mono">{f}</li>
              ))}
            </ul>
          </div>
        )}
        {isConflictFailed && (
          <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
            <span>Conflict resolution failed{rebaseConflict?.error ? `: ${rebaseConflict.error}` : ''}</span>
          </div>
        )}
      </div>
    )
  }

  return (
    <div className="rounded-lg border border-gray-200 bg-white px-4 py-3">
      <div className="flex items-center justify-between gap-3">
        <div className="flex items-center gap-3 min-w-0">
          <span className="text-sm font-mono font-medium text-gray-800 truncate">{branch}</span>
          <span className="text-xs text-gray-500 shrink-0">onto</span>
          <span className="text-xs font-mono text-gray-600 shrink-0">{baseBranch}</span>
        </div>
        <div className="flex items-center gap-3 shrink-0">
          {ahead > 0 && <span className="text-xs text-gray-500">↑{ahead} ahead</span>}
          <span className="text-xs font-medium text-green-600">up to date</span>
        </div>
      </div>
      {isDone && (
        <p className="mt-2 text-xs text-gray-500">
          This Done worktree is retained for review, traceability, diff inspection, and debugging. Archiving will remove the retained worktree.
        </p>
      )}
    </div>
  )
}
