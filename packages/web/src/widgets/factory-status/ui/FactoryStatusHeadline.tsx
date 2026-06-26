import { useMemo, type ComponentType } from 'react'
import { ActivityIcon, CheckCircle2Icon, CircleDollarSignIcon, ClockIcon, LayersIcon, ShieldOffIcon } from 'lucide-react'
import { useIssues, type Issue } from '../../../entities/issue'
import { useAgentStatus, type AgentStatus } from '../../../entities/agent'
import { useProject } from '../../../entities/project'
import { cn } from '@/shared/lib/utils'
import { deriveFactoryStatus } from '../model/factory-status'

export interface FactoryStatusHeadlineProps {
  issues?: Issue[]
  agentStatus?: AgentStatus
}

export function FactoryStatusHeadline(props: FactoryStatusHeadlineProps = {}) {
  const { projectId } = useProject()

  const issuesQuery = useIssues(projectId ? { projectId } : undefined)
  const agentStatusQuery = useAgentStatus()

  const issues = props.issues ?? issuesQuery.data
  const agentStatus = props.agentStatus ?? agentStatusQuery.data

  const status = useMemo(
    () => deriveFactoryStatus(issues, agentStatus),
    [issues, agentStatus],
  )

  const runnerUp = status.runnerAvailable

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
        <div className="flex items-center gap-2" data-testid="factory-cost-reserved">
          <CircleDollarSignIcon className="size-4 text-muted-foreground/60" />
          <div className="flex flex-col">
            <span className="text-[10px] font-medium text-muted-foreground uppercase tracking-wide">Today cost</span>
            <span className="text-sm font-semibold text-muted-foreground/70" aria-label="Today cost reserved">—</span>
          </div>
        </div>
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
}

function Stat({ testId, icon: Icon, iconClassName, label, value, valueClassName }: StatProps) {
  return (
    <div className="flex items-center gap-2" data-testid={testId}>
      <Icon className={cn('size-4', iconClassName)} />
      <div className="flex flex-col">
        <span className="text-[10px] font-medium text-muted-foreground uppercase tracking-wide">{label}</span>
        <span className={cn('text-sm font-semibold tabular-nums', valueClassName)}>{value}</span>
      </div>
    </div>
  )
}
