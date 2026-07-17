import { useMemo, useState } from 'react'
import { ActivityIcon, ArrowDownIcon, ArrowUpIcon } from 'lucide-react'
import { CategoryFilter } from './CategoryFilter'
import { EventTimelineRow } from './EventTimelineRow'
import {
  useEventTimeline,
  type EventTimelineHistoryHook,
} from '../useEventTimeline'
import type { TimelineCategory, TimelineEntry } from '../model/types'

export interface EventTimelinePanelProps {
  issueNumber: number
  workflowStatus?: string | null
  enabled?: boolean
  className?: string
  showHeader?: boolean
  historyHook?: EventTimelineHistoryHook
}

interface EventTimelinePanelViewProps {
  entries: TimelineEntry[]
  isLoading: boolean
  workflowStatus?: string | null
  className?: string
  showHeader?: boolean
}

function startOfDay(iso: string): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return 'Unknown'
  return date.toLocaleDateString(undefined, {
    weekday: 'short',
    year: 'numeric',
    month: 'short',
    day: 'numeric',
  })
}

export function EventTimelinePanel({
  issueNumber,
  workflowStatus,
  enabled = true,
  className,
  showHeader = true,
  historyHook,
}: EventTimelinePanelProps) {
  const { entries, isLoading } = useEventTimeline(issueNumber, enabled, historyHook)

  return (
    <EventTimelinePanelView
      entries={entries}
      isLoading={isLoading}
      workflowStatus={workflowStatus}
      className={className}
      showHeader={showHeader}
    />
  )
}

export function EventTimelinePanelView({
  entries,
  isLoading,
  workflowStatus,
  className,
  showHeader = true,
}: EventTimelinePanelViewProps) {
  const [order, setOrder] = useState<'newest' | 'chronological'>('newest')
  const [selectedCategories, setSelectedCategories] = useState<Set<TimelineCategory>>(
    new Set(['workflow', 'approval', 'integration', 'success', 'failure', 'metadata']),
  )

  const counts = useMemo(() => {
    const result: Record<TimelineCategory, number> = {
      workflow: 0,
      approval: 0,
      integration: 0,
      success: 0,
      failure: 0,
      metadata: 0,
    }
    for (const entry of entries) {
      result[entry.category]++
    }
    return result
  }, [entries])

  const filtered = useMemo(() => {
    return entries.filter((entry) => selectedCategories.has(entry.category))
  }, [entries, selectedCategories])

  const sorted = useMemo(() => {
    const copy = [...filtered]
    copy.sort((a, b) => new Date(a.time).getTime() - new Date(b.time).getTime())
    return order === 'newest' ? copy.reverse() : copy
  }, [filtered, order])

  const grouped = useMemo(() => {
    const groups: { day: string; events: TimelineEntry[] }[] = []
    for (const entry of sorted) {
      const day = startOfDay(entry.time)
      const last = groups[groups.length - 1]
      if (last && last.day === day) {
        last.events.push(entry)
      } else {
        groups.push({ day, events: [entry] })
      }
    }
    return groups
  }, [sorted])

  const isLive = workflowStatus === 'running'

  const toggleCategory = (category: TimelineCategory) => {
    setSelectedCategories((prev) => {
      const next = new Set(prev)
      if (next.has(category)) {
        next.delete(category)
      } else {
        next.add(category)
      }
      return next
    })
  }

  return (
    <div
      className={className ?? 'rounded-lg border border-border bg-background p-4'}
      data-testid="event-timeline-panel"
    >
      {showHeader && (
        <div className="mb-3 flex flex-wrap items-center justify-between gap-3">
          <div className="flex items-center gap-2">
            <ActivityIcon className="h-4 w-4 text-muted-foreground" />
            <h2 className="text-sm font-semibold text-foreground">Activity</h2>
            {isLive && (
              <span
                data-testid="timeline-live-badge"
                className="inline-flex items-center gap-1 rounded-full bg-info-subtle px-2 py-0.5 text-[10px] font-semibold text-info"
              >
                <span className="inline-block h-1.5 w-1.5 rounded-full bg-info animate-pulse" />
                Live
              </span>
            )}
            {!isLive && (
              <span
                data-testid="timeline-inactive-badge"
                className="inline-flex items-center gap-1 rounded-full bg-muted px-2 py-0.5 text-[10px] font-semibold text-muted-foreground"
              >
                Live
              </span>
            )}
          </div>

          <button
            type="button"
            onClick={() => setOrder((o) => (o === 'newest' ? 'chronological' : 'newest'))}
            className="inline-flex items-center gap-1 rounded-md border border-border bg-background px-2 py-1 text-xs font-medium text-muted-foreground hover:bg-muted"
            data-testid="timeline-order-toggle"
          >
            {order === 'newest' ? (
              <>
                <ArrowDownIcon className="h-3 w-3" />
                Newest first
              </>
            ) : (
              <>
                <ArrowUpIcon className="h-3 w-3" />
                Chronological
              </>
            )}
          </button>
        </div>
      )}

      <div className="mb-3 flex flex-wrap items-center gap-2">
        <CategoryFilter
          selected={selectedCategories}
          onToggle={toggleCategory}
          counts={counts}
        />
        {selectedCategories.size < 6 && (
          <button
            type="button"
            onClick={() => setSelectedCategories(new Set(['workflow', 'approval', 'integration', 'success', 'failure', 'metadata']))}
            className="text-xs font-medium text-muted-foreground hover:text-foreground"
            data-testid="timeline-clear-filters"
          >
            Clear
          </button>
        )}
      </div>

      {isLoading && entries.length === 0 && (
        <div className="space-y-2 py-4">
          {[1, 2, 3].map((i) => (
              <div key={i} className="flex gap-3">
              <div className="h-3 w-12 rounded bg-muted" />
              <div className="h-3 w-3 rounded-full bg-muted" />
              <div className="h-3 flex-1 rounded bg-muted" />
            </div>
          ))}
        </div>
      )}

      {!isLoading && entries.length === 0 && (
        <div className="py-6 text-center text-sm text-muted-foreground" data-testid="timeline-empty-state">
          No activity yet.
        </div>
      )}

      {entries.length > 0 && sorted.length === 0 && (
        <div className="py-6 text-center text-sm text-muted-foreground" data-testid="timeline-filtered-empty">
          No events match the selected filters.
        </div>
      )}

      <div className="space-y-6">
        {grouped.map((group) => (
          <div key={group.day}>
            <div className="sticky top-0 z-10 mb-2 bg-background/95 px-3 py-1 text-[10px] font-semibold uppercase tracking-wide text-muted-foreground backdrop-blur-sm">
              {group.day}
            </div>
            <div className="space-y-1">
              {group.events.map((entry) => (
                <EventTimelineRow key={entry.id} entry={entry} />
              ))}
            </div>
          </div>
        ))}
      </div>
    </div>
  )
}
