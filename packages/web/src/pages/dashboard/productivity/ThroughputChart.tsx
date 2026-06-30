import { useCompletionThroughput } from '../../../entities/issue'
import type { CompletionTrendResponse } from '../../../entities/issue'
import { computeMovingAverage } from './model/throughput'
import {
  ChartContainer,
  ChartAccessibility,
  SegmentedBarSeries,
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

const LABEL_INTERVAL = 5

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
  const calendarDate = /^(\d{4})-(\d{2})-(\d{2})/.exec(dateStr)
  const d = calendarDate
    ? new Date(Number(calendarDate[1]), Number(calendarDate[2]) - 1, Number(calendarDate[3]))
    : new Date(dateStr)
  return d.toLocaleDateString('en-US', { month: 'short', day: 'numeric' })
}

function hasThroughputData(data: CompletionTrendResponse | undefined): data is CompletionTrendResponse {
  if (!data || data.buckets.length === 0) return false
  return data.buckets.some(b => b.completed > 0 || b.failed > 0)
}

export function ThroughputChart() {
  const { data, isLoading, isError } = useCompletionThroughput()

  const status = isLoading ? 'loading'
    : isError ? 'error'
    : !hasThroughputData(data) ? 'empty'
    : 'resolved'

  return (
    <section data-testid="throughput-chart" aria-label="Throughput">
      <h3 className="text-xs font-semibold uppercase tracking-wide text-muted-foreground mb-3">
        Throughput
      </h3>
      <ChartContainer
        status={status}
        emptyAction={
          <p className="text-sm text-muted-foreground text-center">
            Throughput appears once an issue completes on this project.
          </p>
        }
      >
        {data && <ChartInner data={data} />}
      </ChartContainer>
    </section>
  )
}

function ChartInner({ data }: { data: CompletionTrendResponse }) {
  const { buckets, window: win } = data
  const barCount = buckets.length
  const barGap = 2
  const totalGap = barGap * (barCount - 1)
  const barWidth = barCount > 0 ? (plotWidth - totalGap) / barCount : 0

  const completedCounts = buckets.map(b => b.completed)

  const axisMax = Math.max(
    ...buckets.map(b => Math.max(b.completed, b.failed)),
    0,
  ) || 1

  const maValues = computeMovingAverage(completedCounts, 7)

  const maPoints: LinePoint[] = maValues.map((value, i) => {
    const x = plotX + i * (barWidth + barGap) + barWidth / 2
    const y = MARGIN.top + plotHeight - (value / axisMax) * plotHeight
    return { x, y }
  })

  const leftTicks = computeTicks(axisMax, plotHeight)

  const totalCompleted = completedCounts.reduce((s, v) => s + v, 0)
  const avgCompleted = barCount > 0 ? totalCompleted / barCount : 0
  const peakBucket = [...buckets].sort((a, b) => b.completed - a.completed)[0]

  const firstBucket = buckets[0]
  const lastBucket = buckets[buckets.length - 1]

  const summary =
    `Daily throughput bar chart from ${firstBucket ? formatLabel(firstBucket.boundary) : formatLabel(win.from)} to ${lastBucket ? formatLabel(lastBucket.boundary) : formatLabel(win.to)}. ` +
    `Total completed: ${totalCompleted}. ` +
    `Average completed per day: ${avgCompleted.toFixed(1)}. ` +
    `Peak day: ${peakBucket ? `${formatLabel(peakBucket.boundary)} ${peakBucket.completed}` : 'N/A'}.`

  const legend = [
    { label: 'Completed', shape: 'bar' as const, className: 'fill-chart-2' },
    { label: 'Failed', shape: 'bar' as const, className: 'fill-chart-4' },
    { label: '7-day average', shape: 'line' as const, className: 'stroke-chart-5' },
  ]

  return (
    <ChartAccessibility
      ariaLabel="Throughput trend for project: daily delivery bar chart with 7-day moving average overlay"
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
        Issues per day
      </text>

      {buckets.map((bucket, i) => {
        if (i % LABEL_INTERVAL !== 0 && i !== barCount - 1) return null
        const x = plotX + i * (barWidth + barGap) + barWidth / 2
        return (
          <text
            key={`label-${i}`}
            x={x}
            y={MARGIN.top + plotHeight + 18}
            textAnchor="middle"
            className="fill-muted-foreground text-[10px] tabular-nums"
          >
            {formatLabel(bucket.boundary)}
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

      <SegmentedBarSeries
        data={buckets.map(b => ({
          label: formatLabel(b.boundary),
          segments: [
            { value: b.completed, fill: 'fill-chart-2' },
            {
              value: b.failed,
              fill: 'fill-chart-4',
              widthRatio: b.completed > 0 ? 0.6 : undefined,
            },
          ],
        }))}
        max={axisMax}
        plotX={plotX}
        plotY={MARGIN.top}
        plotWidth={plotWidth}
        plotHeight={plotHeight}
        barGap={barGap}
      />

      <LineSeries
        points={maPoints}
        className="stroke-chart-5"
        markerClassName="fill-chart-5"
      />
    </ChartAccessibility>
  )
}
