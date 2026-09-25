import { useNavigate } from 'react-router-dom'
import { runnerFleetLabel, runnerSummaryText, type RunnerStatusSummary } from '../../../entities/runner'
import { useRunnerSummary } from '../../../entities/runner'

interface RunnerSummaryProps {
  summary: RunnerStatusSummary
  targetPath?: string
}

function SummaryAction({ summary }: { summary: RunnerStatusSummary }) {
  const action = summary.inventory?.nextActions[0]
  if (!action) return null
  return (
    <span className="text-muted-foreground">
      {action.message}
      {action.command ? (
        <>
          {' '}
          <code className="break-all">{action.command}</code>
        </>
      ) : null}
    </span>
  )
}

export function RunnerSummary({ summary, targetPath = '/runners' }: RunnerSummaryProps) {
  const navigate = useNavigate()
  const goToRunners = () => navigate(targetPath)
  const label = summary.isLoading
    ? 'Checking Runner status'
    : summary.isError
      ? 'Runner status unavailable'
      : runnerFleetLabel(summary)
  const tone =
    summary.fleet.state === 'capacity-available'
      ? 'bg-success-subtle text-success'
      : summary.fleet.state === 'admission-blocked' || summary.fleet.state === 'capacity-full'
        ? 'bg-warning-subtle text-warning'
        : 'bg-muted text-muted-foreground'

  return (
    <button
      type="button"
      onClick={goToRunners}
      aria-label="Inspect Runner reasons"
      data-testid="runner-summary-button"
      className="flex min-w-0 flex-wrap items-center gap-2 text-left text-xs hover:underline"
    >
      <span className={`inline-flex shrink-0 items-center rounded-full px-2 py-0.5 ${tone}`}>{label}</span>
      <span className="text-muted-foreground">{runnerSummaryText(summary)}</span>
      {summary.fleet.observedAt && (
        <time dateTime={summary.fleet.observedAt} className="text-muted-foreground">
          Observed {summary.fleet.observedAt}
        </time>
      )}
      <SummaryAction summary={summary} />
    </button>
  )
}

export function RunnerSummaryBadge({ targetPath }: Pick<RunnerSummaryProps, 'targetPath'>) {
  const summary = useRunnerSummary()
  return <RunnerSummary summary={summary} targetPath={targetPath} />
}
