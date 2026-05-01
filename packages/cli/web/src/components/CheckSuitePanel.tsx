import { useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from '../lib/api'
import { useCheckSuiteProgress } from '../hooks/useCheckSuiteProgress'
import type { CheckSuite, CheckSuiteChecks, CheckState } from '../lib/types'

const CHECK_LABELS: Record<string, string> = {
  'build-test': 'Build & Test',
  'ai-review': 'AI Code Review',
}

const CHECK_ORDER = ['build-test', 'ai-review'] as const

function StatusIcon({ status }: { status: CheckState['status'] }) {
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

function StatusBadge({ status }: { status: CheckState['status'] }) {
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

function CheckRow({ name, state }: { name: string; state: CheckState }) {
  const label = CHECK_LABELS[name] ?? name
  return (
    <div className="flex items-center gap-3 px-3 py-2.5 rounded-md border border-gray-200">
      <StatusIcon status={state.status} />
      <span className="text-sm font-medium text-gray-900 flex-1">{label}</span>
      <StatusBadge status={state.status} />
    </div>
  )
}

interface CheckSuitePanelProps {
  issueNumber: number
  checkSuite: CheckSuite | null
}

export function CheckSuitePanel({ issueNumber, checkSuite }: CheckSuitePanelProps) {
  const queryClient = useQueryClient()
  useCheckSuiteProgress(issueNumber)

  const approveMutation = useMutation({
    mutationFn: () => api.approveIssue(issueNumber),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
    },
  })

  if (!checkSuite) return null

  const checks: CheckSuiteChecks = checkSuite.checks
  const isAwaitingApproval = checkSuite.status === 'awaiting-approval'

  return (
    <div className="rounded-lg border border-gray-200 bg-white p-4 space-y-3">
      <div className="flex items-center justify-between">
        <h2 className="text-sm font-semibold text-gray-700">Check Suite</h2>
        <span className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${
          checkSuite.status === 'running' ? 'bg-blue-100 text-blue-700' :
          checkSuite.status === 'awaiting-approval' ? 'bg-green-100 text-green-700' :
          checkSuite.status === 'passed' ? 'bg-green-100 text-green-700' :
          'bg-red-100 text-red-700'
        }`}>
          {checkSuite.status}
        </span>
      </div>

      <div className="space-y-2">
        {CHECK_ORDER.map((name) => (
          <CheckRow key={name} name={name} state={checks[name]} />
        ))}
      </div>

      {checkSuite.snapshotSha && (
        <div className="text-xs text-gray-400 font-mono truncate">
          {checkSuite.snapshotSha.slice(0, 7)}
        </div>
      )}

      {isAwaitingApproval && (
        <div className="pt-2 border-t border-gray-100 space-y-2">
          <button
            onClick={() => approveMutation.mutate()}
            disabled={approveMutation.isPending}
            className="w-full rounded-md bg-green-600 px-4 py-2.5 text-sm font-medium text-white hover:bg-green-700 disabled:opacity-50 transition-colors"
          >
            {approveMutation.isPending ? 'Approving...' : 'Approve & Merge'}
          </button>
          {approveMutation.error && (
            <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
              {approveMutation.error.message}
            </div>
          )}
        </div>
      )}
    </div>
  )
}
