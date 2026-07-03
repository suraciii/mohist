import { useCumulativeFlow } from '../../../entities/issue'
import type {
  CumulativeFlowDayDto,
  CumulativeFlowResponse,
} from '../../../entities/issue'
import type { InsightsRange } from '../model/insights-range'
import {
  ChartContainer,
  ChartAccessibility,
  ChartAxes,
  AreaSeries,
} from '../charts'
import type { AreaBand, AxisTick, LegendEntry } from '../charts'

const SVG_WIDTH = 500
const SVG_HEIGHT = 300
const MARGIN = { top: 26, right: 24, left: 50, bottom: 35 }

const plotX = MARGIN.left
const plotWidth = SVG_WIDTH - MARGIN.left - MARGIN.right
const plotHeight = SVG_HEIGHT - MARGIN.top - MARGIN.bottom
const baselineY = MARGIN.top + plotHeight

const LABEL_INTERVAL = 7
const DAY_MS = 24 * 60 * 60 * 1000

const STAGE_ORDER = [
  'backlog',
  'plan',
  'build',
  'check',
  'integrate',
  'done',
] as const

type StageName = (typeof STAGE_ORDER)[number]

const STRATUM_FILL_CLASS: Record<StageName, string> = {
  backlog: 'fill-chart-1',
  plan: 'fill-chart-2',
  build: 'fill-chart-3',
  check: 'fill-chart-4',
  integrate: 'fill-chart-5',
  done: 'fill-chart-3',
}

/**
 * The stage → fill-class lookup is keyed by stratum (bottom→top), per
 * design D8. The palette has only five grayscale tokens, so the sixth
 * (top) band reuses the `fill-chart-3` token; the bands remain
 * distinguishable by stacking order + legend shape/label (the
 * accessibility wrapper's non-color contract).
 */

const STAGE_LABEL: Record<StageName, string> = {
  backlog: 'Backlog',
  plan: 'Plan',
  build: 'Build',
  check: 'Check',
  integrate: 'Integrate',
  done: 'Done',
}

function niceCeil(value: number): number {
  if (value <= 0) return 1
  const magnitude = Math.pow(10, Math.floor(Math.log10(value)))
  const normalized = value / magnitude
  if (normalized <= 1) return magnitude
  if (normalized <= 2) return 2 * magnitude
  if (normalized <= 5) return 5 * magnitude
  return 10 * magnitude
}

function computeTicks(maxValue: number): AxisTick[] {
  if (maxValue <= 0) return [{ value: 0, y: baselineY }]
  const step = niceCeil(maxValue / 4)
  const ticks: AxisTick[] = []
  for (let i = 0; i <= 4; i++) {
    const val = i * step
    if (val <= maxValue * 1.05) {
      ticks.push({
        value: val,
        y: baselineY - (val / maxValue) * plotHeight,
      })
    }
  }
  return ticks
}

function formatAxisValue(value: number): string {
  if (Math.abs(value) >= 1_000_000) return `${(value / 1_000_000).toFixed(1)}M`
  if (Math.abs(value) >= 1_000) return `${(value / 1_000).toFixed(1)}k`
  if (Number.isInteger(value)) return String(value)
  return value.toFixed(2)
}

function formatDayLabel(day: string): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(day)
  if (!match) return day
  const d = new Date(
    Number(match[1]),
    Number(match[2]) - 1,
    Number(match[3]),
  )
  return d.toLocaleDateString('en-US', { month: 'short', day: 'numeric' })
}

function stageCount(snapshot: CumulativeFlowDayDto, stage: StageName): number {
  switch (stage) {
    case 'backlog': return snapshot.backlog
    case 'plan': return snapshot.plan
    case 'build': return snapshot.build
    case 'check': return snapshot.check
    case 'integrate': return snapshot.integrate
    case 'done': return snapshot.done
  }
}

function hasSnapshotData(
  data: CumulativeFlowResponse | undefined,
): data is CumulativeFlowResponse {
  return !!data && data.snapshots.length > 0
}

function parseDayUtc(day: string): number | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(day)
  if (!match) return null
  return Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3]))
}

function daySpanInclusive(rangeFrom: string, rangeTo: string): number {
  const from = parseDayUtc(rangeFrom)
  const to = parseDayUtc(rangeTo)
  if (from === null || to === null || to < from) return 1
  return Math.floor((to - from) / DAY_MS) + 1
}

function xForDay(day: string, rangeFrom: string, rangeTo: string): number {
  const from = parseDayUtc(rangeFrom)
  const current = parseDayUtc(day)
  if (from === null || current === null) return plotX
  const daySpan = daySpanInclusive(rangeFrom, rangeTo)
  const offset = Math.max(0, Math.min(daySpan - 1, Math.floor((current - from) / DAY_MS)))
  if (daySpan <= 1) return plotX + plotWidth / 2
  return plotX + (offset / (daySpan - 1)) * plotWidth
}

function singleSnapshotSliceWidth(rangeFrom: string, rangeTo: string): number {
  const daySpan = daySpanInclusive(rangeFrom, rangeTo)
  return Math.max(8, plotWidth / Math.max(daySpan, 1))
}

export function CumulativeFlowChart({ range }: { range: InsightsRange }) {
  const { data, isLoading, isError } = useCumulativeFlow(range)

  const status = isLoading
    ? 'loading'
    : isError
      ? 'error'
      : !hasSnapshotData(data)
        ? 'empty'
        : 'resolved'

  return (
    <section data-testid="cumulative-flow-chart" aria-label="Cumulative Flow">
      <div className="flex items-center justify-between mb-3">
        <h3 className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
          Cumulative Flow
        </h3>
        {hasSnapshotData(data) && (
          <span
            data-testid="cumulative-flow-chart-window"
            className="inline-flex items-center rounded-md border border-border bg-muted/40 px-2 py-0.5 text-xs tabular-nums text-muted-foreground"
          >
            {formatDayLabel(data.rangeFrom)} – {formatDayLabel(data.rangeTo)}
          </span>
        )}
      </div>
      <ChartContainer
        status={status}
        emptyAction={
          <p className="text-sm text-muted-foreground text-center">
            Cumulative flow gains history once the first daily stage-population snapshot lands.
          </p>
        }
      >
        {data && hasSnapshotData(data) && <ChartInner data={data} />}
      </ChartContainer>
    </section>
  )
}

interface ChartInnerProps {
  data: CumulativeFlowResponse
}

function ChartInner({ data }: ChartInnerProps) {
  const { snapshots } = data
  const dayCount = snapshots.length

  const stackedTotals = snapshots.map((snapshot) =>
    STAGE_ORDER.reduce((acc, stage) => acc + stageCount(snapshot, stage), 0),
  )
  const maxValue = stackedTotals.length > 0 ? Math.max(...stackedTotals, 0) : 0
  const safeMax = maxValue > 0 ? maxValue : 1

  const ticks = computeTicks(maxValue)

  const bands: AreaBand[] = STAGE_ORDER.map((stage, stageIndex) => {
    const upperPoints = snapshots.map((snapshot) => {
      let accumulated = 0
      for (let j = 0; j <= stageIndex; j++) {
        accumulated += stageCount(snapshot, STAGE_ORDER[j])
      }
      const y = baselineY - (accumulated / safeMax) * plotHeight
      const x = xForDay(snapshot.day, data.rangeFrom, data.rangeTo)
      return { x, y }
    })
    const lowerPoints = stageIndex === 0
      ? undefined
      : snapshots.map((snapshot) => {
          let accumulated = 0
          for (let j = 0; j < stageIndex; j++) {
            accumulated += stageCount(snapshot, STAGE_ORDER[j])
          }
          const y = baselineY - (accumulated / safeMax) * plotHeight
          const x = xForDay(snapshot.day, data.rangeFrom, data.rangeTo)
          return { x, y }
        })

    const width = snapshots.length === 1
      ? singleSnapshotSliceWidth(data.rangeFrom, data.rangeTo)
      : 0
    const expandedUpper = width > 0 && upperPoints.length === 1
      ? [
          { x: Math.max(plotX, upperPoints[0].x - width / 2), y: upperPoints[0].y },
          { x: Math.min(plotX + plotWidth, upperPoints[0].x + width / 2), y: upperPoints[0].y },
        ]
      : upperPoints
    const expandedLower = width > 0 && lowerPoints?.length === 1
      ? [
          { x: Math.max(plotX, lowerPoints[0].x - width / 2), y: lowerPoints[0].y },
          { x: Math.min(plotX + plotWidth, lowerPoints[0].x + width / 2), y: lowerPoints[0].y },
        ]
      : lowerPoints

    return {
      label: stage,
      upper: expandedUpper,
      lower: expandedLower,
    }
  })

  const peakDay = stackedTotals.reduce<{
    snapshot: CumulativeFlowDayDto | null
    total: number
  }>(
    (acc, total, i) => {
      if (total > acc.total) {
        return { snapshot: snapshots[i], total }
      }
      return acc
    },
    { snapshot: null, total: 0 },
  )

  const latestSnapshot = snapshots[snapshots.length - 1]
  const latestTotal = latestSnapshot
    ? STAGE_ORDER.reduce((acc, stage) => acc + stageCount(latestSnapshot, stage), 0)
    : 0

  const summary =
    `Cumulative flow diagram across ${dayCount} trailing day${dayCount === 1 ? '' : 's'} ` +
    `(${formatDayLabel(data.rangeFrom)} to ${formatDayLabel(data.rangeTo)}), ` +
    `one band per workflow stage (${STAGE_ORDER.join(', ')}) in workflow order. ` +
    `Latest day (${formatDayLabel(latestSnapshot?.day ?? data.rangeTo)}): ` +
    `${STAGE_ORDER.map((stage) => `${STAGE_LABEL[stage]} ${latestSnapshot ? stageCount(latestSnapshot, stage) : 0}`).join(', ')}. ` +
    `Total WIP on latest day: ${latestTotal}. ` +
    (peakDay.snapshot
      ? `Peak stacked WIP was ${peakDay.total} on ${formatDayLabel(peakDay.snapshot.day)}.`
      : '')

  const legend: LegendEntry[] = STAGE_ORDER.map((stage) => ({
    label: `${STAGE_LABEL[stage]} band`,
    shape: 'bar' as const,
    className: STRATUM_FILL_CLASS[stage],
  }))

  return (
    <ChartAccessibility
      ariaLabel={`Cumulative flow diagram for project: stacked areas per workflow stage (${STAGE_ORDER.join(', ')}) across ${daySpanInclusive(data.rangeFrom, data.rangeTo)} trailing days from ${formatDayLabel(data.rangeFrom)} to ${formatDayLabel(data.rangeTo)}`}
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
        data-testid="cf-y-title"
      >
        Issues in workflow
      </text>

      <ChartAxes
        side="left"
        ticks={ticks}
        plotX={plotX}
        plotY={MARGIN.top}
        plotWidth={plotWidth}
        plotHeight={plotHeight}
        axisClassName="stroke-chart-2"
        labelClassName="fill-chart-2 text-[10px] tabular-nums"
        formatValue={formatAxisValue}
      />

      <AreaSeries
        bands={bands}
        baselineY={baselineY}
        bandClassName={(_band, i) => STRATUM_FILL_CLASS[STAGE_ORDER[i]]}
      />

      {snapshots.map((snapshot, i) => {
        if (i % LABEL_INTERVAL !== 0 && i !== dayCount - 1) return null
        const x = xForDay(snapshot.day, data.rangeFrom, data.rangeTo)
        return (
          <text
            key={`label-${i}`}
            x={x}
            y={baselineY + 18}
            textAnchor="middle"
            className="fill-muted-foreground text-[10px] tabular-nums"
            data-testid={`cf-x-label-${snapshot.day}`}
          >
            {formatDayLabel(snapshot.day)}
          </text>
        )
      })}
    </ChartAccessibility>
  )
}
