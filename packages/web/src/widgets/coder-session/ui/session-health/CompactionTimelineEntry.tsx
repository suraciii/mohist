import { useState } from 'react'
import { Button } from '@/shared/ui/components/button'
import { formatCompact } from '@/shared/lib/format-compact'

export interface CompactionTimelineEntryData {
  id: string
  strategy?: string | null
  contextWindowUsedBefore?: number | null
  contextWindowUsedAfter?: number | null
  contextWindowSize?: number | null
  summary?: string | null
  recordedAt?: string | null
}

export interface CompactionTimelineEntryProps {
  entry: CompactionTimelineEntryData
}

function formatTokens(value: number | null | undefined): string {
  if (value == null) return 'unknown'
  return `${formatCompact(value as number)} tokens`
}

function formatReduction(before: number | null | undefined, after: number | null | undefined): string {
  if (before == null || after == null) return ''
  if (after >= before) {
    return 'No reduction recorded'
  }
  const delta = before - after
  const pct = before > 0 ? Math.round((delta / before) * 100) : 0
  return `Reduced by ${formatCompact(delta)} tokens (${pct}%)`
}

function formatTime(iso: string | null | undefined): string {
  if (!iso) return ''
  try {
    return new Date(iso).toLocaleString()
  } catch {
    return iso
  }
}

/**
 * Renders a single context-compaction event as an info-style banner
 * inside the SessionTimeline. Shows the time, strategy, and the
 * before/after token reduction so users can see at a glance how much
 * headroom was recovered. The optional summary from the runner is
 * collapsed by default to keep the timeline readable.
 */
export function CompactionTimelineEntry({ entry }: CompactionTimelineEntryProps) {
  const [expanded, setExpanded] = useState(false)
  const before = entry.contextWindowUsedBefore
  const after = entry.contextWindowUsedAfter
  const strategy = entry.strategy ?? 'summary'
  const reduction = formatReduction(before, after)
  const summary = entry.summary?.trim() ?? ''
  const time = formatTime(entry.recordedAt)

  return (
    <div
      className="flex gap-2"
      data-testid="compaction-timeline-entry"
      data-strategy={strategy}
    >
      <div className="flex flex-col items-center shrink-0 pt-0.5">
        <span
          className="inline-block h-3.5 w-3.5 rounded-full border-2 border-info-border bg-info-subtle"
          aria-hidden="true"
        />
        <div className="w-px flex-1 bg-info-border mt-1" />
      </div>
      <div className="flex-1 min-w-0 pb-3">
        <div className="flex items-center gap-2 rounded-md border border-info-border bg-info-subtle/60 px-2.5 py-1.5 text-xs text-info">
          <svg
            className="h-3.5 w-3.5 shrink-0 text-info"
            viewBox="0 0 20 20"
            fill="currentColor"
            aria-hidden="true"
          >
            <path
              fillRule="evenodd"
              d="M10 18a8 8 0 100-16 8 8 0 000 16zm.75-11.25a.75.75 0 00-1.5 0v3.5h-3.5a.75.75 0 000 1.5h4.25a.75.75 0 00.75-.75V6.75z"
              clipRule="evenodd"
            />
          </svg>
          <span className="font-medium" data-testid="compaction-timeline-title">
            Context compacted ({strategy})
          </span>
          {time && <span className="text-info/80">· {time}</span>}
        </div>
        <div className="mt-1.5 ml-1 space-y-1 text-xs text-foreground">
          <div className="flex flex-wrap items-center gap-x-2 gap-y-0.5" data-testid="compaction-timeline-counts">
            <span>
              <span className="text-muted-foreground">Before:</span>{' '}
              <span className="font-mono">{formatTokens(before)}</span>
            </span>
            <span className="text-muted-foreground/60">→</span>
            <span>
              <span className="text-muted-foreground">After:</span>{' '}
              <span className="font-mono">{formatTokens(after)}</span>
            </span>
            {reduction && <span className="text-muted-foreground">· {reduction}</span>}
          </div>
          {summary && (
            <div>
              <Button
                variant="link"
                size="sm"
                onClick={() => setExpanded((v) => !v)}
                className="h-auto p-0 text-[11px] text-info hover:text-info"
              >
                {expanded ? 'Hide summary' : 'Show summary'}
              </Button>
              {expanded && (
                <pre className="mt-1 whitespace-pre-wrap break-all rounded bg-info-subtle/40 p-2 text-[11px] text-foreground max-h-40 overflow-auto">
                  {summary}
                </pre>
              )}
            </div>
          )}
        </div>
      </div>
    </div>
  )
}
