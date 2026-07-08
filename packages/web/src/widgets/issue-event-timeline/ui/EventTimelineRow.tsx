import { useState } from 'react'
import { ChevronDownIcon } from 'lucide-react'
import { CATEGORY_STYLES, type TimelineEntry } from '../model/types'
import { statusTreatment, type StatusTreatment } from '@/shared/status-presentation'

function formatTimelineTime(iso: string): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return ''
  return date.toLocaleTimeString(undefined, {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hour12: false,
  })
}

function formatTimelineDate(iso: string): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return ''
  return date.toLocaleDateString(undefined, {
    month: 'short',
    day: 'numeric',
  })
}

interface EventTimelineRowProps {
  entry: TimelineEntry
}

export function EventTimelineRow({ entry }: EventTimelineRowProps) {
  const [expanded, setExpanded] = useState(false)
  const style = CATEGORY_STYLES[entry.category]
  const hasDetail = entry.detail != null && entry.detail.length > 0
  const isAttention = entry.attention
  const isFailure = entry.category === 'failure'

  const canExpandDetail = hasDetail && (isFailure || isAttention)

  // Failure / attention markers carry meaning beyond the entry's
  // category badge — route them through the shared layer so the
  // timeline agrees with the dashboard / kanban / review-summary
  // surfaces (danger for failure, warning for attention).
  const markerTreatment: StatusTreatment | null = isFailure
    ? statusTreatment('severity', 'ERROR')
    : isAttention
      ? statusTreatment('severity', 'WARN')
      : null
  const markerClass = markerTreatment ? markerTreatment.dot : style.dot
  const markerRing = markerTreatment ? `ring-2 ring-offset-1 ${markerTreatment.border}` : ''

  return (
    <div
      data-testid="event-timeline-row"
      data-category={entry.category}
      data-source={entry.source}
      data-attention={isAttention}
      data-live={entry.isLive}
      data-family={markerTreatment?.family ?? 'muted'}
      className={`group relative flex gap-3 px-3 py-2.5 transition-colors`}
    >
      <div className="flex shrink-0 flex-col items-end pt-0.5">
        <span className="text-[10px] tabular-nums text-muted-foreground">
          {formatTimelineTime(entry.time)}
        </span>
        <span className="text-[9px] text-muted-foreground/70">
          {formatTimelineDate(entry.time)}
        </span>
      </div>

      <div className="relative flex shrink-0 flex-col items-center pt-2">
        <span
          data-testid={markerTreatment ? 'event-timeline-marker' : undefined}
          className={`inline-block h-2.5 w-2.5 rounded-full ${markerClass} ${markerRing}`}
        />
        <div className="mt-1 w-px flex-1 bg-border group-last:hidden" />
      </div>

      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-1.5">
          <span className="inline-flex items-center rounded border border-border bg-muted px-1.5 py-0.5 text-[10px] font-medium text-muted-foreground">
            {entry.source}
          </span>
          <span className="text-sm text-foreground">{entry.description}</span>
        </div>

        {canExpandDetail && (
          <button
            type="button"
            onClick={() => setExpanded(!expanded)}
            className="mt-1 inline-flex min-h-11 min-w-11 items-center gap-1 rounded-md px-2 text-xs text-muted-foreground hover:text-foreground sm:min-h-7 sm:min-w-0 sm:px-0"
            data-testid="event-detail-toggle"
          >
            <ChevronDownIcon
              className={`h-3.5 w-3.5 transition-transform ${expanded ? 'rotate-180' : ''}`}
            />
            {expanded ? 'Hide detail' : 'Show detail'}
          </button>
        )}

        {expanded && canExpandDetail && (
          <pre
            data-testid="event-detail"
            className="mt-2 max-h-40 overflow-auto rounded bg-muted px-3 py-2 text-xs font-mono text-foreground whitespace-pre-wrap break-all border border-border"
          >
            {entry.detail}
          </pre>
        )}
      </div>
    </div>
  )
}
