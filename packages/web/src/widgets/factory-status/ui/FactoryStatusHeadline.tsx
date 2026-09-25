import { useMemo, type ComponentType } from 'react'
import {
  ActivityIcon,
  CheckCircle2Icon,
  CircleDollarSignIcon,
  ClockIcon,
  GaugeIcon,
  LayersIcon,
  ShieldOffIcon,
} from 'lucide-react'
import { Link } from 'react-router-dom'
import { useIssues, type Issue } from '../../../entities/issue'
import { useCostRollup, type AgentCostMetricDto } from '../../../entities/agent'
import { useProject } from '../../../entities/project'
import {
  runnerFleetLabel,
  runnerSummaryText,
  useRunnerSummary,
  type RunnerStatusSummary,
} from '../../../entities/runner'
import { cn } from '@/shared/lib/utils'
import { formatCost } from '@/shared/lib/format-compact'
import { deriveFactoryStatus } from '../model/factory-status'

export interface FactoryStatusHeadlineProps {
  issues?: Issue[]
  runnerSummary?: RunnerStatusSummary
  runnerSummaryHook?: typeof useRunnerSummary
  todayCost?: AgentCostMetricDto
}

export function FactoryStatusHeadline(props: FactoryStatusHeadlineProps = {}) {
  const { projectId } = useProject()

  const issuesQuery = useIssues(projectId ? { projectId } : undefined)
  const runnerSummaryQuery = (props.runnerSummaryHook ?? useRunnerSummary)()
  const costRollupQuery = useCostRollup()

  const issues = props.issues ?? issuesQuery.data
  const runnerSummary = props.runnerSummary ?? runnerSummaryQuery
  const todayCost = props.todayCost ?? costRollupQuery.data?.todayCost

  const status = useMemo(() => deriveFactoryStatus(issues, todayCost), [issues, todayCost])

  const runnerReady =
    runnerSummary.hasAdmissibleCapacity && runnerSummary.isLoading !== true && runnerSummary.isError !== true
  const runnerStatusLabel = runnerSummary.isLoading
    ? 'Checking'
    : runnerSummary.isError
      ? 'Unavailable'
      : runnerFleetLabel(runnerSummary)
  const eligiblePool = runnerSummary.fleet.eligiblePool
  const runnerCapacity = eligiblePool ? `${eligiblePool.used}/${eligiblePool.total} occupied` : 'unknown'
  const todayCostHasSample = (todayCost?.sampleCount ?? 0) > 0
  const todayCostDisplay = todayCostHasSample ? formatCost(todayCost?.amount ?? null, todayCost?.currency ?? null) : '—'

  return (
    <section
      data-testid="factory-status-headline"
      aria-label="Factory status"
      className="rounded-lg border border-border bg-background p-4"
    >
      <div className="flex flex-wrap items-center gap-x-6 gap-y-3">
        <Stat
          testId="factory-status-runner"
          icon={runnerReady ? CheckCircle2Icon : ShieldOffIcon}
          iconClassName={runnerReady ? 'text-emerald-500' : 'text-muted-foreground'}
          label="Runner"
          value={runnerStatusLabel}
          valueClassName={
            runnerReady
              ? 'text-emerald-700'
              : runnerSummary.fleet.state === 'admission-blocked'
                ? 'text-warning'
                : 'text-muted-foreground'
          }
        />
        <Stat
          testId="factory-status-runner-capacity"
          icon={GaugeIcon}
          iconClassName="text-info"
          label="Runner capacity"
          value={runnerCapacity}
        />
        <Stat
          testId="factory-status-runner-active-work"
          icon={LayersIcon}
          iconClassName="text-info"
          label="Active work"
          value={runnerSummary.activeWorkCount}
        />
        <Stat
          testId="factory-status-runner-draining"
          icon={ShieldOffIcon}
          iconClassName={runnerSummary.drainingCount > 0 ? 'text-warning' : 'text-muted-foreground/60'}
          label="Draining"
          value={runnerSummary.drainingCount}
        />
        <Stat
          testId="factory-status-in-flight"
          icon={LayersIcon}
          iconClassName="text-blue-500"
          label="In flight"
          value={status.inFlight}
        />
        <Stat
          testId="factory-status-awaiting-approval"
          icon={ClockIcon}
          iconClassName="text-amber-500"
          label="Awaiting approval"
          value={status.awaitingApproval}
        />
        <Stat
          testId="factory-status-shipped-today"
          icon={ActivityIcon}
          iconClassName="text-violet-500"
          label="Shipped today"
          value={status.shippedToday}
        />
        <Stat
          testId="factory-status-today-cost"
          icon={CircleDollarSignIcon}
          iconClassName="text-muted-foreground/60"
          label="Today cost"
          value={todayCostDisplay}
          valueClassName={todayCostHasSample ? 'tabular-nums' : 'text-muted-foreground/70'}
        />
        <div className="mt-3 flex flex-wrap items-center gap-x-3 gap-y-1 text-[10px] leading-4 text-muted-foreground">
          {runnerSummary.fleet.observedAt && (
            <time dateTime={runnerSummary.fleet.observedAt} data-testid="factory-status-runner-observed-at">
              Observed {runnerSummary.fleet.observedAt}
            </time>
          )}
          <Link to="/runners" className="underline hover:no-underline">
            Inspect Runner reasons
          </Link>
        </div>
      </div>
      {runnerSummary.rows.length > 0 && (
        <p className="mt-3 text-[10px] leading-4 text-muted-foreground" data-testid="factory-status-runner-facts">
          {runnerSummaryText(runnerSummary)}
        </p>
      )}
    </section>
  )
}

interface StatProps {
  testId: string
  icon: ComponentType<{ className?: string }>
  iconClassName?: string
  label: string
  value: string | number
  valueClassName?: string
  valueAriaLabel?: string
}

function Stat({ testId, icon: Icon, iconClassName, label, value, valueClassName, valueAriaLabel }: StatProps) {
  return (
    <div className="flex items-center gap-2" data-testid={testId}>
      <Icon className={cn('size-4', iconClassName)} />
      <div className="flex flex-col">
        <span className="text-[10px] font-medium text-muted-foreground uppercase tracking-wide">{label}</span>
        <span className={cn('text-sm font-semibold tabular-nums', valueClassName)} aria-label={valueAriaLabel}>
          {value}
        </span>
      </div>
    </div>
  )
}
