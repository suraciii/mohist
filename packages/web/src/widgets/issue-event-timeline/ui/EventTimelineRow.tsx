import { useState } from 'react'
import { ChevronDownIcon } from 'lucide-react'
import { CATEGORY_STYLES, type TimelineEntry } from '../model/types'

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

  const attentionBg = isFailure
    ? 'bg-red-50/80'
    : isAttention
      ? 'bg-amber-50/60'
      : 'bg-transparent'

  return (
    <div
      data-testid="event-timeline-row"
      data-category={entry.category}
      data-source={entry.source}
      data-attention={isAttention}
      data-live={entry.isLive}
      className={`group relative flex gap-3 px-3 py-2.5 transition-colors ${
        entry.isLive ? 'animate-in slide-in-from-top-2 fade-in duration-300' : ''
      } ${attentionBg} ${isAttention ? 'rounded-md' : ''}`}
    >
      <div className="flex shrink-0 flex-col items-end pt-0.5">
        <span className="text-[10px] tabular-nums text-gray-500">
          {formatTimelineTime(entry.time)}
        </span>
        <span className="text-[9px] text-gray-400">
          {formatTimelineDate(entry.time)}
        </span>
      </div>

      <div className="relative flex shrink-0 flex-col items-center pt-2">
        <span
          className={`inline-block h-2.5 w-2.5 rounded-full ${style.dot} ${
            isAttention ? 'ring-2 ring-offset-1 ' + style.border.replace('border-', 'ring-') : ''
          }`}
        />
        <div className="mt-1 w-px flex-1 bg-gray-100 group-last:hidden" />
      </div>

      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-1.5">
          <span
            className={`inline-flex items-center rounded border px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide ${style.bg} ${style.text} ${style.border}`}
          >
            {style.label}
          </span>
          <span className="inline-flex items-center rounded border border-gray-200 bg-gray-50 px-1.5 py-0.5 text-[10px] font-medium text-gray-600">
            {entry.source}
          </span>
          <span className="text-sm text-gray-800">{entry.description}</span>
        </div>

        {canExpandDetail && (
          <button
            type="button"
            onClick={() => setExpanded(!expanded)}
            className="mt-1 inline-flex items-center gap-0.5 text-xs text-gray-500 hover:text-gray-700"
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
            className="mt-2 max-h-40 overflow-auto rounded bg-gray-900 px-3 py-2 text-xs font-mono text-gray-100 whitespace-pre-wrap break-all"
          >
            {entry.detail}
          </pre>
        )}
      </div>
    </div>
  )
}
