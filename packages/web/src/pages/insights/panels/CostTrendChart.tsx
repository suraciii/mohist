import { useAgentUsage } from '../../../entities/agent'
import type { AgentUsageTimeseriesDto } from '../../../entities/agent'
import {
  ChartContainer,
  ChartAccessibility,
  BarSeries,
  LineSeries,
  ChartAxes,
} from '../charts'
import type { AxisTick, LinePoint } from '../charts'

const SVG_WIDTH = 500
const SVG_HEIGHT = 300
const MARGIN = { top: 26, right: 55, left: 55, bottom: 35 }

const plotX = MARGIN.left
const plotWidth = SVG_WIDTH - MARGIN.left - MARGIN.right
const plotHeight = SVG_HEIGHT - MARGIN.top - MARGIN.bottom

function niceCeil(value: number): number {
  const magnitude = Math.pow(10, Math.floor(Math.log10(value)))
  const normalized = value / magnitude
  if (normalized <= 1) return magnitude
  if (normalized <= 2) return 2 * magnitude
  if (normalized <= 5) return 5 * magnitude
  return 10 * magnitude
}

function computeTicks(maxValue: number, axisHeight: number): AxisTick[] {
  if (maxValue <= 0) return [{ value: 0, y: MARGIN.top + axisHeight }]
  const step = niceCeil(maxValue / 4)
  const ticks: AxisTick[] = []
  for (let i = 0; i <= 4; i++) {
    const val = i * step
    if (val <= maxValue * 1.05) {
      ticks.push({
        value: val,
        y: MARGIN.top + axisHeight - (val / maxValue) * axisHeight,
      })
    }
  }
  return ticks
}

function formatLabel(dateStr: string): string {
  const d = new Date(dateStr)
  return d.toLocaleDateString('en-US', { month: 'short', day: 'numeric' })
}

function getCurrency(buckets: AgentUsageTimeseriesDto['buckets']): string | null {
  for (const b of buckets) {
    if (b.costCurrency) return b.costCurrency
  }
  return null
}

function hasUsageData(data: AgentUsageTimeseriesDto | undefined): data is AgentUsageTimeseriesDto {
  if (!data || data.buckets.length === 0) return false

  const hasBucketUsage = data.buckets.some((bucket) =>
    bucket.inputTokens > 0
    || bucket.outputTokens > 0
    || bucket.totalTokens > 0
    || bucket.costAmount > 0
    || bucket.costCurrency !== null,
  )

  const hasMeasuredCumulativeCost = (data.cumulativeCostPerShip ?? []).some((point) =>
    point.cumulativeCost !== null,
  )

  return hasBucketUsage || hasMeasuredCumulativeCost
}

export function CostTrendChart() {
  const { data, isLoading, isError } = useAgentUsage()

  const status = isLoading ? 'loading'
    : isError ? 'error'
    : !hasUsageData(data) ? 'empty'
    : 'resolved'

  return (
    <section data-testid="cost-trend-chart" aria-label="Cost Trend">
      <h3 className="text-xs font-semibold uppercase tracking-wide text-muted-foreground mb-3">
        Cost Trend
      </h3>
      <ChartContainer
        status={status}
        emptyAction={
          <p className="text-sm text-muted-foreground text-center">
            Cost and cost-per-ship appear once an agent session reports usage
            on this project.
          </p>
        }
      >
        {data && <ChartInner data={data} />}
      </ChartContainer>
    </section>
  )
}

function ChartInner({ data }: { data: AgentUsageTimeseriesDto }) {
  const { buckets, cumulativeCostPerShip } = data
  const barCount = buckets.length
  const barGap = 2
  const totalGap = barGap * (barCount - 1)
  const barWidth = barCount > 0 ? (plotWidth - totalGap) / barCount : 0

  const maxCost = Math.max(...buckets.map((b) => b.costAmount), 0) || 1

  const trendValues = (cumulativeCostPerShip ?? [])
    .map((p) => p.costPerShip)
    .filter((v): v is number => v != null)
  const hasTrend = trendValues.length > 0
  const maxTrend = hasTrend ? Math.max(...trendValues.map(Math.abs), 0) || 1 : 1

  const trendPoints: (LinePoint | null)[] = hasTrend
    ? cumulativeCostPerShip!.map((point, i) => {
        if (point.costPerShip == null) return null
        const x = plotX + i * (barWidth + barGap) + barWidth / 2
        const y = MARGIN.top + plotHeight - (point.costPerShip / maxTrend) * plotHeight
        return { x, y }
      })
    : []

  const leftTicks = computeTicks(maxCost, plotHeight)
  const rightTicks = hasTrend ? computeTicks(maxTrend, plotHeight) : []

  const totalCost = buckets.reduce((s, b) => s + b.costAmount, 0)
  const peakBucket = [...buckets].sort((a, b) => b.costAmount - a.costAmount)[0]
  const firstTrend = hasTrend
    ? cumulativeCostPerShip!.find((p) => p.costPerShip != null)
    : null
  const lastTrend = hasTrend
    ? [...cumulativeCostPerShip!].reverse().find((p) => p.costPerShip != null)
    : null

  const firstBucket = buckets[0]
  const lastBucket = buckets[buckets.length - 1]

  const summary =
    `Daily cost bar chart from ${firstBucket ? formatLabel(firstBucket.bucketStart) : formatLabel(data.rangeFrom)} to ${lastBucket ? formatLabel(lastBucket.bucketStart) : formatLabel(data.rangeTo)}. ` +
    `Total window cost: ${totalCost.toFixed(2)}. ` +
    `Peak day: ${peakBucket ? `${formatLabel(peakBucket.bucketStart)} ${peakBucket.costAmount.toFixed(2)}` : 'N/A'}.` +
    (hasTrend && firstTrend && lastTrend
      ? ` Cost per ship from ${firstTrend.costPerShip!.toFixed(2)} to ${lastTrend.costPerShip!.toFixed(2)}.`
      : '')

  const legend = [
    { label: 'Daily cost', shape: 'bar' as const, className: 'fill-chart-2' },
    ...(hasTrend
      ? [{ label: 'Cost per ship', shape: 'line' as const, className: 'stroke-chart-5' }]
      : []),
  ]

  const currency = getCurrency(buckets)

  return (
    <ChartAccessibility
      ariaLabel={`Cost trend for project: daily cost bar chart${hasTrend ? ' with cost-per-ship trend overlay' : ''}`}
      summary={summary}
      legend={legend}
      viewBox={`0 0 ${SVG_WIDTH} ${SVG_HEIGHT}`}
      className="w-full h-auto"
    >
      <text
        x={plotX}
        y={MARGIN.top - 6}
        textAnchor="middle"
        className="fill-chart-2 text-[9px] tabular-nums font-medium"
      >
        Daily cost{currency ? ` (${currency})` : ''}
      </text>

      <text
        x={plotX + plotWidth}
        y={MARGIN.top - 6}
        textAnchor="middle"
        className="fill-chart-5 text-[9px] tabular-nums font-medium"
      >
        Cost per ship{currency ? ` (${currency}/issue)` : ''}
      </text>

      {buckets.map((bucket, i) => {
        const x = plotX + i * (barWidth + barGap) + barWidth / 2
        return (
          <text
            key={`label-${i}`}
            x={x}
            y={MARGIN.top + plotHeight + 18}
            textAnchor="middle"
            className="fill-muted-foreground text-[10px] tabular-nums"
          >
            {formatLabel(bucket.bucketStart)}
          </text>
        )
      })}

      <ChartAxes
        side="left"
        ticks={leftTicks}
        plotX={plotX}
        plotY={MARGIN.top}
        plotWidth={plotWidth}
        plotHeight={plotHeight}
        axisClassName="stroke-chart-2"
        labelClassName="fill-chart-2 text-[10px] tabular-nums"
      />

      {hasTrend && (
        <ChartAxes
          side="right"
          ticks={rightTicks}
          plotX={plotX}
          plotY={MARGIN.top}
          plotWidth={plotWidth}
          plotHeight={plotHeight}
          axisClassName="stroke-chart-5"
          labelClassName="fill-chart-5 text-[10px] tabular-nums"
        />
      )}

      <BarSeries
        data={buckets.map((b) => ({
          value: b.costAmount,
          label: formatLabel(b.bucketStart),
        }))}
        plotX={plotX}
        plotY={MARGIN.top}
        plotWidth={plotWidth}
        plotHeight={plotHeight}
        barGap={barGap}
        className="fill-chart-2"
      />

      {hasTrend && (
        <LineSeries
          points={trendPoints}
          className="stroke-chart-5"
          markerClassName="fill-chart-5"
        />
      )}
    </ChartAccessibility>
  )
}
