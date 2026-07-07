import { useNavigate } from 'react-router-dom'
import type { RunnerStatusSummary } from '../../../entities/runner'
import { useRunnerSummary } from '../../../entities/runner'
import { useProjectPath } from '../../../entities/project'
import { statusTreatment } from '@/shared/status-presentation'

const RUNNER_START_HINT = 'Start a runner with: npx mohist runner'

interface RunnerSummaryProps {
  summary: RunnerStatusSummary
}

function SummaryBadge({
  treatment,
  icon,
  children,
}: {
  treatment: ReturnType<typeof statusTreatment>
  icon: React.ReactNode
  children: React.ReactNode
}) {
  return (
    <span
      data-family={treatment.family}
      className={`inline-flex items-center gap-1 rounded-full px-2 py-0.5 ${treatment.container}`}
    >
      {icon}
      {children}
    </span>
  )
}

export function RunnerSummary({ summary }: RunnerSummaryProps) {
  const navigate = useNavigate()
  const toProjectPath = useProjectPath()
  const { rows } = summary

  if (rows.length === 0) {
    const treatment = statusTreatment('runner', 'offline')
    return (
      <div className="flex items-center gap-2 text-xs">
        <SummaryBadge
          treatment={treatment}
          icon={
            <svg className="h-3 w-3" viewBox="0 0 20 20" fill="currentColor" aria-hidden="true">
              <circle cx="10" cy="10" r="8" stroke="currentColor" strokeWidth="2" fill="none" />
              <path d="M10 6v4M10 14h.01" stroke="currentColor" strokeWidth="2" strokeLinecap="round" />
            </svg>
          }
        >
          No runner
        </SummaryBadge>
        <span className="text-muted-foreground">{RUNNER_START_HINT}</span>
      </div>
    )
  }

  const { connectedIdleCount, connectedBusyCount, hasConnectedCapacity } = summary

  if (!hasConnectedCapacity) {
    const treatment = statusTreatment('runner', 'stale')
    return (
      <button
        onClick={() => navigate(toProjectPath('/runners'))}
        className="flex items-center gap-2 text-xs hover:underline text-left"
      >
        <SummaryBadge
          treatment={treatment}
          icon={
            <svg className="h-3 w-3" viewBox="0 0 20 20" fill="currentColor" aria-hidden="true">
              <circle cx="10" cy="10" r="8" stroke="currentColor" strokeWidth="2" fill="none" />
              <path d="M10 6v4M10 14h.01" stroke="currentColor" strokeWidth="2" strokeLinecap="round" />
            </svg>
          }
        >
          Runner stale/offline
        </SummaryBadge>
        <span className="text-muted-foreground">{RUNNER_START_HINT}</span>
      </button>
    )
  }

  if (connectedBusyCount > 0) {
    const treatment = statusTreatment('runner', 'busy')
    return (
      <button
        onClick={() => navigate(toProjectPath('/runners'))}
        className="flex items-center gap-2 text-xs hover:underline text-left"
      >
        <SummaryBadge
          treatment={treatment}
          icon={
            <svg className="h-3 w-3" viewBox="0 0 20 20" fill="currentColor" aria-hidden="true">
              <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zM9 5a1 1 0 100 2h2a1 1 0 100-2H9zM8.95 9.636a1 1 0 011.06 0l1.275 1.638a1 1 0 01-.742 1.636H8.05a1 1 0 01-.742-1.637l1.275-1.637a1 1 0 010-1.638z" clipRule="evenodd" />
            </svg>
          }
        >
          Runner busy
        </SummaryBadge>
        <span className="text-muted-foreground">
          {connectedBusyCount} running {connectedBusyCount === 1 ? 'workflow' : 'workflows'}
        </span>
      </button>
    )
  }

  const idleTreatment = statusTreatment('runner', 'idle')
  return (
    <button
      onClick={() => navigate(toProjectPath('/runners'))}
      className="flex items-center gap-2 text-xs hover:underline text-left"
    >
      <SummaryBadge
        treatment={idleTreatment}
        icon={
          <svg className="h-3 w-3" viewBox="0 0 20 20" fill="currentColor" aria-hidden="true">
            <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.707-9.293a1 1 0 00-1.414-1.414L9 10.586 7.707 9.293a1 1 0 00-1.414 1.414l2 2a1 1 0 001.414 0l4-4z" clipRule="evenodd" />
          </svg>
        }
      >
        Runner idle
      </SummaryBadge>
      <span className="text-muted-foreground">
        {connectedIdleCount} {connectedIdleCount === 1 ? 'runner' : 'runners'} ready
      </span>
    </button>
  )
}

export function RunnerSummaryBadge() {
  const summary = useRunnerSummary()
  return <RunnerSummary summary={summary} />
}