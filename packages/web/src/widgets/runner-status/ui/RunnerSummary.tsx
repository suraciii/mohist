import { useNavigate } from 'react-router-dom'
import { runnerSummaryText, type RunnerStatusSummary } from '../../../entities/runner'
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
  const rows = summary.rows
  const goToRunners = () => navigate(targetPath)

  if (rows.length === 0) {
    const label = summary.isLoading
      ? 'Checking Runner status'
      : summary.inventory?.state === 'first-install'
        ? 'No Runner definitions'
        : summary.isError
          ? 'Runner status unavailable'
          : 'Runner inventory'
    return (
      <button
        type="button"
        onClick={goToRunners}
        data-testid="runner-summary-button"
        className="flex min-w-0 flex-wrap items-center gap-2 text-left text-xs hover:underline"
      >
        <span className="inline-flex shrink-0 items-center rounded-full bg-muted px-2 py-0.5 text-muted-foreground">
          {label}
        </span>
        <SummaryAction summary={summary} />
      </button>
    )
  }

  const label = summary.blockedCount > 0 ? 'Runner admission blocked' : 'Runner admission ready'
  const tone = summary.blockedCount > 0 ? 'bg-warning-subtle text-warning' : 'bg-success-subtle text-success'
  const counts = runnerSummaryText(summary)

  return (
    <button
      type="button"
      onClick={goToRunners}
      data-testid="runner-summary-button"
      className="flex min-w-0 flex-wrap items-center gap-2 text-left text-xs hover:underline"
    >
      <span className={`inline-flex shrink-0 items-center rounded-full px-2 py-0.5 ${tone}`}>{label}</span>
      <span className="text-muted-foreground">{counts}</span>
    </button>
  )
}

export function RunnerSummaryBadge({ targetPath }: Pick<RunnerSummaryProps, 'targetPath'>) {
  const summary = useRunnerSummary()
  return <RunnerSummary summary={summary} targetPath={targetPath} />
}
