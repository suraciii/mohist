import { useState } from 'react'
import { useDeliveryTime } from '../../../entities/issue'
import type { DeliveryTimeMetricsResponse } from '../../../entities/issue'
import type { InsightsRange } from '../model/insights-range'
import {
  ChartContainer,
  ChartAccessibility,
  ChartAxes,
  ScatterSeries,
} from '../charts'
import type { AxisTick, LinePoint, ScatterPoint, LegendEntry } from '../charts'
import {
  computeRollingPercentile,
  P50_MEDIAN,
  P85_LINEAR_INTERPOLATION,
  ROLLING_WINDOW,
} from './model/delivery-time'

const SVG_WIDTH = 500
const SVG_HEIGHT = 300
const MARGIN = { top: 26, right: 35, left: 60, bottom: 35 }

const plotX = MARGIN.left
const plotWidth = SVG_WIDTH - MARGIN.left - MARGIN.right
const plotHeight = SVG_HEIGHT - MARGIN.top - MARGIN.bottom
const singlePointPercentileHalfWidth = 5

type DurationLens = 'lead' | 'cycle'

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

function formatDayLabel(iso: string): string {
  const dateOnly = /^(\d{4})-(\d{2})-(\d{2})/.exec(iso)
  const d = dateOnly
    ? new Date(Number(dateOnly[1]), Number(dateOnly[2]) - 1, Number(dateOnly[3]))
    : new Date(iso)
  return d.toLocaleDateString('en-US', { month: 'short', day: 'numeric' })
}

function formatDuration(days: number): string {
  return days.toFixed(1)
}

function durationFor(
  point: DeliveryTimeMetricsResponse['points'][number],
  lens: DurationLens,
): number | null {
  if (lens === 'cycle') return point.cycleDays
  return point.leadDays
}

function hasPlottablePoints(
  data: DeliveryTimeMetricsResponse | undefined,
): data is DeliveryTimeMetricsResponse {
  if (!data) return false
  return data.points.length > 0
}

export function CycleTimeChart({ range }: { range: InsightsRange }) {
  const { data, isLoading, isError } = useDeliveryTime(range)
  const [lens, setLens] = useState<DurationLens>('lead')

  const visibleCount = data && lens === 'cycle'
    ? data.points.filter(p => p.cycleDays !== null).length
    : (data?.points.length ?? 0)

  const status = isLoading
    ? 'loading'
    : isError
      ? 'error'
      : !hasPlottablePoints(data) || visibleCount === 0
        ? 'empty'
        : 'resolved'

  return (
    <section data-testid="cycle-time-chart" aria-label="Cycle Time">
      <div className="flex items-center justify-between mb-3">
        <h3 className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
          Cycle Time
        </h3>
        <div className="flex items-center gap-2">
          <span
            data-testid="cycle-time-chart-window"
            className="inline-flex items-center rounded-md border border-border bg-muted/40 px-2 py-0.5 text-xs tabular-nums text-muted-foreground"
          >
            {range}
          </span>
          <LensToggle value={lens} onChange={setLens} />
        </div>
      </div>
      <ChartContainer
        status={status}
        emptyAction={
          <p className="text-sm text-muted-foreground text-center">
            Cycle time appears once an issue completes on this project.
          </p>
        }
      >
        {data && <ChartInner data={data} lens={lens} />}
      </ChartContainer>
    </section>
  )
}

function LensToggle({
  value,
  onChange,
}: {
  value: DurationLens
  onChange: (lens: DurationLens) => void
}) {
  return (
    <div
      data-testid="cycle-time-lens"
      role="group"
      aria-label="Duration lens"
      className="inline-flex rounded-md border border-border text-xs"
    >
      <button
        type="button"
        data-testid="cycle-time-lens-lead"
        aria-pressed={value === 'lead'}
        className={
          'px-2.5 py-1 rounded-l-md transition-colors ' +
          (value === 'lead'
            ? 'bg-chart-2 text-background'
            : 'bg-card/30 text-muted-foreground hover:text-foreground')
        }
        onClick={() => onChange('lead')}
      >
        Lead time
      </button>
      <button
        type="button"
        data-testid="cycle-time-lens-cycle"
        aria-pressed={value === 'cycle'}
        className={
          'px-2.5 py-1 rounded-r-md -ml-px transition-colors ' +
          (value === 'cycle'
            ? 'bg-chart-2 text-background'
            : 'bg-card/30 text-muted-foreground hover:text-foreground')
        }
        onClick={() => onChange('cycle')}
      >
        Cycle time
      </button>
    </div>
  )
}

function ChartInner({
  data,
  lens,
}: {
  data: DeliveryTimeMetricsResponse
  lens: DurationLens
}) {
  const { points } = data
  const visiblePoints = lens === 'cycle'
    ? points.filter(p => p.cycleDays !== null)
    : points

  if (visiblePoints.length === 0) {
    return (
      <ChartAccessibility
        ariaLabel="Cycle-time scatter control chart for project"
        summary="No cycle-time points under the selected lens."
        legend={[]}
        viewBox={`0 0 ${SVG_WIDTH} ${SVG_HEIGHT}`}
        className="w-full h-auto"
      >
        <></>
      </ChartAccessibility>
    )
  }

  const timeDomain = points.map(p => new Date(p.completedAt).getTime())

  let xMin = Math.min(...timeDomain)
  let xMax = Math.max(...timeDomain)
  if (xMin === xMax) {
    xMin = xMin - 12 * 60 * 60 * 1000
    xMax = xMax + 12 * 60 * 60 * 1000
  }

  const xRange = xMax - xMin

  const xFor = (iso: string): number => {
    const t = new Date(iso).getTime()
    if (xRange === 0) return plotX + plotWidth / 2
    return plotX + ((t - xMin) / xRange) * plotWidth
  }

  const durations = visiblePoints.map(p => durationFor(p, lens)) as (number | null)[]
  const validDurations = durations.filter((d): d is number => d !== null)
  const yMax = validDurations.length > 0
    ? Math.max(...validDurations, 0)
    : 0

  const yFor = (days: number | null): number => {
    if (days === null) return MARGIN.top + plotHeight
    if (yMax <= 0) return MARGIN.top + plotHeight
    return MARGIN.top + plotHeight - (days / yMax) * plotHeight
  }

  const scatterPoints: ScatterPoint[] = visiblePoints.map(p => ({
    x: xFor(p.completedAt),
    y: yFor(durationFor(p, lens)),
    id: p.issueNumber,
  }))

  const percentileSamples = visiblePoints.map(p => ({
    issueNumber: p.issueNumber,
    value: durationFor(p, lens),
  }))

  const p50Values = computeRollingPercentile(percentileSamples, ROLLING_WINDOW, P50_MEDIAN)
  const p85Values = computeRollingPercentile(percentileSamples, ROLLING_WINDOW, P85_LINEAR_INTERPOLATION)

  const p50Points: LinePoint[] = []
  for (let i = 0; i < p50Values.length; i++) {
    const value = p50Values[i]
    if (value === null) continue
    p50Points.push({
      x: xFor(visiblePoints[i].completedAt),
      y: yFor(value),
    })
  }

  const p85Points: LinePoint[] = []
  for (let i = 0; i < p85Values.length; i++) {
    const value = p85Values[i]
    if (value === null) continue
    p85Points.push({
      x: xFor(visiblePoints[i].completedAt),
      y: yFor(value),
    })
  }

  const leftTicks = computeTicks(yMax, plotHeight)

  const pointCount = visiblePoints.length
  const firstPoint = visiblePoints[0]
  const lastPoint = visiblePoints[pointCount - 1]
  const p50Latest = p50Values[p50Values.length - 1] ?? null
  const p85Latest = p85Values[p85Values.length - 1] ?? null

  const summary =
    `Cycle-time scatter control chart from ${
      firstPoint ? formatDayLabel(firstPoint.completedAt) : 'N/A'
    } to ${lastPoint ? formatDayLabel(lastPoint.completedAt) : 'N/A'}. ` +
    `${pointCount} delivered issue${pointCount === 1 ? '' : 's'} with ` +
    `${lens === 'lead' ? 'lead' : 'cycle'} time plotted. ` +
    `Latest ${lens === 'lead' ? 'lead' : 'cycle'} time: ` +
    (durations[durations.length - 1] !== null && durations[durations.length - 1] !== undefined
      ? formatDuration(durations[durations.length - 1] as number)
      : 'N/A') +
    ` day(s). ` +
    (p50Latest !== null ? `Rolling P50: ${formatDuration(p50Latest)} day(s). ` : '') +
    (p85Latest !== null ? `Rolling P85: ${formatDuration(p85Latest)} day(s).` : '')

  const legend: LegendEntry[] = [
    { label: 'Delivered issue', shape: 'dot' as const, className: 'fill-chart-2' },
    { label: 'P50 (median)', shape: 'line' as const, className: 'stroke-chart-5' },
    { label: 'P85 (tail)', shape: 'dashedLine' as const, className: 'stroke-chart-3' },
  ]

  return (
    <ChartAccessibility
      ariaLabel="Cycle-time scatter control chart for project: one dot per delivered issue with rolling P50 and P85 percentile overlays"
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
        {lens === 'lead' ? 'Lead time' : 'Cycle time'} (days)
      </text>

      {visiblePoints.length > 1 && (
        <text
          x={plotX + plotWidth}
          y={MARGIN.top + plotHeight + 22}
          textAnchor="end"
          className="fill-muted-foreground text-[10px] tabular-nums"
        >
          {formatDayLabel(visiblePoints[0].completedAt)}
          {' – '}
          {formatDayLabel(visiblePoints[visiblePoints.length - 1].completedAt)}
        </text>
      )}

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

      {p50Points.length > 0 && (
        <PercentileOverlay points={p50Points} variant="solid" testId="p50-line" />
      )}
      {p85Points.length > 0 && (
        <PercentileOverlay points={p85Points} variant="dashed" testId="p85-line" />
      )}

      <ScatterSeries points={scatterPoints} className="fill-chart-2" radius={3} />
    </ChartAccessibility>
  )
}

function PercentileOverlay({
  points,
  variant,
  testId,
}: {
  points: LinePoint[]
  variant: 'solid' | 'dashed'
  testId: string
}) {
  const strokeClass = variant === 'dashed' ? 'stroke-chart-3' : 'stroke-chart-5'
  const dashArray = variant === 'dashed' ? '4 3' : undefined
  if (points.length === 0) return null
  const pathD = points.length === 1
    ? singlePointPath(points[0])
    : points.map((p, i) => `${i === 0 ? 'M' : 'L'}${p.x},${p.y}`).join(' ')
  return (
    <path
      data-testid={testId}
      d={pathD}
      fill="none"
      className={strokeClass}
      strokeWidth={1.5}
      strokeDasharray={dashArray}
      strokeLinejoin="round"
      strokeLinecap="round"
    />
  )
}

function singlePointPath(point: LinePoint): string {
  const x1 = Math.max(plotX, point.x - singlePointPercentileHalfWidth)
  const x2 = Math.min(plotX + plotWidth, point.x + singlePointPercentileHalfWidth)
  return `M${x1},${point.y} L${x2},${point.y}`
}
