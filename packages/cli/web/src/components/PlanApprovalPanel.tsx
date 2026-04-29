import { useState } from 'react'
import Markdown from 'react-markdown'
import type { ApprovalArtifact, ApprovalOutput } from '../lib/types'
import { api } from '../lib/api'
import { useMutation, useQueryClient } from '@tanstack/react-query'

interface PlanApprovalPanelProps {
  issueNumber: number
  output: ApprovalOutput
}

function ArtifactItem({ artifact }: { artifact: ApprovalArtifact }) {
  const [expanded, setExpanded] = useState(false)
  const hasContent = artifact.content != null && artifact.content.trim().length > 0

  return (
    <div className="rounded-md border border-gray-200 bg-white">
      <button
        onClick={() => setExpanded((prev) => !prev)}
        className="w-full flex items-center gap-2 px-3 py-2 text-sm text-left hover:bg-gray-50 transition-colors"
      >
        <svg
          className={`h-4 w-4 text-gray-400 transition-transform flex-shrink-0 ${expanded ? 'rotate-90' : ''}`}
          viewBox="0 0 20 20"
          fill="currentColor"
        >
          <path
            fillRule="evenodd"
            d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z"
            clipRule="evenodd"
          />
        </svg>
        <svg className="h-4 w-4 text-gray-400 flex-shrink-0" viewBox="0 0 20 20" fill="currentColor">
          <path d="M3 3.5A1.5 1.5 0 014.5 2h6.879a1.5 1.5 0 011.06.44l4.122 4.12A1.5 1.5 0 0117 7.622V16.5a1.5 1.5 0 01-1.5 1.5h-11A1.5 1.5 0 013 16.5v-13z" />
        </svg>
        <span className="text-gray-700 font-medium truncate">{artifact.name}</span>
        <span className="ml-auto text-xs text-gray-400 font-mono truncate">{artifact.path}</span>
      </button>
      {expanded && hasContent && (
        <div className="border-t border-gray-100 p-3 prose prose-sm max-w-none prose-gray">
          <Markdown>{artifact.content}</Markdown>
        </div>
      )}
    </div>
  )
}

function SelfReviewNotes({ notes }: { notes: string }) {
  const [expanded, setExpanded] = useState(false)

  return (
    <div className="mt-3">
      <button
        onClick={() => setExpanded((prev) => !prev)}
        className="w-full rounded-md border border-gray-200 bg-white px-3 py-2 text-sm font-medium text-gray-600 hover:bg-gray-50 transition-colors flex items-center justify-center gap-2"
      >
        <svg
          className={`h-4 w-4 text-gray-400 transition-transform ${expanded ? 'rotate-90' : ''}`}
          viewBox="0 0 20 20"
          fill="currentColor"
        >
          <path
            fillRule="evenodd"
            d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z"
            clipRule="evenodd"
          />
        </svg>
        Self-Review Notes
      </button>
      {expanded && (
        <div className="mt-2 rounded-md border border-gray-200 bg-white p-4 prose prose-sm max-w-none prose-gray">
          <Markdown>{notes}</Markdown>
        </div>
      )}
    </div>
  )
}

export function PlanApprovalPanel({ issueNumber, output }: PlanApprovalPanelProps) {
  const queryClient = useQueryClient()
  const [notesText, setNotesText] = useState('')

  const approveMutation = useMutation({
    mutationFn: () => api.approveIssue(issueNumber),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
    },
  })

  const sendMessageMutation = useMutation({
    mutationFn: (message: string) => api.sendMessage(issueNumber, message),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      setNotesText('')
    },
  })

  const artifacts = output.artifacts && output.artifacts.length > 0
    ? output.artifacts
    : null

  const selfReviewNotes = output.selfReviewNotes?.trim() || null

  const handleSendNotes = () => {
    if (!notesText.trim()) return
    sendMessageMutation.mutate(`User feedback on plan:\n${notesText}`)
  }

  return (
    <div className="space-y-4">
      {artifacts ? (
        <div className="space-y-2">
          <h3 className="text-sm font-semibold text-gray-700">Design Artifacts</h3>
          {artifacts.map((artifact) => (
            <ArtifactItem key={artifact.path} artifact={artifact} />
          ))}
        </div>
      ) : (
        <div className="space-y-3">
          {selfReviewNotes && (
            <SelfReviewNotes notes={selfReviewNotes} />
          )}
          <p className="text-xs text-gray-400 italic text-center">
            Design artifacts not available for preview
          </p>
        </div>
      )}

      {artifacts && selfReviewNotes && (
        <SelfReviewNotes notes={selfReviewNotes} />
      )}

      <div className="space-y-2 pt-2 border-t border-gray-200">
        <button
          onClick={() => approveMutation.mutate()}
          disabled={approveMutation.isPending}
          className="w-full rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50 transition-colors"
        >
          {approveMutation.isPending ? 'Approving...' : 'Approve & Build'}
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
            onClick={handleSendNotes}
            disabled={!notesText.trim() || sendMessageMutation.isPending}
            className="w-full rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50 transition-colors"
          >
            {sendMessageMutation.isPending ? 'Sending...' : 'Send back with notes'}
          </button>
        </div>
      </div>

      {approveMutation.error && (
        <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
          {approveMutation.error.message}
        </div>
      )}
      {sendMessageMutation.error && (
        <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
          {sendMessageMutation.error.message}
        </div>
      )}
    </div>
  )
}
