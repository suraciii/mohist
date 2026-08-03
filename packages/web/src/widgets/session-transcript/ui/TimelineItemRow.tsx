import { useState } from 'react'
import { ChevronDownIcon } from 'lucide-react'
import { Link } from 'react-router-dom'
import type { TimelineDetail, TimelineItem, TimelineReference, TimelineRenderClass } from '@/entities/session'

export type TimelineReferenceResolver = (reference: TimelineReference) => string | null | undefined

const ITEM_STYLES: Record<TimelineRenderClass, string> = {
  input: 'border-info-border bg-info-subtle',
  message: 'border-border bg-background',
  reasoning: 'border-border/60 bg-muted/20 text-muted-foreground',
  'file-read': 'border-border/60 bg-background',
  'file-edit': 'border-success/30 bg-success-subtle',
  shell: 'border-border/60 bg-background',
  'domain-action': 'border-info-border bg-info-subtle',
  plan: 'border-border bg-background',
  tool: 'border-border/60 bg-background text-muted-foreground',
  status: 'border-border/50 bg-muted/30 text-muted-foreground',
  boundary: 'border-warning-border bg-warning-subtle',
  error: 'border-danger-border bg-danger-subtle text-danger',
  suppressed: 'border-border/50 bg-muted/20 text-muted-foreground',
}

const MARKER_STYLES: Record<TimelineRenderClass, string> = {
  input: 'bg-info',
  message: 'bg-foreground',
  reasoning: 'bg-muted-foreground/50',
  'file-read': 'bg-muted-foreground/40',
  'file-edit': 'bg-success',
  shell: 'bg-muted-foreground/60',
  'domain-action': 'bg-info',
  plan: 'bg-foreground/70',
  tool: 'bg-muted-foreground/40',
  status: 'bg-muted-foreground/30',
  boundary: 'bg-warning',
  error: 'bg-danger',
  suppressed: 'bg-muted-foreground/30',
}

function formatValue(value: unknown): string {
  if (typeof value === 'string') return value
  try {
    const json = JSON.stringify(value, null, 2)
    return json ?? String(value)
  } catch {
    return String(value)
  }
}

function formatOccurredAt(occurredAt: string): string {
  const date = new Date(occurredAt)
  return Number.isNaN(date.getTime()) ? occurredAt : date.toISOString()
}

function sourceIdsFor(item: TimelineItem): string[] {
  return item.sourceIds.length > 0 ? item.sourceIds : [item.id]
}

function SourceAnchors({ sourceIds }: { sourceIds: string[] }) {
  return (
    <>
      {sourceIds.slice(1).map((sourceId) => (
        <span
          key={sourceId}
          aria-hidden="true"
          className="sr-only"
          data-timeline-source-id={sourceId}
        />
      ))}
    </>
  )
}

function DetailValue({ label, value }: { label: string; value: unknown }) {
  return (
    <div className="space-y-1">
      <div className="text-[11px] font-medium text-muted-foreground">{label}</div>
      <pre className="max-h-72 overflow-auto whitespace-pre-wrap break-words rounded border border-border bg-background px-2.5 py-2 text-xs text-foreground">
        {formatValue(value)}
      </pre>
    </div>
  )
}

function TimelineItemDetail({ detail }: { detail: TimelineDetail }) {
  return (
    <div className="space-y-2 border-t border-border px-3 py-2">
      {detail.input !== undefined && <DetailValue label="Input" value={detail.input} />}
      {detail.output !== undefined && <DetailValue label="Output" value={detail.output} />}
      {detail.diff !== undefined && <DetailValue label="Diff" value={detail.diff} />}
      {detail.error !== undefined && <DetailValue label="Error" value={detail.error} />}
      <DetailValue label="Raw" value={detail.raw} />
    </div>
  )
}

export interface TimelineItemRowProps {
  item: TimelineItem
  resolveReference?: TimelineReferenceResolver
}

export function TimelineItemRow({ item, resolveReference }: TimelineItemRowProps) {
  const [isDetailsOpen, setIsDetailsOpen] = useState(false)
  const sourceIds = sourceIdsFor(item)
  const referenceTarget = item.reference && resolveReference?.(item.reference)
  const hasDetail = item.detail !== undefined

  return (
    <article
      className={`relative flex gap-3 rounded-md border px-3 py-2.5 ${ITEM_STYLES[item.renderClass]} ${item.renderClass === 'error' ? 'border-l-4' : ''}`}
      data-testid="timeline-item-row"
      data-timeline-item-id={item.id}
      data-timeline-render-class={item.renderClass}
      data-timeline-salience={item.salience}
      data-timeline-source-id={sourceIds[0]}
    >
      <SourceAnchors sourceIds={sourceIds} />
      <span
        aria-hidden="true"
        className={`mt-1.5 h-2 w-2 shrink-0 rounded-full ${MARKER_STYLES[item.renderClass]}`}
        data-testid="timeline-item-marker"
      />

      <div className="min-w-0 flex-1">
        <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1">
          <time className="shrink-0 text-[10px] tabular-nums text-muted-foreground" dateTime={item.occurredAt}>
            {formatOccurredAt(item.occurredAt)}
          </time>
          <span className="min-w-0 text-sm font-medium leading-relaxed">
            {referenceTarget ? (
              <Link className="hover:underline" to={referenceTarget}>
                {item.summary}
              </Link>
            ) : (
              item.summary
            )}
          </span>
        </div>

        {hasDetail && (
          <details
            className="mt-1.5 text-xs"
            onToggle={(event) => setIsDetailsOpen(event.currentTarget.open)}
            data-testid="timeline-item-details"
          >
            <summary className="inline-flex cursor-pointer list-none items-center gap-1 text-muted-foreground hover:text-foreground [&::-webkit-details-marker]:hidden">
              <ChevronDownIcon
                aria-hidden="true"
                className={`size-3.5 transition-transform ${isDetailsOpen ? 'rotate-180' : ''}`}
              />
              Show details
            </summary>
            <TimelineItemDetail detail={item.detail!} />
          </details>
        )}
      </div>
    </article>
  )
}
