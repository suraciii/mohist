import { useState, useEffect, useMemo } from 'react'
import { Link } from 'react-router-dom'
import { ChevronDownIcon } from 'lucide-react'
import { StatusBar } from '../../../shared/ui/StatusBar'
import { useActivityCards } from '../../../entities/agent-ops'
import { UsageSnapshotLabel, useActivityUsageSnapshot, useActivityEvents, sortActivityEvents, type ActivityEvent } from '../../../widgets/coder-session'
import { RunnerSummaryBadge } from '../../../widgets/runner-status'
import { useProjectPath } from '../../../entities/project'
import { ActivityEventEntry } from './ActivityEventEntry'

const EVENT_TYPES: ActivityEvent['type'][] = ['issue-state', 'workflow-stage', 'agent-session', 'runner', 'failure']
const EVENT_TYPE_LABELS: Record<ActivityEvent['type'], string> = {
  'issue-state': 'Issue changes',
  'workflow-stage': 'Workflow stages',
  'agent-session': 'Agent sessions',
  runner: 'Runners',
  failure: 'Failures',
}

export interface ActivityPageDependencies {
  activityEventsHook: typeof useActivityEvents
  activityCardsHook: typeof useActivityCards
  activityUsageSnapshotHook: typeof useActivityUsageSnapshot
  RunnerSummaryBadge: typeof RunnerSummaryBadge
}

const defaultDependencies: ActivityPageDependencies = {
  activityEventsHook: useActivityEvents,
  activityCardsHook: useActivityCards,
  activityUsageSnapshotHook: useActivityUsageSnapshot,
  RunnerSummaryBadge,
}

function FilterBar({
  selectedTypes,
  attentionOnly,
  onToggleType,
  onToggleAttention,
  onClear,
  counts,
}: {
  selectedTypes: Set<ActivityEvent['type']>
  attentionOnly: boolean
  onToggleType: (type: ActivityEvent['type']) => void
  onToggleAttention: () => void
  onClear: () => void
  counts: Record<ActivityEvent['type'], number>
}) {
  const hasFilters = selectedTypes.size > 0 || attentionOnly

  return (
    <div className="flex flex-wrap items-center gap-2" data-testid="activity-filter-bar">
      {EVENT_TYPES.map((type) => {
        const active = selectedTypes.has(type)
        return (
          <button
            key={type}
            type="button"
            onClick={() => onToggleType(type)}
            data-testid={`activity-filter-${type}`}
            data-active={active}
            aria-pressed={active}
            className={`inline-flex items-center gap-1 rounded-full border px-2.5 py-0.5 text-xs font-semibold transition-colors ${
              active
                ? 'bg-foreground text-background border-foreground'
                : 'border-border bg-background text-muted-foreground hover:bg-muted'
            }`}
          >
            {EVENT_TYPE_LABELS[type]}
            <span className="tabular-nums text-[10px] opacity-80">{counts[type] ?? 0}</span>
          </button>
        )
      })}
      <button
        type="button"
        onClick={onToggleAttention}
        data-testid="activity-filter-attention"
        data-active={attentionOnly}
        aria-pressed={attentionOnly}
        className={`inline-flex items-center gap-1 rounded-full border px-2.5 py-0.5 text-xs font-semibold transition-colors ${
          attentionOnly
            ? 'bg-warning text-warning-foreground border-warning-border'
            : 'border-border bg-background text-muted-foreground hover:bg-muted'
        }`}
      >
        Attention only
      </button>
      {hasFilters && (
        <button
          type="button"
          onClick={onClear}
          data-testid="activity-filter-clear"
          className="text-xs font-medium text-muted-foreground hover:text-foreground"
        >
          Clear
        </button>
      )}
    </div>
  )
}

function ZoneHeader({
  title,
  count,
  collapsed,
  onToggle,
  testId,
}: {
  title: string
  count: number
  collapsed: boolean
  onToggle: () => void
  testId: string
}) {
  return (
    <button
      type="button"
      onClick={onToggle}
      aria-expanded={!collapsed}
      data-testid={testId}
      className="flex w-full items-center gap-2 text-left"
    >
      <ChevronDownIcon
        className={`h-4 w-4 text-muted-foreground transition-transform ${collapsed ? '-rotate-90' : ''}`}
        aria-hidden="true"
      />
      <span className="text-sm font-semibold text-foreground">{title}</span>
      <span className="text-xs text-muted-foreground">({count})</span>
    </button>
  )
}

export function ActivityPage({
  dependencies = defaultDependencies,
  now: providedNow,
}: {
  dependencies?: ActivityPageDependencies
  now?: number
} = {}) {
  const [clockNow, setClockNow] = useState(() => providedNow ?? Date.now())
  const [selectedTypes, setSelectedTypes] = useState<Set<ActivityEvent['type']>>(new Set())
  const [attentionOnly, setAttentionOnly] = useState(false)
  const [attentionCollapsed, setAttentionCollapsed] = useState(false)
  const [routineCollapsed, setRoutineCollapsed] = useState(false)
  const { activityEventsHook: useEvents, activityCardsHook: useCards, activityUsageSnapshotHook: useUsageSnapshot, RunnerSummaryBadge: RunnerBadge } = dependencies
  const selectedTypeFilters = useMemo(() => [...selectedTypes], [selectedTypes])
  const { events, isLoading, isError, sourceErrors = [] } = useEvents({
    types: selectedTypeFilters,
    attentionOnly,
  })
  const { statusCounts, slotUsage } = useCards()
  const usageSnapshot = useUsageSnapshot()
  const toProjectPath = useProjectPath()

  useEffect(() => {
    if (providedNow != null) return
    const id = setInterval(() => setClockNow(Date.now()), 1000)
    return () => clearInterval(id)
  }, [providedNow])

  const now = providedNow ?? clockNow
  const orderedEvents = useMemo(() => sortActivityEvents(events), [events])

  const counts = useMemo(() => {
    const result: Record<ActivityEvent['type'], number> = {
      'issue-state': 0,
      'workflow-stage': 0,
      'agent-session': 0,
      runner: 0,
      failure: 0,
    }
    for (const event of orderedEvents) {
      result[event.type]++
    }
    return result
  }, [orderedEvents])

  const filtered = useMemo(() => {
    return orderedEvents.filter((event) => {
      if (selectedTypes.size > 0 && !selectedTypes.has(event.type)) return false
      if (attentionOnly && event.attention === 'routine') return false
      return true
    })
  }, [orderedEvents, selectedTypes, attentionOnly])

  const attentionEntries = filtered.filter((e) => e.attention !== 'routine')
  const routineEntries = filtered.filter((e) => e.attention === 'routine')
  const evidenceCounts = useMemo(() => ({
    completed: orderedEvents.filter((event) => event.outcome === 'completed').length,
    failed: orderedEvents.filter((event) => event.outcome === 'failed').length,
  }), [orderedEvents])

  return (
    <div className="flex-1 flex flex-col min-h-0">
      <StatusBar
        active={statusCounts.active}
        waiting={statusCounts.waiting}
        completed={evidenceCounts.completed}
        failed={evidenceCounts.failed}
        activeSlots={slotUsage.active}
        maxSlots={slotUsage.max}
      >
        <RunnerBadge targetPath="/runners?from=activity" />
      </StatusBar>

      <div className="flex-1 overflow-y-auto">
        <div className="max-w-3xl mx-auto px-4 py-4 md:px-6 space-y-6">
          <div className="flex justify-end">
            <Link
              to={toProjectPath('/runners?from=activity')}
              data-testid="activity-runners-link"
              className="inline-flex items-center gap-1 text-xs font-medium text-info hover:text-info-foreground hover:underline"
            >
              View runners
              <svg className="h-3 w-3" viewBox="0 0 20 20" fill="currentColor" aria-hidden="true">
                <path
                  fillRule="evenodd"
                  d="M7.293 14.707a1 1 0 010-1.414L10.586 10 7.293 6.707a1 1 0 011.414-1.414l4 4a1 1 0 010 1.414l-4 4a1 1 0 01-1.414 0z"
                  clipRule="evenodd"
                />
              </svg>
            </Link>
          </div>

          <section>
            <UsageSnapshotLabel snapshot={usageSnapshot} />
          </section>

          <section className="space-y-3">
            <FilterBar
              selectedTypes={selectedTypes}
              attentionOnly={attentionOnly}
              onToggleType={(type) => {
                setSelectedTypes((prev) => {
                  const next = new Set(prev)
                  if (next.has(type)) next.delete(type)
                  else next.add(type)
                  return next
                })
              }}
              onToggleAttention={() => setAttentionOnly((v) => !v)}
              onClear={() => {
                setSelectedTypes(new Set())
                setAttentionOnly(false)
              }}
              counts={counts}
            />

            {sourceErrors.map((source) => (
              <div
                key={source.key}
                role="alert"
                data-testid={`activity-evidence-error-${source.key}`}
                className="flex flex-wrap items-center justify-between gap-2 rounded-lg border border-warning-border bg-warning-subtle px-4 py-3 text-sm text-warning-foreground"
              >
                <span>Activity evidence is incomplete: {source.label} is unavailable.</span>
                <button
                  type="button"
                  onClick={() => { void source.retry() }}
                  data-testid={`activity-evidence-retry-${source.key}`}
                  className="rounded border border-warning-border bg-background px-2 py-1 text-xs font-semibold text-foreground hover:bg-warning-subtle"
                >
                  Retry
                </button>
              </div>
            ))}

            {isLoading && events.length === 0 && (
              <div className="space-y-2">
                {[1, 2, 3].map((i) => (
                  <div key={i} className="h-16 rounded-lg border border-border bg-muted animate-pulse" />
                ))}
              </div>
            )}

            {!isLoading && !isError && events.length === 0 && (
              <div className="rounded-lg border border-dashed border-border bg-muted/50 px-4 py-8 text-center">
                <p className="text-sm text-muted-foreground">No activity yet.</p>
              </div>
            )}

            {attentionEntries.length > 0 && (
              <div data-testid="activity-attention-zone" className="space-y-3">
                <ZoneHeader
                  title="Needs attention"
                  count={attentionEntries.length}
                  collapsed={attentionCollapsed}
                  onToggle={() => setAttentionCollapsed((value) => !value)}
                  testId="activity-attention-toggle"
                />
                {!attentionCollapsed && (
                  <div className="space-y-3">
                    {attentionEntries.map((event) => (
                      <ActivityEventEntry key={event.id} event={event} now={now} />
                    ))}
                  </div>
                )}
              </div>
            )}

            {routineEntries.length > 0 && (
              <div data-testid="activity-routine-zone" className="space-y-3">
                <ZoneHeader
                  title="Routine"
                  count={routineEntries.length}
                  collapsed={routineCollapsed}
                  onToggle={() => setRoutineCollapsed((value) => !value)}
                  testId="activity-routine-toggle"
                />
                {!routineCollapsed && (
                  <div className="space-y-3">
                    {routineEntries.map((event) => (
                      <ActivityEventEntry key={event.id} event={event} now={now} />
                    ))}
                  </div>
                )}
              </div>
            )}

            {filtered.length === 0 && events.length > 0 && (
              <div className="rounded-lg border border-dashed border-border bg-muted/50 px-4 py-8 text-center">
                <p className="text-sm text-muted-foreground">No events match the selected filters.</p>
              </div>
            )}
          </section>
        </div>
      </div>
    </div>
  )
}
