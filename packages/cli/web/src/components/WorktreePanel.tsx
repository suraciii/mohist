import { useState, useEffect } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { api, ApiError } from '../lib/api'
import { useWorktreeStatus } from '../hooks/useQueries'
import { onRebaseEvent } from '../lib/rebase-events'

interface WorktreePanelProps {
  issueNumber: number
  isAgentRunning: boolean
}

type RebaseResult = {
  type: 'success' | 'info' | 'error' | 'queued'
  message: string
  conflicts?: string[]
}

type RebaseStep = 'fetching' | 'checking' | 'rebasing' | 'verifying'

const STEP_LABELS: Record<RebaseStep, string> = {
  fetching: 'Fetching latest...',
  checking: 'Checking fast-forward...',
  rebasing: 'Rebasing onto master...',
  verifying: 'Verifying build...',
}

export function WorktreePanel({ issueNumber, isAgentRunning }: WorktreePanelProps) {
  const queryClient = useQueryClient()
  const { data: status, isLoading } = useWorktreeStatus(issueNumber)
  const [rebaseResult, setRebaseResult] = useState<RebaseResult | null>(null)
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
        setRebaseResult({ type: 'error', message: 'Rebase aborted due to conflicts', conflicts: event.conflicts })
      }
    })
  }, [issueNumber])

  const rebaseMutation = useMutation({
    mutationFn: () => api.rebaseIssue(issueNumber),
    onSuccess: (data) => {
      if (data.conflicts && data.conflicts.length > 0) {
        setRebaseResult({ type: 'error', message: 'Rebase aborted due to conflicts', conflicts: data.conflicts })
      } else if (data.rebased) {
        setRebaseResult({ type: 'success', message: 'Rebase successful' })
      } else {
        setRebaseResult({ type: 'info', message: 'Already up to date' })
      }
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
      queryClient.invalidateQueries({ queryKey: ['worktree-status', issueNumber] })
    },
    onError: (error: Error) => {
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

  if (!status?.exists) return null
  if (isLoading) return null

  const isBehind = status.behind > 0
  const isAhead = status.ahead > 0
  const isUpToDate = !isBehind && !isAhead
  const isRebasing = rebaseMutation.isPending || status.isRebaseInProgress

  return (
    <div className="rounded-lg border border-gray-200 bg-white p-4">
      <h2 className="text-sm font-semibold text-gray-700 mb-3">Worktree</h2>

      {status.branch && (
        <div className="text-xs text-gray-500 mb-2 font-mono">{status.branch}</div>
      )}

      <div className="mb-3">
        {isUpToDate && (
          <span className="inline-flex items-center gap-1.5 text-xs text-green-700">
            <svg className="h-3.5 w-3.5" viewBox="0 0 20 20" fill="currentColor">
              <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.857-9.809a.75.75 0 00-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 10-1.06 1.061l2.5 2.5a.75.75 0 001.137-.089l4-5.5z" clipRule="evenodd" />
            </svg>
            Up to date
          </span>
        )}
        {isBehind && !isAhead && (
          <span className="inline-flex items-center gap-1.5 text-xs text-amber-700">
            <svg className="h-3.5 w-3.5" viewBox="0 0 20 20" fill="currentColor">
              <path fillRule="evenodd" d="M8.485 2.495c.673-1.167 2.357-1.167 3.03 0l6.28 10.875c.673 1.167-.168 2.625-1.516 2.625H3.72c-1.347 0-2.189-1.458-1.515-2.625L8.485 2.495zM10 6a.75.75 0 01.75.75v3.5a.75.75 0 01-1.5 0v-3.5A.75.75 0 0110 6zm0 9a1 1 0 100-2 1 1 0 000 2z" clipRule="evenodd" />
            </svg>
            {status.behind} {status.behind === 1 ? 'commit' : 'commits'} behind master
          </span>
        )}
        {isAhead && !isBehind && (
          <span className="inline-flex items-center gap-1.5 text-xs text-gray-600">
            {status.ahead} {status.ahead === 1 ? 'commit' : 'commits'} ahead of master
          </span>
        )}
        {isAhead && isBehind && (
          <span className="inline-flex items-center gap-1.5 text-xs text-amber-700">
            <svg className="h-3.5 w-3.5" viewBox="0 0 20 20" fill="currentColor">
              <path fillRule="evenodd" d="M8.485 2.495c.673-1.167 2.357-1.167 3.03 0l6.28 10.875c.673 1.167-.168 2.625-1.516 2.625H3.72c-1.347 0-2.189-1.458-1.515-2.625L8.485 2.495zM10 6a.75.75 0 01.75.75v3.5a.75.75 0 01-1.5 0v-3.5A.75.75 0 0110 6zm0 9a1 1 0 100-2 1 1 0 000 2z" clipRule="evenodd" />
            </svg>
            {status.ahead} ahead, {status.behind} behind master
          </span>
        )}
      </div>

      {isRebasing && rebaseStep && (
        <div className="mb-3 flex items-center gap-2 text-xs text-blue-700">
          <svg className="h-3.5 w-3.5 animate-spin" viewBox="0 0 24 24" fill="none">
            <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
            <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
          </svg>
          {STEP_LABELS[rebaseStep]}
        </div>
      )}

      {rebaseResult && (
        <div className={`mb-3 rounded-md px-3 py-2 text-xs ${
          rebaseResult.type === 'success' ? 'bg-green-50 text-green-700' :
          rebaseResult.type === 'error' ? 'bg-red-50 text-red-700' :
          'bg-blue-50 text-blue-700'
        }`}>
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

      <button
        onClick={() => { setRebaseResult(null); rebaseMutation.mutate() }}
        disabled={isRebasing}
        className={`w-full rounded-md border px-3 py-2 text-sm font-medium transition-colors inline-flex items-center justify-center gap-2 disabled:opacity-50 ${
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
        ) : isAgentRunning ? (
          'Rebase after completion'
        ) : (
          'Rebase onto master'
        )}
      </button>
    </div>
  )
}
