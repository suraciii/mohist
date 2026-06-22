import { useMemo } from 'react'
import { useCompletionTrend } from '../../../entities/issue'

const VIEWBOX_WIDTH = 120
const VIEWBOX_HEIGHT = 40
const PADDING_X = 4
const PADDING_TOP = 4
const PADDING_BOTTOM = 6

interface SparklineGeometry {
  points: string
  plotWidth: number
  plotHeight: number
}

function buildGeometry(completedCounts: number[]): SparklineGeometry {
  const plotWidth = VIEWBOX_WIDTH - 2 * PADDING_X
  const plotHeight = VIEWBOX_HEIGHT - PADDING_TOP - PADDING_BOTTOM
  const baselineY = PADDING_TOP + plotHeight

  if (completedCounts.length === 0) {
    return { points: '', plotWidth, plotHeight }
  }

  const max = Math.max(...completedCounts, 0)
  const safeMax = max === 0 ? 1 : max
  const stepX = completedCounts.length > 1 ? plotWidth / (completedCounts.length - 1) : 0
  const startX = PADDING_X

  const points = completedCounts
    .map((value, index) => {
      const x = startX + index * stepX
      const y = baselineY - (value / safeMax) * plotHeight
      return `${x.toFixed(2)},${y.toFixed(2)}`
    })
    .join(' ')

  return { points, plotWidth, plotHeight }
}

interface SparklineProps {
  completedCounts: number[]
}

function Sparkline({ completedCounts }: SparklineProps) {
  const { points, plotWidth, plotHeight } = useMemo(
    () => buildGeometry(completedCounts),
    [completedCounts],
  )

  const baselineY = PADDING_TOP + plotHeight
  const baselineX2 = plotWidth + 2 * PADDING_X

  return (
    <svg
      data-testid="productivity-trend-sparkline"
      viewBox={`0 0 ${VIEWBOX_WIDTH} ${VIEWBOX_HEIGHT}`}
      preserveAspectRatio="none"
      role="img"
      aria-label={`Completion trend across ${completedCounts.length} weeks`}
      className="w-full h-16"
    >
      <line
        data-testid="productivity-trend-baseline"
        x1={0}
        y1={baselineY}
        x2={baselineX2}
        y2={baselineY}
        stroke="currentColor"
        strokeOpacity="0.2"
        strokeWidth={1}
        strokeDasharray="2 2"
      />
      {points && (
        <polyline
          data-testid="productivity-trend-polyline"
          points={points}
          fill="none"
          stroke="currentColor"
          strokeWidth={1.5}
          strokeLinejoin="round"
          strokeLinecap="round"
        />
      )}
    </svg>
  )
}

export function CompletionTrend() {
  const { data } = useCompletionTrend()

  const buckets = data?.buckets ?? []
  const completedCounts = buckets.map((bucket) => bucket.completed)

  const hasNoData = buckets.length === 0

  if (hasNoData) {
    return (
      <section
        data-testid="productivity-trend"
        data-state="empty"
        aria-label="Completion trend"
        className="rounded-lg border border-border bg-card/50 p-4"
      >
        <div className="flex items-center justify-between mb-3">
          <h3 className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
            Completion trend
          </h3>
        </div>
        <p
          data-testid="productivity-trend-empty"
          className="text-sm text-muted-foreground"
        >
          No completion data yet — weekly completions appear once issues reach the done state.
        </p>
      </section>
    )
  }

  return (
    <section
      data-testid="productivity-trend"
      aria-label="Completion trend"
      className="rounded-lg border border-border bg-card/50 p-4"
    >
      <div className="flex items-center justify-between mb-3">
        <h3 className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
          Completion trend
        </h3>
        <span
          data-testid="productivity-trend-meta"
          className="text-xs text-muted-foreground tabular-nums"
        >
          {buckets.length} weeks
        </span>
      </div>
      <div className="text-blue-600">
        <Sparkline completedCounts={completedCounts} />
      </div>
    </section>
  )
}
