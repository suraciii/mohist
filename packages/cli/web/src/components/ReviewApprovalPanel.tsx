import { useState, useCallback } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from '../lib/api'
import { useSendMessage } from '../hooks/useQueries'
import { ReviewSummary, parseReviewOutput } from './ReviewSummary'
import type { ReviewOutput } from './ReviewSummary'
import { FullReportModal } from './ReviewReportModal'
import { useLiveTask } from '../hooks/useSSE'

function classifyResult(result?: string): 'PASS' | 'FAIL' | 'UNKNOWN' {
  if (!result) return 'UNKNOWN'
  const upper = result.toUpperCase()
  if (upper === 'PASS') return 'PASS'
  if (upper === 'FAIL') return 'FAIL'
  return 'UNKNOWN'
}

function buildIssueSummary(review: ReviewOutput): string {
  const failDims = (review.dimensions ?? []).filter(
    (d) => d.status.toUpperCase() === 'FAIL',
  )

  if (failDims.length > 0) {
    const parts = failDims.map((dim) => {
      const issues = dim.issues && dim.issues.length > 0
        ? dim.issues.map((i) => `- ${i}`).join('\n')
        : '- Issues identified in this dimension'
      return `### ${dim.name}\n${issues}`
    })
    return `Please fix the following issues:\n\n${parts.join('\n\n')}`
  }

  if (review.reviewReport) {
    const fixMatch = review.reviewReport.match(
      /^## Fix Suggestions\s*\n([\s\S]*?)(?=^## |\s*$)/m,
    )
    if (fixMatch && fixMatch[1]?.trim()) {
      return `Please fix the following issues:\n\n${fixMatch[1].trim()}`
    }
    return `Please fix the following issues:\n\n${review.reviewReport}`
  }

  return 'The review found issues that need to be addressed. Please review and fix all problems.'
}

function buildInstructionMessage(
  instructions: string,
  review: ReviewOutput,
): string {
  const failDims = (review.dimensions ?? []).filter(
    (d) => d.status.toUpperCase() === 'FAIL',
  )

  let message = instructions

  if (failDims.length > 0) {
    const parts = failDims.map((dim) => {
      const issues = dim.issues && dim.issues.length > 0
        ? dim.issues.map((i) => `- ${i}`).join('\n')
        : ''
      return issues ? `### ${dim.name}\n${issues}` : ''
    }).filter(Boolean)
    if (parts.length > 0) {
      message += `\n\n---\n\nReference — issues found:\n\n${parts.join('\n\n')}`
    }
  }

  return message
}

interface ReviewApprovalPanelProps {
  output?: Record<string, unknown>
  issueNumber: number
  onViewFiles: () => void
  rebaseResult: {
    type: 'success' | 'info' | 'error'
    message: string
    conflicts?: string[]
  } | null
  onRebase: () => void
  rebasePending: boolean
}

export function ReviewApprovalPanel({
  output,
  issueNumber,
  onViewFiles,
  rebaseResult,
  onRebase,
  rebasePending,
}: ReviewApprovalPanelProps) {
  const review = parseReviewOutput(output)
  const classified = classifyResult(review.result)
  const queryClient = useQueryClient()
  const { rebaseConflict } = useLiveTask()

  const [reportModalOpen, setReportModalOpen] = useState(false)
  const [instructionsExpanded, setInstructionsExpanded] = useState(false)
  const [instructionsText, setInstructionsText] = useState('')
  const [notesExpanded, setNotesExpanded] = useState(false)
  const [notesText, setNotesText] = useState('')
  const [actionError, setActionError] = useState<string | null>(null)

  const approveMutation = useMutation({
    mutationFn: () => api.approveIssue(issueNumber),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
    },
    onError: (err: Error) => {
      setActionError(err.message)
    },
  })

  const sendMessageMutation = useSendMessage(issueNumber)

  const handleSendBackForFixes = useCallback(() => {
    setActionError(null)
    const message = buildIssueSummary(review)
    sendMessageMutation.mutate(message, {
      onError: (err: Error) => {
        setActionError(err.message)
      },
    })
  }, [review, sendMessageMutation])

  const handleSendWithInstructions = useCallback(() => {
    if (!instructionsText.trim()) return
    setActionError(null)
    const message = buildInstructionMessage(instructionsText.trim(), review)
    sendMessageMutation.mutate(message, {
      onSuccess: () => {
        setInstructionsText('')
        setInstructionsExpanded(false)
      },
      onError: (err: Error) => {
        setActionError(err.message)
      },
    })
  }, [instructionsText, review, sendMessageMutation])

  const handleSendBackWithNotes = useCallback(() => {
    if (!notesText.trim()) return
    setActionError(null)
    sendMessageMutation.mutate(notesText.trim(), {
      onSuccess: () => {
        setNotesText('')
        setNotesExpanded(false)
      },
      onError: (err: Error) => {
        setActionError(err.message)
      },
    })
  }, [notesText, sendMessageMutation])

  const handleApproveAnyway = useCallback(() => {
    setActionError(null)
    approveMutation.mutate()
  }, [approveMutation])

  const isSending = sendMessageMutation.isPending

  return (
    <div>
      {reportModalOpen && (
        <FullReportModal
          review={review}
          classified={classified}
          onClose={() => setReportModalOpen(false)}
        />
      )}

      <div className="rounded-lg border border-gray-200 bg-white p-4 space-y-4">
        <ReviewSummary output={output} />

        {rebaseResult && (
          <div
            className={`rounded-md px-3 py-2 text-xs ${
              rebaseResult.type === 'success'
                ? 'bg-green-50 text-green-700'
                : rebaseResult.type === 'info'
                  ? 'bg-blue-50 text-blue-700'
                  : 'bg-red-50 text-red-600'
            }`}
          >
            {rebaseResult.message}
            {rebaseResult.conflicts && rebaseResult.conflicts.length > 0 && (
              <ul className="mt-1 ml-3 list-disc">
                {rebaseResult.conflicts.map((f) => (
                  <li key={f} className="font-mono text-xs">
                    {f}
                  </li>
                ))}
              </ul>
            )}
          </div>
        )}

        {rebaseConflict?.issueNumber === issueNumber && rebaseConflict.status === 'resolving' && (
          <div className="rounded-md bg-blue-50 px-3 py-2 text-xs text-blue-700 flex items-center gap-2">
            <svg className="h-3.5 w-3.5 animate-spin" viewBox="0 0 24 24" fill="none">
              <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
              <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
            </svg>
            Agent is resolving conflicts...
          </div>
        )}

        {rebaseConflict?.issueNumber === issueNumber && rebaseConflict.status === 'failed' && (
          <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
            Conflict resolution failed{rebaseConflict.error ? `: ${rebaseConflict.error}` : ''}
          </div>
        )}

        <div className="pt-2 border-t border-gray-100">
          <button
            onClick={onRebase}
            disabled={rebasePending || (rebaseConflict?.issueNumber === issueNumber && rebaseConflict.status === 'resolving')}
            className="w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50 transition-colors inline-flex items-center justify-center gap-2"
          >
            {rebasePending && (
              <svg className="h-4 w-4 animate-spin" viewBox="0 0 24 24" fill="none">
                <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
              </svg>
            )}
            {rebasePending ? 'Rebasing...' : 'Rebase onto master'}
          </button>
        </div>

        {actionError && (
          <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
            {actionError}
          </div>
        )}

        {classified === 'PASS' && (
          <div className="space-y-3">
            <button
              onClick={() => {
                setActionError(null)
                approveMutation.mutate()
              }}
              disabled={approveMutation.isPending}
              className="w-full rounded-md bg-green-600 px-4 py-2.5 text-sm font-medium text-white hover:bg-green-700 disabled:opacity-50 transition-colors"
            >
              {approveMutation.isPending ? 'Approving...' : 'Approve & Continue'}
            </button>
            <div className="flex items-center gap-4">
              <button
                onClick={() => setReportModalOpen(true)}
                className="text-sm text-gray-500 hover:text-gray-700 transition-colors"
              >
                View Report &rarr;
              </button>
              <button
                onClick={onViewFiles}
                className="text-sm text-gray-500 hover:text-gray-700 transition-colors"
              >
                View Files &rarr;
              </button>
            </div>
          </div>
        )}

        {classified === 'FAIL' && (
          <div className="space-y-3">
            <button
              onClick={handleSendBackForFixes}
              disabled={isSending}
              className="w-full rounded-md bg-red-600 px-4 py-2.5 text-sm font-medium text-white hover:bg-red-700 disabled:opacity-50 transition-colors inline-flex items-center justify-center gap-2"
            >
              {isSending && (
                <svg className="h-4 w-4 animate-spin" viewBox="0 0 24 24" fill="none">
                  <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                  <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
                </svg>
              )}
              {isSending ? 'Sending...' : 'Send back for fixes'}
            </button>

            <div>
              <button
                onClick={() => setInstructionsExpanded(!instructionsExpanded)}
                className="text-sm text-gray-600 hover:text-gray-800 transition-colors"
              >
                {instructionsExpanded ? '▾' : '▸'} Add instructions...
              </button>
              {instructionsExpanded && (
                <div className="mt-2 space-y-2">
                  <textarea
                    value={instructionsText}
                    onChange={(e) => setInstructionsText(e.target.value)}
                    placeholder="Add your instructions for the fix..."
                    rows={3}
                    className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 resize-none"
                  />
                  <button
                    onClick={handleSendWithInstructions}
                    disabled={!instructionsText.trim() || isSending}
                    className="rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50 transition-colors"
                  >
                    {isSending ? 'Sending...' : 'Send with instructions'}
                  </button>
                </div>
              )}
            </div>

            <button
              onClick={handleApproveAnyway}
              disabled={approveMutation.isPending}
              className="w-full rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-600 hover:bg-gray-50 disabled:opacity-50 transition-colors"
            >
              {approveMutation.isPending ? 'Approving...' : 'Approve anyway'}
            </button>

            <div className="flex items-center gap-4 pt-1">
              <button
                onClick={() => setReportModalOpen(true)}
                className="text-sm text-gray-500 hover:text-gray-700 transition-colors"
              >
                View Report &rarr;
              </button>
              <button
                onClick={onViewFiles}
                className="text-sm text-gray-500 hover:text-gray-700 transition-colors"
              >
                View Files &rarr;
              </button>
            </div>
          </div>
        )}

        {classified === 'UNKNOWN' && (
          <div className="space-y-3">
            <button
              onClick={() => {
                setActionError(null)
                approveMutation.mutate()
              }}
              disabled={approveMutation.isPending}
              className="w-full rounded-md bg-blue-600 px-4 py-2.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50 transition-colors"
            >
              {approveMutation.isPending ? 'Approving...' : 'Approve & Continue'}
            </button>

            <div>
              <button
                onClick={() => setNotesExpanded(!notesExpanded)}
                className="text-sm text-gray-600 hover:text-gray-800 transition-colors"
              >
                {notesExpanded ? '▾' : '▸'} Send back with notes...
              </button>
              {notesExpanded && (
                <div className="mt-2 space-y-2">
                  <textarea
                    value={notesText}
                    onChange={(e) => setNotesText(e.target.value)}
                    placeholder="Describe what needs to be changed..."
                    rows={3}
                    className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 resize-none"
                  />
                  <button
                    onClick={handleSendBackWithNotes}
                    disabled={!notesText.trim() || isSending}
                    className="rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50 transition-colors"
                  >
                    {isSending ? 'Sending...' : 'Send'}
                  </button>
                </div>
              )}
            </div>

            <div className="flex items-center gap-4 pt-1">
              <button
                onClick={() => setReportModalOpen(true)}
                className="text-sm text-gray-500 hover:text-gray-700 transition-colors"
              >
                View Report &rarr;
              </button>
              <button
                onClick={onViewFiles}
                className="text-sm text-gray-500 hover:text-gray-700 transition-colors"
              >
                View Files &rarr;
              </button>
            </div>
          </div>
        )}
      </div>
    </div>
  )
}
