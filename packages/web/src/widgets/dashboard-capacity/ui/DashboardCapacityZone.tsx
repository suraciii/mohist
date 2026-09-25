import { Link } from 'react-router-dom'
import { useRunnerSummary, type RunnerStatusSummary } from '../../../entities/runner'
import { cn } from '@/shared/lib/utils'
import { GaugeIcon } from 'lucide-react'

export interface DashboardCapacityZoneProps {
  runnerSummaryOverride?: RunnerStatusSummary
  runnerSummaryHook?: typeof useRunnerSummary
}

/**
 * Capacity level is the global Runner slot projection. It stays visible even
 * when no project has active work, because slots are shared across Projects.
 * Unknown used capacity remains unknown rather than being rendered as free.
 */
export function DashboardCapacityZone({
  runnerSummaryOverride,
  runnerSummaryHook = useRunnerSummary,
}: DashboardCapacityZoneProps = {}) {
  const fetchedSummary = runnerSummaryHook()
  const summary = runnerSummaryOverride ?? fetchedSummary
  if (summary.isLoading || summary.rows.length === 0) return null

  const configuredExcludedSlots = summary.fleet.excludedGroups.reduce<number | null>(
    (total, group) => (total == null || group.configuredSlots == null ? null : total + group.configuredSlots),
    0,
  )
  const max = summary.fleet.eligiblePool?.total ?? configuredExcludedSlots
  const displayMax = max ?? 'unknown'
  if (max == null || max <= 0) return null
  const active = summary.fleet.eligiblePool == null ? null : Math.max(0, summary.fleet.eligiblePool.used)
  const usedPercent = active == null || max == null || max <= 0 ? 0 : Math.min(100, Math.round((active / max) * 100))
  const saturated = active != null && max != null && active >= max

  return (
    <section
      data-testid="dashboard-zone-capacity"
      data-zone="capacity"
      data-state={active == null ? 'unknown' : saturated ? 'saturated' : 'available'}
      data-active={active == null ? 'unknown' : active}
      data-max={displayMax}
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
      <div className="flex items-center gap-2 flex-1 min-w-[160px]" data-testid="dashboard-zone-capacity-usage">
        <div className="h-1.5 flex-1 rounded-full bg-muted overflow-hidden" aria-hidden>
          <div
            data-testid="dashboard-zone-capacity-bar"
            className={cn('h-full rounded-full transition-all duration-300', saturated ? 'bg-warning' : 'bg-info')}
            style={{ width: `${usedPercent}%` }}
          />
        </div>
        <span data-testid="dashboard-zone-capacity-count" className="text-sm font-medium tabular-nums text-foreground">
          {active == null ? `unknown/${displayMax}` : `${active}/${displayMax}`}
        </span>
      </div>
      <Link
        to="/runners"
        data-testid="dashboard-zone-capacity-link"
        className="text-xs text-muted-foreground hover:text-foreground hover:underline shrink-0"
      >
        Manage slots
      </Link>
    </section>
  )
}
