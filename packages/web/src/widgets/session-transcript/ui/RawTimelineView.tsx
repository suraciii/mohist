import type { TimelineFact } from '@/entities/session'

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

export interface RawTimelineViewProps {
  facts: TimelineFact[]
}

export function RawTimelineView({ facts }: RawTimelineViewProps) {
  return (
    <div className="space-y-2" data-testid="raw-timeline-view" role="list">
      {facts.map((fact) => (
        <article
          key={fact.sourceId}
          className="rounded-md border border-border bg-background px-3 py-2.5"
          data-testid="raw-timeline-row"
          data-timeline-source-id={fact.sourceId}
          data-timeline-source-kind={fact.kind}
          data-timeline-source={fact.source}
          role="listitem"
        >
          <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1 text-xs">
            <span className="text-muted-foreground" data-testid="raw-timeline-source">
              {fact.source}
            </span>
            <span className="font-medium text-foreground">{fact.kind}</span>
            <span className="font-mono text-muted-foreground">{fact.sourceId}</span>
            <time className="tabular-nums text-muted-foreground" dateTime={fact.occurredAt}>
              {formatOccurredAt(fact.occurredAt)}
            </time>
          </div>

          <details className="mt-1.5 text-xs" data-testid="raw-timeline-payload-details">
            <summary className="cursor-pointer text-muted-foreground hover:text-foreground">
              Show payload
            </summary>
            <pre className="mt-2 max-h-80 overflow-auto whitespace-pre-wrap break-words rounded border border-border bg-muted/30 px-2.5 py-2 text-foreground">
              {formatValue(fact.raw)}
            </pre>
          </details>
        </article>
      ))}
    </div>
  )
}
