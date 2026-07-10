import { Link } from 'react-router-dom'
import { useProjectPath } from '../../../entities/project'
import { useAgentStatus, type AgentStatus } from '../../../entities/agent'
import { cn } from '@/shared/lib/utils'
import { GaugeIcon } from 'lucide-react'

export interface DashboardCapacityZoneProps {
  /**
   * Test/dev override: lets spec tests inject an in-memory agent status
   * without going through `useAgentStatus`. Production callers should rely
   * on the default `useAgentStatus()` pull.
   */
  agentStatusOverride?: AgentStatus
  agentStatusHook?: typeof useAgentStatus
}

/**
 * Capacity level — a compact usage strip that surfaces runner slot usage
 * (`active / max`) on its own. Sourced from `agentStatus.capacity` (the
 * same field the attention model and the headline already consume) — not
 * from `AgentActivity.summary.slots` — so capacity feedback stays on a
 * single feed.
 *
 * Collapse rule: renders nothing when capacity data is absent or
 * `max === 0`. There is no reserved fixed-height box, so an absent
 * capacity level is not visible on the page at all.
 */
export function DashboardCapacityZone({
  agentStatusOverride,
  agentStatusHook = useAgentStatus,
}: DashboardCapacityZoneProps = {}) {
  const { data: fetchedStatus } = agentStatusHook()
  const agentStatus = agentStatusOverride ?? fetchedStatus
  const toProjectPath = useProjectPath()

  const capacity = agentStatus?.capacity
  if (!capacity || capacity.max <= 0) return null

  const active = Math.max(0, capacity.active)
  const max = capacity.max
  const usedPercent = Math.min(100, Math.round((active / max) * 100))
  const saturated = active >= max

  return (
    <section
      data-testid="dashboard-zone-capacity"
      data-zone="capacity"
      data-state={saturated ? 'saturated' : 'available'}
      data-active={active}
      data-max={max}
      aria-label="Runner capacity"
      className={cn(
        'flex flex-wrap items-center gap-3 rounded-lg border bg-background px-4 py-3',
        saturated ? 'border-warning-border' : 'border-border',
      )}
    >
      <span
        className={cn(
          'inline-flex items-center justify-center size-6 rounded-full shrink-0',
          saturated ? 'bg-warning text-warning-foreground' : 'bg-info-subtle text-info',
        )}
        aria-hidden
      >
        <GaugeIcon className="size-3.5" />
      </span>
      <span
        data-testid="dashboard-zone-capacity-label"
        className="text-xs font-semibold uppercase tracking-wide text-muted-foreground"
      >
        Runner capacity
      </span>
      <div
        className="flex items-center gap-2 flex-1 min-w-[160px]"
        data-testid="dashboard-zone-capacity-usage"
      >
        <div
          className="h-1.5 flex-1 rounded-full bg-muted overflow-hidden"
          aria-hidden
        >
          <div
            data-testid="dashboard-zone-capacity-bar"
            className={cn(
              'h-full rounded-full transition-all duration-300',
              saturated ? 'bg-warning' : 'bg-info',
            )}
            style={{ width: `${usedPercent}%` }}
          />
        </div>
        <span
          data-testid="dashboard-zone-capacity-count"
          className="text-sm font-medium tabular-nums text-foreground"
        >
          {active}/{max}
        </span>
      </div>
      <Link
        to={toProjectPath('/runners')}
        data-testid="dashboard-zone-capacity-link"
        className="text-xs text-muted-foreground hover:text-foreground hover:underline shrink-0"
      >
        Manage slots
      </Link>
    </section>
  )
}
