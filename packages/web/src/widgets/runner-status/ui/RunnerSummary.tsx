import { useNavigate } from 'react-router-dom'
import type { RunnerStatusSummary } from '../../../entities/runner'
import { useRunnerSummary } from '../../../entities/runner'

const RUNNER_START_HINT = 'Start a runner with: npx mohist runner'

interface RunnerSummaryProps {
  summary: RunnerStatusSummary
}

export function RunnerSummary({ summary }: RunnerSummaryProps) {
  const navigate = useNavigate()
  const { rows } = summary

  if (rows.length === 0) {
    return (
      <div className="flex items-center gap-2 text-xs">
        <span className="inline-flex items-center gap-1 rounded-full px-2 py-0.5 bg-gray-100 text-gray-500">
          <svg className="h-3 w-3" viewBox="0 0 20 20" fill="currentColor">
            <circle cx="10" cy="10" r="8" stroke="currentColor" strokeWidth="2" fill="none" />
            <path d="M10 6v4M10 14h.01" stroke="currentColor" strokeWidth="2" strokeLinecap="round" />
          </svg>
          No runner
        </span>
        <span className="text-gray-400">{RUNNER_START_HINT}</span>
      </div>
    )
  }

  const { connectedIdleCount, connectedBusyCount, hasConnectedCapacity } = summary

  if (!hasConnectedCapacity) {
    return (
      <button
        onClick={() => navigate('/activity')}
        className="flex items-center gap-2 text-xs hover:underline text-left"
      >
        <span className="inline-flex items-center gap-1 rounded-full px-2 py-0.5 bg-amber-100 text-amber-700">
          <svg className="h-3 w-3" viewBox="0 0 20 20" fill="currentColor">
            <circle cx="10" cy="10" r="8" stroke="currentColor" strokeWidth="2" fill="none" />
            <path d="M10 6v4M10 14h.01" stroke="currentColor" strokeWidth="2" strokeLinecap="round" />
          </svg>
          Runner stale/offline
        </span>
        <span className="text-gray-400">{RUNNER_START_HINT}</span>
      </button>
    )
  }

  if (connectedBusyCount > 0) {
    return (
      <button
        onClick={() => navigate('/activity')}
        className="flex items-center gap-2 text-xs hover:underline text-left"
      >
        <span className="inline-flex items-center gap-1 rounded-full px-2 py-0.5 bg-blue-100 text-blue-700">
          <svg className="h-3 w-3" viewBox="0 0 20 20" fill="currentColor">
            <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zM9 5a1 1 0 100 2h2a1 1 0 100-2H9zM8.95 9.636a1 1 0 011.06 0l1.275 1.638a1 1 0 01-.742 1.636H8.05a1 1 0 01-.742-1.637l1.275-1.637a1 1 0 010-1.638z" clipRule="evenodd" />
          </svg>
          Runner busy
        </span>
        <span className="text-gray-500">
          {connectedBusyCount} running {connectedBusyCount === 1 ? 'workflow' : 'workflows'}
        </span>
      </button>
    )
  }

  return (
    <button
      onClick={() => navigate('/activity')}
      className="flex items-center gap-2 text-xs hover:underline text-left"
    >
      <span className="inline-flex items-center gap-1 rounded-full px-2 py-0.5 bg-green-100 text-green-700">
        <svg className="h-3 w-3" viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.707-9.293a1 1 0 00-1.414-1.414L9 10.586 7.707 9.293a1 1 0 00-1.414 1.414l2 2a1 1 0 001.414 0l4-4z" clipRule="evenodd" />
        </svg>
        Runner idle
      </span>
      <span className="text-gray-500">
        {connectedIdleCount} {connectedIdleCount === 1 ? 'runner' : 'runners'} ready
      </span>
    </button>
  )
}

export function RunnerSummaryBadge() {
  const summary = useRunnerSummary()
  return <RunnerSummary summary={summary} />
}