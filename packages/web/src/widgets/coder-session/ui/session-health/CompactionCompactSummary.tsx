import { formatCompact } from '@/shared/lib/format-compact'

/**
 * Minimal shape that `CompactionCompactSummary` needs from a compaction
 * event. Decoupled from the model's `CompactionEntry` so the component
 * is easy to feed from Fakes, transcript projections, or any other
 * source of compaction records without dragging the full `Round`/
 * `CompactionEntry` surface area along.
 */
export interface CompactCompaction {
  id: string
  strategy?: string | null
  contextWindowUsedBefore?: number | null
  contextWindowUsedAfter?: number | null
  recordedAt?: string | null
}

export interface CompactionCompactSummaryProps {
  entries: CompactCompaction[]
}

interface AggregateStats {
  count: number
  strategies: string[]
  totalBefore: number
  totalReduction: number
  totalReductionPercent: number
  hasCountableReduction: boolean
  firstAt: string | null
  lastAt: string | null
}

function computeAggregate(entries: CompactCompaction[]): AggregateStats {
  let totalBefore = 0
  let totalReduction = 0
  let countableReduction = 0
  const strategySet = new Set<string>()
  let firstAt: string | null = null
  let lastAt: string | null = null

  for (const entry of entries) {
    if (entry.strategy) strategySet.add(entry.strategy)

    const before = entry.contextWindowUsedBefore
    const after = entry.contextWindowUsedAfter
    if (typeof before === 'number' && typeof after === 'number' && after < before) {
      totalBefore += before
      const delta = before - after
      totalReduction += delta
      countableReduction += 1
    }

    const at = entry.recordedAt
    if (at) {
      if (firstAt == null || at < firstAt) firstAt = at
      if (lastAt == null || at > lastAt) lastAt = at
    }
  }

  const totalReductionPercent = totalBefore > 0
    ? Math.round((totalReduction / totalBefore) * 100)
    : 0

  return {
    count: entries.length,
    strategies: Array.from(strategySet),
    totalBefore,
    totalReduction,
    totalReductionPercent,
    hasCountableReduction: countableReduction > 0,
    firstAt,
    lastAt,
  }
}

function formatTime(iso: string | null | undefined): string {
  if (!iso) return ''
  try {
    return new Date(iso).toLocaleString()
  } catch {
    return iso
  }
}

function formatReductionLabel(reduction: number, percent: number, countable: boolean): string {
  if (!countable || reduction <= 0) return 'reduction unknown'
  return `reduced by ${formatCompact(reduction)} tokens (${percent}%)`
}

function formatStrategiesLabel(strategies: string[]): string {
  if (strategies.length === 0) return 'strategy unknown'
  if (strategies.length === 1) return strategies[0]
  return strategies.join(', ')
}

function formatTimesLabel(firstAt: string | null, lastAt: string | null): string {
  const first = formatTime(firstAt)
  const last = formatTime(lastAt)
  if (!first && !last) return ''
  if (first && last && first !== last) return `${first} → ${last}`
  return first || last
}

/**
 * One-line aggregate summary for a session's compaction events. Renders
 * nothing when there are zero events so a session without compactions
 * carries no extra decoration. The per-round `CompactionTimelineEntry`
 * remains the source of detail (before/after counts, summary) inside
 * expanded rounds — the compact summary is intentionally aggregate-only.
 */
export function CompactionCompactSummary({ entries }: CompactionCompactSummaryProps) {
  if (!entries || entries.length === 0) return null

  const stats = computeAggregate(entries)
  const countLabel = stats.count === 1 ? 'compaction' : 'compactions'
  const timesLabel = formatTimesLabel(stats.firstAt, stats.lastAt)
  const strategiesLabel = formatStrategiesLabel(stats.strategies)
  const reductionLabel = formatReductionLabel(
    stats.totalReduction,
    stats.totalReductionPercent,
    stats.hasCountableReduction,
  )

  const titleParts = [
    `${stats.count} ${countLabel}`,
    timesLabel,
    `strategies: ${strategiesLabel}`,
    reductionLabel,
  ].filter((part) => part && part.length > 0)

  const tooltip = titleParts.join(' · ')

  return (
    <div
      className="flex items-center gap-2 rounded-md border border-info-border bg-info-subtle/60 px-2.5 py-1.5 text-xs text-info"
      data-testid="compaction-compact-summary"
      data-compaction-count={stats.count}
      title={tooltip}
      aria-label={tooltip}
    >
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
      <span className="font-medium" data-testid="compaction-compact-summary-count">
        {stats.count} {countLabel}
      </span>
      {timesLabel && (
        <>
          <span className="text-info/60" aria-hidden="true">·</span>
          <span data-testid="compaction-compact-summary-times" className="text-info">
            {timesLabel}
          </span>
        </>
      )}
      <span className="text-info/60" aria-hidden="true">·</span>
      <span data-testid="compaction-compact-summary-strategies">
        {strategiesLabel}
      </span>
      {stats.hasCountableReduction && (
        <>
          <span className="text-info/60" aria-hidden="true">·</span>
          <span data-testid="compaction-compact-summary-reduction" className="text-info">
            reduced by {formatCompact(stats.totalReduction)} tokens ({stats.totalReductionPercent}%)
          </span>
        </>
      )}
      {!stats.hasCountableReduction && entries.length > 0 && (
        <>
          <span className="text-info/60" aria-hidden="true">·</span>
          <span data-testid="compaction-compact-summary-reduction" className="text-info">
            reduction unknown
          </span>
        </>
      )}
    </div>
  )
}
