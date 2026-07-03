import { useCompletionTrend } from '../../../entities/issue'
import type { CompletionBucketPoint } from '../../../entities/issue'
import type { InsightsRange } from '../model/insights-range'
import { ChartAccessibility, ChartContainer, LineSeries } from '../charts'
import type { LinePoint } from '../charts'

const VIEWBOX_WIDTH = 120
const VIEWBOX_HEIGHT = 40
const PADDING_X = 4
const PADDING_TOP = 4
const PADDING_BOTTOM = 6

interface SparklineGeometry {
  points: LinePoint[]
  plotWidth: number
  plotHeight: number
}

function buildGeometry(completedCounts: number[]): SparklineGeometry {
  const plotWidth = VIEWBOX_WIDTH - 2 * PADDING_X
  const plotHeight = VIEWBOX_HEIGHT - PADDING_TOP - PADDING_BOTTOM
  const baselineY = PADDING_TOP + plotHeight

  if (completedCounts.length === 0) {
    return { points: [], plotWidth, plotHeight }
  }

  const max = Math.max(...completedCounts, 0)
  const safeMax = max === 0 ? 1 : max
  const stepX = completedCounts.length > 1 ? plotWidth / (completedCounts.length - 1) : 0
  const startX = PADDING_X

  const points = completedCounts
    .map((value, index) => {
      const x = startX + index * stepX
      const y = baselineY - (value / safeMax) * plotHeight
      return { x: Number(x.toFixed(2)), y: Number(y.toFixed(2)) }
    })

  return { points, plotWidth, plotHeight }
}

interface SparklineProps {
  completedCounts: number[]
  summary: string
}

function Sparkline({ completedCounts, summary }: SparklineProps) {
  const { points, plotWidth, plotHeight } = buildGeometry(completedCounts)

  const baselineY = PADDING_TOP + plotHeight
  const baselineX2 = plotWidth + 2 * PADDING_X

  return (
    <ChartAccessibility
      ariaLabel={`Completion trend across ${completedCounts.length} weeks`}
      summary={summary}
      legend={[{ label: 'Completed issues', shape: 'line', className: 'stroke-chart-1' }]}
      viewBox={`0 0 ${VIEWBOX_WIDTH} ${VIEWBOX_HEIGHT}`}
      className="w-full h-16"
    >
      <line
        data-testid="productivity-trend-baseline"
        x1={0}
        y1={baselineY}
        x2={baselineX2}
        y2={baselineY}
        className="stroke-border"
        opacity="0.2"
        strokeWidth={1}
        strokeDasharray="2 2"
      />
      {points.length > 0 && (
        <LineSeries points={points} className="stroke-chart-1" markerClassName="fill-chart-1" />
      )}
    </ChartAccessibility>
  )
}

export function CompletionTrend({ range }: { range: InsightsRange }) {
  const { data, isLoading, isError } = useCompletionTrend(range)

  const buckets = data?.buckets ?? []
  const completedCounts = buckets.map((bucket) => bucket.completed)

  const hasNoData = buckets.length === 0
  const status = isLoading ? 'loading'
    : isError ? 'error'
    : hasNoData ? 'empty'
    : 'resolved'
  const totalCompleted = completedCounts.reduce((total, count) => total + count, 0)
  const peak = buckets.reduce<CompletionBucketPoint | null>(
    (current, bucket) => current === null || bucket.completed > current.completed ? bucket : current,
    null,
  )
  const summary = `Weekly completion trend from ${data?.window.from ?? 'unknown'} to ${data?.window.to ?? 'unknown'}. Total completed issues: ${totalCompleted}. Peak week: ${peak ? `${peak.boundary} ${peak.completed}` : 'N/A'}.`

  return (
    <section
      data-testid="productivity-trend"
      data-state={status === 'empty' ? 'empty' : undefined}
      aria-label="Completion trend"
      className="rounded-lg border border-border bg-card/50 p-4"
    >
      <div className="flex items-center justify-between mb-3">
        <h3 className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
          Completion trend
        </h3>
        {status === 'resolved' && (
          <span
            data-testid="productivity-trend-meta"
            className="text-xs text-muted-foreground tabular-nums"
          >
            {buckets.length} weeks
          </span>
        )}
      </div>
      <ChartContainer
        status={status}
        emptyAction={
          <p
            data-testid="productivity-trend-empty"
            className="text-sm text-muted-foreground"
          >
            No completion data yet - weekly completions appear once issues reach the done state.
          </p>
        }
      >
        <Sparkline completedCounts={completedCounts} summary={summary} />
      </ChartContainer>
    </section>
  )
}
