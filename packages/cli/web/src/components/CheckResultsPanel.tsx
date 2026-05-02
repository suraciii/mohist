import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import Markdown from 'react-markdown'
import { api } from '../lib/api'
import { useSendMessage } from '../hooks/useQueries'
import type { CheckResult, CheckSuiteOutput } from '../lib/types'

function parseCheckSuite(output?: Record<string, unknown>): CheckSuiteOutput | null {
  if (!output) return null
  const rawChecks = output.checks ?? output.checkResults
  const checks = Array.isArray(rawChecks)
    ? rawChecks
        .filter((c): c is Record<string, unknown> => typeof c === 'object' && c !== null)
        .map((c) => {
          const rawStatus = typeof c.status === 'string' ? c.status : 'pending'
          const normalizedStatus = rawStatus === 'pass' ? 'passed' : rawStatus
          return {
            name: typeof c.name === 'string' ? c.name : 'unknown',
            status: normalizedStatus as CheckResult['status'],
            duration: typeof c.duration === 'number' ? c.duration : undefined,
            summary: typeof c.summary === 'string' ? c.summary : undefined,
            buildLog: typeof c.buildLog === 'string' ? c.buildLog : undefined,
            reviewReport: typeof c.reviewReport === 'string' ? c.reviewReport : undefined,
            autoFixed: typeof c.autoFixed === 'boolean' ? c.autoFixed : undefined,
            verdict: typeof c.verdict === 'string' ? c.verdict : undefined,
          }
        })
    : []
  let overallResult: 'passed' | 'failed'
  if (typeof output.overallResult === 'string') {
    overallResult = output.overallResult === 'passed' || output.overallResult === 'pass' ? 'passed' : 'failed'
  } else if (checks.length > 0) {
    overallResult = checks.every(c => c.status === 'passed') ? 'passed' : 'failed'
  } else {
    overallResult = 'failed'
  }
  return { checks, overallResult }
}

const CHECK_LABELS: Record<string, string> = {
  'build-test': 'Build & Test',
  'merge-ready': 'Merge Ready',
  'ai-review': 'AI Code Review',
}

function StatusIcon({ status }: { status: CheckResult['status'] }) {
  if (status === 'passed') {
    return (
      <svg className="h-5 w-5 text-green-500 flex-shrink-0" viewBox="0 0 20 20" fill="currentColor">
        <path
          fillRule="evenodd"
          d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.857-9.809a.75.75 0 00-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 10-1.06 1.061l2.5 2.5a.75.75 0 001.137-.089l4-5.5z"
          clipRule="evenodd"
        />
      </svg>
    )
  }
  if (status === 'failed') {
    return (
      <svg className="h-5 w-5 text-red-500 flex-shrink-0" viewBox="0 0 20 20" fill="currentColor">
        <path
          fillRule="evenodd"
          d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.28 7.22a.75.75 0 00-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 101.06 1.06L10 11.06l1.72 1.72a.75.75 0 101.06-1.06L11.06 10l1.72-1.72a.75.75 0 00-1.06-1.06L10 8.94 8.28 7.22z"
          clipRule="evenodd"
        />
      </svg>
    )
  }
  if (status === 'running') {
    return (
      <svg className="h-5 w-5 text-blue-500 flex-shrink-0 animate-spin" viewBox="0 0 24 24" fill="none">
        <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
        <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
      </svg>
    )
  }
  return (
    <svg className="h-5 w-5 text-gray-300 flex-shrink-0" viewBox="0 0 20 20" fill="currentColor">
      <path
        fillRule="evenodd"
        d="M10 18a8 8 0 100-16 8 8 0 000 16zm0-2a6 6 0 100-12 6 6 0 000 12z"
        clipRule="evenodd"
      />
    </svg>
  )
}

function StatusBadge({ status }: { status: CheckResult['status'] }) {
  const styles: Record<string, string> = {
    passed: 'bg-green-100 text-green-700',
    failed: 'bg-red-100 text-red-700',
    running: 'bg-blue-100 text-blue-700',
    pending: 'bg-gray-100 text-gray-500',
  }
  return (
    <span className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${styles[status] ?? styles.pending}`}>
      {status}
    </span>
  )
}

function formatDuration(ms?: number): string {
  if (ms === undefined) return ''
  if (ms < 1000) return `${ms}ms`
  if (ms < 60000) return `${(ms / 1000).toFixed(1)}s`
  return `${Math.floor(ms / 60000)}m ${Math.floor((ms % 60000) / 1000)}s`
}

function CheckRow({ check }: { check: CheckResult }) {
  const [expanded, setExpanded] = useState(false)
  const label = CHECK_LABELS[check.name] ?? check.name

  return (
    <div className="border border-gray-200 rounded-md overflow-hidden">
      <button
        onClick={() => setExpanded(!expanded)}
        className="w-full flex items-center gap-3 px-3 py-2.5 text-left hover:bg-gray-50 transition-colors"
      >
        <StatusIcon status={check.status} />
        <span className="text-sm font-medium text-gray-900 flex-1">{label}</span>
        <StatusBadge status={check.status} />
        {check.duration !== undefined && (
          <span className="text-xs text-gray-400">{formatDuration(check.duration)}</span>
        )}
        <svg
          className={`h-4 w-4 text-gray-400 transition-transform flex-shrink-0 ${expanded ? 'rotate-180' : ''}`}
          viewBox="0 0 20 20"
          fill="currentColor"
        >
          <path
            fillRule="evenodd"
            d="M5.23 7.21a.75.75 0 011.06.02L10 10.94l3.71-3.71a.75.75 0 111.06 1.06l-4.24 4.24a.75.75 0 01-1.06 0L5.23 8.27a.75.75 0 01.02-1.06z"
            clipRule="evenodd"
          />
        </svg>
      </button>

      {expanded && (
        <div className="px-3 pb-3 border-t border-gray-100 bg-gray-50">
          {check.summary && (
            <p className="text-sm text-gray-600 mt-2">{check.summary}</p>
          )}
          {check.autoFixed && (
            <span className="inline-flex items-center gap-1 mt-2 rounded bg-amber-100 px-2 py-0.5 text-xs font-medium text-amber-700">
              Auto-fixed
            </span>
          )}
          {check.buildLog && (
            <div className="mt-2">
              <pre className="text-xs font-mono bg-white border border-gray-200 rounded p-2 max-h-48 overflow-y-auto whitespace-pre-wrap">
                {check.buildLog}
              </pre>
            </div>
          )}
          {check.reviewReport && (
            <div className="mt-2 prose prose-sm max-w-none bg-white border border-gray-200 rounded p-3 max-h-64 overflow-y-auto">
              <Markdown>{check.reviewReport}</Markdown>
            </div>
          )}
        </div>
      )}
    </div>
  )
}

interface CheckResultsPanelProps {
  output?: Record<string, unknown>
  issueNumber: number
  onViewFiles: () => void
}

export function CheckResultsPanel({ output, issueNumber, onViewFiles }: CheckResultsPanelProps) {
  const suite = parseCheckSuite(output)
  const queryClient = useQueryClient()
  const sendMessageMutation = useSendMessage(issueNumber)

  const [instructionsExpanded, setInstructionsExpanded] = useState(false)
  const [instructionsText, setInstructionsText] = useState('')
  const [actionError, setActionError] = useState<string | null>(null)

  const approveMutation = useMutation({
    mutationFn: () => api.approveIssue(issueNumber),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      setActionError(null)
    },
    onError: (err: Error) => {
      setActionError(err.message)
    },
  })

  const rejectMutation = useMutation({
    mutationFn: () =>
      api.rejectIssue(issueNumber, { message: undefined }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      setActionError(null)
    },
    onError: (err: Error) => {
      setActionError(err.message)
    },
  })

  const buildTestCheck = suite?.checks.find((c) => c.name === 'build-test')
  const aiReviewCheck = suite?.checks.find((c) => c.name === 'ai-review')
  const allPassed = suite?.overallResult === 'passed'
  const buildTestFailed = buildTestCheck?.status === 'failed'
  const aiReviewFailed = aiReviewCheck?.status === 'failed'

  const handleAddInstructions = () => {
    if (!instructionsText.trim()) return
    setActionError(null)
    sendMessageMutation.mutate(instructionsText.trim(), {
      onSuccess: () => {
        setInstructionsText('')
        setInstructionsExpanded(false)
      },
      onError: (err: Error) => {
        setActionError(err.message)
      },
    })
  }

  return (
    <div className="rounded-lg border border-gray-200 bg-white p-4 space-y-4">
      <h2 className="text-sm font-semibold text-gray-700">Check Results</h2>

      {suite ? (
        <div className="space-y-2">
          {suite.checks.map((check) => (
            <CheckRow key={check.name} check={check} />
          ))}
        </div>
      ) : (
        <p className="text-sm text-gray-400">No check results available.</p>
      )}

      {actionError && (
        <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
          {actionError}
        </div>
      )}

      {(allPassed || !suite || (suite && suite.checks.length === 0)) && (
        <div className="space-y-3 pt-2 border-t border-gray-100">
          <button
            onClick={() => {
              setActionError(null)
              approveMutation.mutate()
            }}
            disabled={approveMutation.isPending}
            className="w-full rounded-md bg-green-600 px-4 py-2.5 text-sm font-medium text-white hover:bg-green-700 disabled:opacity-50 transition-colors"
          >
            {approveMutation.isPending ? 'Approving...' : 'Approve & Merge'}
          </button>
          <div className="flex items-center gap-4">
            <button
              onClick={onViewFiles}
              className="text-sm text-gray-500 hover:text-gray-700 transition-colors"
            >
              View Files &rarr;
            </button>
          </div>
        </div>
      )}

      {buildTestFailed && (
        <div className="space-y-3 pt-2 border-t border-gray-100">
          <button
            onClick={() => {
              setActionError(null)
              rejectMutation.mutate()
            }}
            disabled={rejectMutation.isPending}
            className="w-full rounded-md bg-red-600 px-4 py-2.5 text-sm font-medium text-white hover:bg-red-700 disabled:opacity-50 transition-colors"
          >
            {rejectMutation.isPending ? 'Sending back...' : 'Back to Build'}
          </button>
        </div>
      )}

      {aiReviewFailed && !buildTestFailed && (
        <div className="space-y-3 pt-2 border-t border-gray-100">
          <button
            onClick={() => {
              setActionError(null)
              rejectMutation.mutate()
            }}
            disabled={rejectMutation.isPending}
            className="w-full rounded-md bg-red-600 px-4 py-2.5 text-sm font-medium text-white hover:bg-red-700 disabled:opacity-50 transition-colors"
          >
            {rejectMutation.isPending ? 'Sending back...' : 'Back to Build'}
          </button>

          <div>
            <button
              onClick={() => setInstructionsExpanded(!instructionsExpanded)}
              className="text-sm text-gray-600 hover:text-gray-800 transition-colors"
            >
              {instructionsExpanded ? '▾' : '▸'} Add Instructions...
            </button>
            {instructionsExpanded && (
              <div className="mt-2 space-y-2">
                <textarea
                  value={instructionsText}
                  onChange={(e) => setInstructionsText(e.target.value)}
                  placeholder="Add your instructions for the agent..."
                  rows={3}
                  className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 resize-none"
                />
                <button
                  onClick={handleAddInstructions}
                  disabled={!instructionsText.trim() || sendMessageMutation.isPending}
                  className="rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50 transition-colors"
                >
                  {sendMessageMutation.isPending ? 'Sending...' : 'Send with instructions'}
                </button>
              </div>
            )}
          </div>

          <button
            onClick={() => {
              setActionError(null)
              approveMutation.mutate()
            }}
            disabled={approveMutation.isPending}
            className="w-full rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-600 hover:bg-gray-50 disabled:opacity-50 transition-colors"
          >
            {approveMutation.isPending ? 'Approving...' : 'Force Approve'}
          </button>
        </div>
      )}
    </div>
  )
}
