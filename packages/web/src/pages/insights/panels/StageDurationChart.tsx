import { useState } from 'react'
import { useStageDuration } from '../../../entities/issue'
import type { StageDurationMetricsResponse, StageDurationStageDto } from '../../../entities/issue'
import type { InsightsRange } from '../model/insights-range'
import {
  ChartContainer,
  ChartAccessibility,
  ChartAxes,
  useReducedMotion,
} from '../charts'
import type { LegendEntry } from '../charts'

const SVG_WIDTH = 500
const SVG_HEIGHT = 300
const MARGIN = { top: 26, right: 30, left: 70, bottom: 35 }

const plotX = MARGIN.left
const plotWidth = SVG_WIDTH - MARGIN.left - MARGIN.right
const plotHeight = SVG_HEIGHT - MARGIN.top - MARGIN.bottom

type DurationLens = 'average' | 'median'

const STAGE_LABEL_GAP = 6
const STAGE_ROW_HEIGHT = 28

function niceCeil(value: number): number {
  const magnitude = Math.pow(10, Math.floor(Math.log10(value)))
  const normalized = value / magnitude
  if (normalized <= 1) return magnitude
  if (normalized <= 2) return 2 * magnitude
  if (normalized <= 5) return 5 * magnitude
  return 10 * magnitude
}

function computeTickValues(maxValue: number): number[] {
  if (maxValue <= 0) return [0]
  const step = niceCeil(maxValue / 4)
  const ticks: number[] = []
  for (let i = 0; i <= 4; i++) {
    const val = i * step
    if (val <= maxValue * 1.05) {
      ticks.push(val)
    }
  }
  return ticks
}

function xForTick(value: number, maxValue: number): number {
  if (maxValue <= 0) return plotX
  return plotX + (value / maxValue) * plotWidth
}

function formatTickValue(value: number): string {
  if (Math.abs(value) >= 3600) return `${(value / 3600).toFixed(1)}h`
  if (Math.abs(value) >= 60) return `${(value / 60).toFixed(0)}m`
  return `${Math.round(value)}s`
}

function formatStageDuration(seconds: number): string {
  if (seconds >= 3600) return `${(seconds / 3600).toFixed(1)}h`
  if (seconds >= 60) return `${(seconds / 60).toFixed(0)}m`
  return `${Math.round(seconds)}s`
}

function formatStageWindowDayLabel(iso: string): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(iso)
  if (!match) return iso
  const d = new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]))
  return d.toLocaleDateString('en-US', { month: 'short', day: 'numeric' })
}

function formatStageWindow(data: StageDurationMetricsResponse): string {
  return `${formatStageWindowDayLabel(data.window.from)} – ${formatStageWindowDayLabel(data.window.to)}`
}

function valueFor(stage: StageDurationStageDto, lens: DurationLens): number | null {
  return lens === 'median' ? stage.medianSeconds : stage.averageSeconds
}

function hasPlottableStages(
  data: StageDurationMetricsResponse | undefined,
): data is StageDurationMetricsResponse {
  if (!data) return false
  return data.stages.some(
    (s) => s.averageSeconds !== null || s.medianSeconds !== null,
  )
}

export function StageDurationChart({ range }: { range: InsightsRange }) {
  const { data, isLoading, isError } = useStageDuration(range)
  const [lens, setLens] = useState<DurationLens>('average')

  const visibleCount = data
    ? data.stages.filter(s => valueFor(s, lens) !== null).length
    : 0

  const status = isLoading
    ? 'loading'
    : isError
      ? 'error'
      : !hasPlottableStages(data) || visibleCount === 0
        ? 'empty'
        : 'resolved'

  return (
    <section data-testid="stage-duration-chart" aria-label="Stage Duration">
      <div className="flex items-center justify-between mb-3">
        <h3 className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
          Stage Duration
        </h3>
        <div className="flex items-center gap-2">
          {hasPlottableStages(data) && (
            <span
              data-testid="stage-duration-chart-window"
              className="inline-flex items-center rounded-md border border-border bg-muted/40 px-2 py-0.5 text-xs tabular-nums text-muted-foreground"
            >
              {formatStageWindow(data)}
            </span>
          )}
          <LensToggle value={lens} onChange={setLens} />
        </div>
      </div>
      <ChartContainer
        status={status}
        emptyAction={
          <p className="text-sm text-muted-foreground text-center">
            Stage durations appear once an issue completes on the project.
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
      data-testid="stage-duration-lens"
      role="group"
      aria-label="Duration lens"
      className="inline-flex rounded-md border border-border text-xs"
    >
      <button
        type="button"
        data-testid="stage-duration-lens-average"
        aria-pressed={value === 'average'}
        className={
          'px-2.5 py-1 rounded-l-md transition-colors ' +
          (value === 'average'
            ? 'bg-chart-2 text-background'
            : 'bg-card/30 text-muted-foreground hover:text-foreground')
        }
        onClick={() => onChange('average')}
      >
        Average
      </button>
      <button
        type="button"
        data-testid="stage-duration-lens-median"
        aria-pressed={value === 'median'}
        className={
          'px-2.5 py-1 rounded-r-md -ml-px transition-colors ' +
          (value === 'median'
            ? 'bg-chart-2 text-background'
            : 'bg-card/30 text-muted-foreground hover:text-foreground')
        }
        onClick={() => onChange('median')}
      >
        Median
      </button>
    </div>
  )
}

function ChartInner({
  data,
  lens,
}: {
  data: StageDurationMetricsResponse
  lens: DurationLens
}) {
  const reduced = useReducedMotion()
  const stages = data.stages
  const visibleStages = stages.filter(s => valueFor(s, lens) !== null)

  const numericValues = visibleStages
    .map(s => valueFor(s, lens) as number)

  const maxValue = numericValues.length > 0
    ? Math.max(...numericValues, 0)
    : 0

  const stageRowHeight = visibleStages.length > 0
    ? Math.min(STAGE_ROW_HEIGHT, plotHeight / Math.max(visibleStages.length, 1))
    : STAGE_ROW_HEIGHT

  const barHeight = Math.max(stageRowHeight - STAGE_LABEL_GAP, 8)
  const totalBarHeight = visibleStages.length * stageRowHeight
  const barsStartY = MARGIN.top + Math.max((plotHeight - totalBarHeight) / 2, 0)

  const ticks = computeTickValues(maxValue)
  const axisTicks = ticks.map(value => ({
    value,
    x: xForTick(value, maxValue),
  }))

  const ratio = data.flowEfficiencyRatio
  const ratioText = ratio !== null && ratio !== undefined
    ? `${(ratio * 100).toFixed(0)}%`
    : 'n/a'

  const waitApproval = data.waitBreakout?.averageApprovalGateWaitSeconds
  const waitInactive = data.waitBreakout?.averageInactiveGapSeconds

  const summary =
    `Stage-duration distribution across ${visibleStages.length} workflow stage${visibleStages.length === 1 ? '' : 's'} ` +
    `(${visibleStages.map(s => s.stage).join(', ')}), ` +
    `${lens} lens. ` +
    `Flow efficiency: ${ratioText}. ` +
    `Average approval-gate wait per delivered issue: ${waitApproval !== null && waitApproval !== undefined ? formatStageDuration(waitApproval) : 'n/a'}. ` +
    `Average inactive gap per delivered issue: ${waitInactive !== null && waitInactive !== undefined ? formatStageDuration(waitInactive) : 'n/a'}.`

  const legend: LegendEntry[] = [
    { label: 'Stage bar', shape: 'bar' as const, className: 'fill-chart-2' },
    { label: 'Flow efficiency ratio', shape: 'line' as const, className: 'stroke-chart-5' },
    { label: 'Wait breakout', shape: 'dashedLine' as const, className: 'stroke-chart-3' },
  ]

  if (visibleStages.length === 0) {
    return (
      <ChartAccessibility
        ariaLabel="Stage duration chart for project"
        summary={`Stage durations empty under the ${lens} lens.`}
        legend={[]}
        viewBox={`0 0 ${SVG_WIDTH} ${SVG_HEIGHT}`}
        className="w-full h-auto"
      >
        <></>
      </ChartAccessibility>
    )
  }

  return (
    <ChartAccessibility
      ariaLabel={`Stage duration chart for project: one horizontal bar per workflow stage (${visibleStages.map(s => s.stage).join(', ')}) with ${lens} durations, flow-efficiency ratio, and wait breakout`}
      summary={summary}
      legend={legend}
      viewBox={`0 0 ${SVG_WIDTH} ${SVG_HEIGHT}`}
      className="w-full h-auto"
    >
      <text
        x={plotX + plotWidth / 2}
        y={MARGIN.top - 6}
        textAnchor="middle"
        className="fill-chart-2 text-[9px] tabular-nums font-medium"
      >
        {lens === 'average' ? 'Average' : 'Median'} duration per stage
      </text>

      <ChartAxes
        side="bottom"
        ticks={axisTicks}
        plotX={plotX}
        plotY={MARGIN.top}
        plotWidth={plotWidth}
        plotHeight={plotHeight}
        axisClassName="stroke-chart-2"
        labelClassName="fill-chart-2 text-[10px] tabular-nums"
        formatValue={formatTickValue}
      />

      <g data-testid="stage-bars">
        {visibleStages.map((stage, i) => {
          const v = valueFor(stage, lens) as number
          const ratio = maxValue > 0 ? v / maxValue : 0
          const rowY = barsStartY + i * stageRowHeight
          const barY = rowY + (stageRowHeight - barHeight) / 2
          const labelY = rowY + stageRowHeight / 2 + 3
          const valueLabelInside = ratio > 0.86
          const valueLabelX = valueLabelInside
            ? plotX + plotWidth * ratio - 6
            : plotX + plotWidth * ratio + 6
          return (
            <g key={stage.stage} data-testid={`stage-bar-${stage.stage}`}>
              <text
                x={plotX - STAGE_LABEL_GAP}
                y={labelY}
                textAnchor="end"
                className="fill-muted-foreground text-[10px] tabular-nums"
              >
                {stage.stage}
              </text>
              <rect
                x={plotX}
                y={barY}
                width={plotWidth}
                height={barHeight}
                fill="transparent"
                data-testid={`stage-bar-track-${stage.stage}`}
              />
              <rect
                x={plotX}
                y={barY}
                width={plotWidth}
                height={barHeight}
                className="fill-chart-2"
                rx={1}
                data-testid={`stage-bar-fill-${stage.stage}`}
                style={{
                  transform: `scaleX(${ratio})`,
                  transformOrigin: `${plotX}px ${barY + barHeight / 2}px`,
                  transition:
                    reduced
                      ? 'none'
                      : 'transform 0.5s ease-out',
                }}
              />
              <text
                x={valueLabelX}
                y={labelY}
                textAnchor={valueLabelInside ? 'end' : 'start'}
                className={`${valueLabelInside ? 'fill-background' : 'fill-foreground'} text-[10px] tabular-nums`}
                data-testid={`stage-bar-value-${stage.stage}`}
              >
                {formatStageDuration(v)}
              </text>
            </g>
          )
        })}
      </g>

      <FlowEfficiencyAnnotation
        ratio={ratio}
        x={plotX + plotWidth}
        y={MARGIN.top}
      />

      <WaitBreakoutAnnotation
        approvalSeconds={waitApproval ?? null}
        inactiveSeconds={waitInactive ?? null}
        x={plotX + plotWidth}
        y={MARGIN.top + plotHeight}
      />
    </ChartAccessibility>
  )
}

function FlowEfficiencyAnnotation({
  ratio,
  x,
  y,
}: {
  ratio: number | null
  x: number
  y: number
}) {
  if (ratio === null || ratio === undefined) return null
  return (
    <g data-testid="flow-efficiency-annotation" aria-hidden="true">
      <line
        x1={x - 88}
        x2={x - 76}
        y1={y - 11}
        y2={y - 11}
        className="stroke-chart-5"
        strokeWidth={2}
        strokeLinecap="round"
      />
      <text
        x={x}
        y={y - 8}
        textAnchor="end"
        className="fill-chart-5 text-[10px] tabular-nums font-medium"
      >
        Flow efficiency {Math.round(ratio * 100)}%
      </text>
    </g>
  )
}

function WaitBreakoutAnnotation({
  approvalSeconds,
  inactiveSeconds,
  x,
  y,
}: {
  approvalSeconds: number | null
  inactiveSeconds: number | null
  x: number
  y: number
}) {
  if (approvalSeconds === null && inactiveSeconds === null) return null
  const approvalText = approvalSeconds !== null
    ? `Wait approval ${formatStageDuration(approvalSeconds)}`
    : null
  const inactiveText = inactiveSeconds !== null
    ? `Wait idle ${formatStageDuration(inactiveSeconds)}`
    : null
  return (
    <g data-testid="wait-breakout-annotation" aria-hidden="true">
      <line
        x1={x - 88}
        x2={x - 76}
        y1={y + 11}
        y2={y + 11}
        className="stroke-chart-3"
        strokeWidth={2}
        strokeDasharray="3 2"
        strokeLinecap="round"
      />
      <text
        x={x}
        y={y + 14}
        textAnchor="end"
        className="fill-chart-3 text-[10px] tabular-nums"
      >
        {approvalText ?? ''}
      </text>
      <text
        x={x}
        y={y + 26}
        textAnchor="end"
        className="fill-chart-3 text-[10px] tabular-nums"
      >
        {inactiveText ?? ''}
      </text>
    </g>
  )
}
