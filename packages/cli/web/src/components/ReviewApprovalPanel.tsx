import { useState, useEffect, useRef } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import type { ApprovalOutput } from '../lib/types'
import { api } from '../lib/api'
import { ReviewSummary } from './ReviewSummary'

interface ReviewApprovalPanelProps {
  issueNumber: number
  output: ApprovalOutput
  onViewCodeChanges: () => void
}

export function ReviewApprovalPanel({ issueNumber, output, onViewCodeChanges }: ReviewApprovalPanelProps) {
  const queryClient = useQueryClient()
  const [instructionsText, setInstructionsText] = useState('')
  const [notesText, setNotesText] = useState('')
  const [forceConfirming, setForceConfirming] = useState(false)
  const forceConfirmTimer = useRef<ReturnType<typeof setTimeout> | null>(null)

  useEffect(() => {
    if (!forceConfirming) return
    forceConfirmTimer.current = setTimeout(() => setForceConfirming(false), 3000)
    return () => {
      if (forceConfirmTimer.current) clearTimeout(forceConfirmTimer.current)
    }
  }, [forceConfirming])

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: ['issues'] })
    queryClient.invalidateQueries({ queryKey: ['agent-status'] })
  }

  const approveMutation = useMutation({
    mutationFn: () => api.approveIssue(issueNumber),
    onSuccess: invalidate,
  })

  const sendMessageMutation = useMutation({
    mutationFn: (message: string) => api.sendMessage(issueNumber, message),
    onSuccess: () => {
      invalidate()
      setInstructionsText('')
      setNotesText('')
    },
  })

  const verdict = output.verdict ?? null

  const handleSendBackForFixes = () => {
    const report = output.reviewReport?.trim()
    if (report) {
      sendMessageMutation.mutate(`Please fix the issues identified in the review:\n\n${report}`)
    } else {
      sendMessageMutation.mutate('The review identified issues that need to be fixed. Please address them and re-submit.')
    }
  }

  const handleSendWithInstructions = () => {
    if (!instructionsText.trim()) return
    const reportRef = output.reviewReport?.trim()
      ? '\n\n(See the full review report above for details.)'
      : ''
    sendMessageMutation.mutate(`${instructionsText.trim()}${reportRef}`)
  }

  const handleSendWithNotes = () => {
    if (!notesText.trim()) return
    sendMessageMutation.mutate(notesText.trim())
  }

  const handleForceApprove = () => {
    if (!forceConfirming) {
      setForceConfirming(true)
      return
    }
    approveMutation.mutate()
  }

  return (
    <div className="space-y-4">
      <ReviewSummary output={output} />

      <button
        onClick={onViewCodeChanges}
        className="w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 transition-colors inline-flex items-center justify-center gap-2"
      >
        <svg className="h-4 w-4 text-gray-400" viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M4 4a2 2 0 012-2h4.586A2 2 0 0112 2.586L15.414 6A2 2 0 0116 7.414V16a2 2 0 01-2 2H6a2 2 0 01-2-2V4z" clipRule="evenodd" />
        </svg>
        View Code Changes
      </button>

      {verdict === 'PASS' && (
        <div className="space-y-2 pt-2 border-t border-gray-200">
          <button
            onClick={() => approveMutation.mutate()}
            disabled={approveMutation.isPending}
            className="w-full rounded-md bg-green-600 px-3 py-2 text-sm font-medium text-white hover:bg-green-700 disabled:opacity-50 transition-colors"
          >
            {approveMutation.isPending ? 'Approving...' : 'Approve & Done'}
          </button>
        </div>
      )}

      {verdict === 'FAIL' && (
        <div className="space-y-2 pt-2 border-t border-gray-200">
          <button
            onClick={handleSendBackForFixes}
            disabled={sendMessageMutation.isPending}
            className="w-full rounded-md bg-red-600 px-3 py-2 text-sm font-medium text-white hover:bg-red-700 disabled:opacity-50 transition-colors inline-flex items-center justify-center gap-2"
          >
            {sendMessageMutation.isPending ? (
              <>
                <svg className="h-4 w-4 animate-spin" viewBox="0 0 24 24" fill="none">
                  <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                  <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
                </svg>
                Sending...
              </>
            ) : (
              'Send back for fixes'
            )}
          </button>

          <div className="rounded-md border border-gray-200 bg-white p-3 space-y-2">
            <textarea
              value={instructionsText}
              onChange={(e) => setInstructionsText(e.target.value)}
              placeholder="Add instructions for fixes..."
              rows={3}
              className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 resize-none"
            />
            <button
              onClick={handleSendWithInstructions}
              disabled={!instructionsText.trim() || sendMessageMutation.isPending}
              className="w-full rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50 transition-colors"
            >
              {sendMessageMutation.isPending ? 'Sending...' : 'Send back with instructions'}
            </button>
          </div>

          <button
            onClick={handleForceApprove}
            disabled={approveMutation.isPending}
            className={`w-full rounded-md px-3 py-2 text-sm font-medium transition-colors disabled:opacity-50 ${
              forceConfirming
                ? 'bg-orange-600 text-white hover:bg-orange-700'
                : 'border border-orange-300 bg-white text-orange-600 hover:bg-orange-50'
            }`}
          >
            {approveMutation.isPending
              ? 'Approving...'
              : forceConfirming
                ? 'Confirm Force Approve'
                : 'Force Approve'}
          </button>
          {forceConfirming && (
            <p className="text-xs text-orange-500 text-center">
              Click again within 3s to confirm force approval
            </p>
          )}
        </div>
      )}

      {verdict !== 'PASS' && verdict !== 'FAIL' && (
        <div className="space-y-2 pt-2 border-t border-gray-200">
          <button
            onClick={() => approveMutation.mutate()}
            disabled={approveMutation.isPending}
            className="w-full rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50 transition-colors"
          >
            {approveMutation.isPending ? 'Approving...' : 'Approve & Continue'}
          </button>

          <div className="rounded-md border border-gray-200 bg-white p-3 space-y-2">
            <textarea
              value={notesText}
              onChange={(e) => setNotesText(e.target.value)}
              placeholder="Send back with notes..."
              rows={3}
              className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 resize-none"
            />
            <button
              onClick={handleSendWithNotes}
              disabled={!notesText.trim() || sendMessageMutation.isPending}
              className="w-full rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50 transition-colors"
            >
              {sendMessageMutation.isPending ? 'Sending...' : 'Send back with notes'}
            </button>
          </div>
        </div>
      )}

      {(approveMutation.error || sendMessageMutation.error) && (
        <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
          {approveMutation.error?.message || sendMessageMutation.error?.message}
        </div>
      )}
    </div>
  )
}
