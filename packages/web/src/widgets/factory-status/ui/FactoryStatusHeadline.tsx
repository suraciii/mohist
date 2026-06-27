import { useMemo, type ComponentType } from 'react'
import { ActivityIcon, CheckCircle2Icon, CircleDollarSignIcon, ClockIcon, LayersIcon, ShieldOffIcon } from 'lucide-react'
import { useIssues, type Issue } from '../../../entities/issue'
import { useAgentStatus, useCostRollup, type AgentCostMetricDto, type AgentStatus } from '../../../entities/agent'
import { useProject } from '../../../entities/project'
import { cn } from '@/shared/lib/utils'
import { formatCost } from '@/shared/lib/format-compact'
import { deriveFactoryStatus } from '../model/factory-status'

export interface FactoryStatusHeadlineProps {
  issues?: Issue[]
  agentStatus?: AgentStatus
  todayCost?: AgentCostMetricDto
}

export function FactoryStatusHeadline(props: FactoryStatusHeadlineProps = {}) {
  const { projectId } = useProject()

  const issuesQuery = useIssues(projectId ? { projectId } : undefined)
  const agentStatusQuery = useAgentStatus()
  const costRollupQuery = useCostRollup()

  const issues = props.issues ?? issuesQuery.data
  const agentStatus = props.agentStatus ?? agentStatusQuery.data
  const todayCost = props.todayCost ?? costRollupQuery.data?.todayCost

  const status = useMemo(
    () => deriveFactoryStatus(issues, agentStatus, todayCost),
    [issues, agentStatus, todayCost],
  )

  const runnerUp = status.runnerAvailable
  const todayCostHasSample = (todayCost?.sampleCount ?? 0) > 0
  const todayCostDisplay = todayCostHasSample
    ? formatCost(todayCost?.amount ?? null, todayCost?.currency ?? null)
    : '—'

  return (
    <section
      data-testid="factory-status-headline"
      aria-label="Factory status"
      className="rounded-lg border border-border bg-background p-4"
    >
      <div className="flex flex-wrap items-center gap-x-6 gap-y-3">
        <Stat
          testId="factory-status-runner"
          icon={runnerUp ? CheckCircle2Icon : ShieldOffIcon}
          iconClassName={runnerUp ? 'text-emerald-500' : 'text-muted-foreground'}
          label="Runner"
          value={runnerUp ? 'Online' : 'Unavailable'}
          valueClassName={runnerUp ? 'text-emerald-700' : 'text-muted-foreground'}
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
          valueAriaLabel={todayCostHasSample ? 'Today cost' : 'Today cost unavailable'}
        />
      </div>
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
        <span className={cn('text-sm font-semibold tabular-nums', valueClassName)} aria-label={valueAriaLabel}>{value}</span>
      </div>
    </div>
  )
}
