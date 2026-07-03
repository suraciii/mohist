import { useState } from 'react'
import { useQualityMetrics } from '../../../entities/issue'
import type { QualityTrendDto, QualityTrendPointDto } from '../../../entities/issue'
import type { InsightsRange } from '../model/insights-range'
import {
  ChartContainer,
  ChartAccessibility,
  ChartAxes,
  LineSeries,
} from '../charts'
import type { AxisTick, LegendEntry, LinePoint } from '../charts'

const SVG_WIDTH = 500
const SVG_HEIGHT = 300
const MARGIN = { top: 26, right: 24, left: 50, bottom: 35 }

const plotX = MARGIN.left
const plotWidth = SVG_WIDTH - MARGIN.left - MARGIN.right
const plotHeight = SVG_HEIGHT - MARGIN.top - MARGIN.bottom

const LABEL_INTERVAL = 5
const TICK_VALUES = [0, 0.25, 0.5, 0.75, 1] as const

function formatBoundaryLabel(boundary: string): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(boundary)
  const d = match
    ? new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]))
    : new Date(boundary)
  return d.toLocaleDateString('en-US', { month: 'short', day: 'numeric' })
}

function buildPercentTicks(): AxisTick[] {
  return TICK_VALUES.map((value) => ({
    value,
    y: MARGIN.top + plotHeight - value * plotHeight,
  }))
}

function tickY(value: number): number {
  return MARGIN.top + plotHeight - value * plotHeight
}

function percentPoints(
  points: QualityTrendPointDto[],
  bucketCount: number,
  pickRate: (point: QualityTrendPointDto) => number | null,
): (LinePoint | null)[] {
  const slotWidth = bucketCount > 0 ? plotWidth / bucketCount : 0
  return points.map((point, i) => {
    const rate = pickRate(point)
    if (rate === null) return null
    const x = plotX + i * slotWidth + slotWidth / 2
    const y = tickY(rate)
    return { x, y }
  })
}

function formatPercent(value: number): string {
  return `${Math.round(value * 100)}%`
}

function hasTrendData(data: { trend?: QualityTrendDto } | undefined): boolean {
  const trend = data?.trend
  if (!trend || trend.points.length === 0) return false
  return trend.points.some((p) => p.sampleCount > 0)
}

export function FtrTrendChart({ range }: { range: InsightsRange }) {
  const { data, isLoading, isError } = useQualityMetrics(range)
  const [showRework, setShowRework] = useState(false)

  const trend = data?.trend
  const bucketCount = trend?.points.length ?? 0
  const anyReworkData = !!trend?.points.some((p) => p.reworkRate !== null)

  const status = isLoading
    ? 'loading'
    : isError
      ? 'error'
      : !hasTrendData(data)
        ? 'empty'
        : 'resolved'

  return (
    <section
      data-testid="ftr-trend-chart"
      aria-label="First-Time-Right Trend"
    >
      <div className="flex items-center justify-between mb-3 gap-2">
        <h3 className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
          First-Time-Right Trend
        </h3>
        <div className="flex items-center gap-2 text-xs text-muted-foreground">
          {trend && bucketCount > 0 && (
            <span
              data-testid="ftr-trend-chart-window"
              className="inline-flex items-center rounded-md border border-border bg-muted/40 px-2 py-0.5 tabular-nums"
            >
              {formatBoundaryLabel(trend.from)} – {formatBoundaryLabel(trend.to)}
            </span>
          )}
          <label
            className="flex items-center gap-1.5 cursor-pointer select-none tabular-nums"
            data-testid="ftr-trend-overlay-toggle"
          >
            <input
              type="checkbox"
              className="h-3.5 w-3.5 cursor-pointer accent-chart-4"
              checked={showRework}
              disabled={!anyReworkData}
              onChange={(e) => setShowRework(e.target.checked)}
              aria-label="Toggle rework rate overlay"
            />
            <span>Rework overlay</span>
          </label>
        </div>
      </div>
      <ChartContainer
        status={status}
        emptyAction={
          <p className="text-sm text-muted-foreground text-center">
            First-time-right trend appears once an issue ships within the trailing window.
          </p>
        }
      >
        {trend && bucketCount > 0 && (
          <ChartInner trend={trend} showRework={showRework} anyReworkData={anyReworkData} />
        )}
      </ChartContainer>
    </section>
  )
}

interface ChartInnerProps {
  trend: QualityTrendDto
  showRework: boolean
  anyReworkData: boolean
}

function ChartInner({ trend, showRework, anyReworkData }: ChartInnerProps) {
  const { points, from, to } = trend
  const bucketCount = points.length
  const slotWidth = bucketCount > 0 ? plotWidth / bucketCount : 0

  const ftrPoints = percentPoints(points, bucketCount, (p) => p.firstTimeRightRate)
  const reworkPoints = showRework
    ? percentPoints(points, bucketCount, (p) => p.reworkRate)
    : []

  const ticks = buildPercentTicks()
  const renderRework = showRework && anyReworkData && reworkPoints.some((p) => p !== null)

  const firstFtr = points.find((p) => p.firstTimeRightRate !== null)
  const lastFtr = [...points].reverse().find((p) => p.firstTimeRightRate !== null)
  const peakRework = points.reduce<QualityTrendPointDto | null>((acc, p) => {
    if (p.reworkRate === null) return acc
    if (!acc || (acc.reworkRate ?? 0) < p.reworkRate) return p
    return acc
  }, null)

  const summary =
    `First-time-right trend line from ${formatBoundaryLabel(points[0]?.boundary ?? from)} to ${formatBoundaryLabel(points[points.length - 1]?.boundary ?? to)} (${points.length} trailing days). ` +
    `Buckets plotted: ${points.filter((p) => p.firstTimeRightRate !== null).length}. ` +
    (firstFtr && lastFtr
      ? `First-time-right from ${formatPercent(firstFtr.firstTimeRightRate!)} to ${formatPercent(lastFtr.firstTimeRightRate!)}. `
      : '') +
    (anyReworkData && peakRework
      ? `Peak rework day: ${formatBoundaryLabel(peakRework.boundary)} at ${formatPercent(peakRework.reworkRate!)}.`
      : '')

  const legend: LegendEntry[] = renderRework
    ? [
        { label: 'First-time-right', shape: 'line' as const, className: 'stroke-chart-5' },
        { label: 'Rework rate', shape: 'dashedLine' as const, className: 'stroke-chart-4' },
      ]
    : [{ label: 'First-time-right', shape: 'line' as const, className: 'stroke-chart-5' }]

  return (
    <div>
      <ChartAccessibility
        ariaLabel={`First-time-right trend for project: ${renderRework ? 'daily FTR line with rework overlay' : 'daily FTR line'}`}
        summary={summary}
        legend={legend}
        viewBox={`0 0 ${SVG_WIDTH} ${SVG_HEIGHT}`}
        className="w-full h-auto"
      >
        <text
          x={plotX}
          y={MARGIN.top - 6}
          textAnchor="start"
          className="fill-muted-foreground text-[9px] tabular-nums font-medium"
        >
          First-time-right %
        </text>

        {points.map((point, i) => {
          if (i % LABEL_INTERVAL !== 0 && i !== bucketCount - 1) return null
          const x = plotX + i * slotWidth + slotWidth / 2
          return (
            <text
              key={`label-${i}`}
              x={x}
              y={MARGIN.top + plotHeight + 18}
              textAnchor="middle"
              className="fill-muted-foreground text-[10px] tabular-nums"
            >
              {formatBoundaryLabel(point.boundary)}
            </text>
          )
        })}

        <ChartAxes
          side="left"
          ticks={ticks}
          plotX={plotX}
          plotY={MARGIN.top}
          plotWidth={plotWidth}
          plotHeight={plotHeight}
          formatValue={formatPercent}
        />

        <LineSeries
          points={ftrPoints}
          className="stroke-chart-5"
          markerClassName="fill-chart-5"
        />

        {renderRework && (
          <LineSeries
            points={reworkPoints}
            className="stroke-chart-4"
            markerClassName="fill-chart-4"
            strokeDasharray="2 2"
          />
        )}
      </ChartAccessibility>
    </div>
  )
}
