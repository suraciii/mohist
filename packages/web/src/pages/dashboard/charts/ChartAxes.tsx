export interface AxisTick {
  value: number
  y: number
}

export type AxisSide = 'left' | 'right'

export interface ChartAxesProps {
  side: AxisSide
  ticks: AxisTick[]
  plotX: number
  plotY: number
  plotWidth: number
  plotHeight: number
  tickLength?: number
  axisClassName?: string
  labelClassName?: string
}

function formatAxisValue(value: number): string {
  if (Math.abs(value) >= 1_000_000) return `${(value / 1_000_000).toFixed(1)}M`
  if (Math.abs(value) >= 1_000) return `${(value / 1_000).toFixed(1)}k`
  if (Number.isInteger(value)) return String(value)
  return value.toFixed(2)
}

export function ChartAxes({
  side,
  ticks,
  plotX,
  plotY,
  plotWidth,
  plotHeight,
  tickLength = 4,
  axisClassName = 'stroke-border',
  labelClassName = 'fill-muted-foreground text-[10px] tabular-nums',
}: ChartAxesProps) {
  const axisX = side === 'left' ? plotX : plotX + plotWidth
  const isLeft = side === 'left'

  return (
    <g data-testid={`chart-axis-${side}`}>
      <line
        x1={axisX}
        y1={plotY}
        x2={axisX}
        y2={plotY + plotHeight}
        className={axisClassName}
        strokeWidth={1}
      />
      {ticks.map((tick, i) => (
        <g key={i}>
          <line
            x1={axisX}
            y1={tick.y}
            x2={isLeft ? axisX - tickLength : axisX + tickLength}
            y2={tick.y}
            className={axisClassName}
            strokeWidth={1}
          />
          <text
            x={isLeft ? axisX - tickLength - 4 : axisX + tickLength + 4}
            y={tick.y}
            textAnchor={isLeft ? 'end' : 'start'}
            dominantBaseline="central"
            className={labelClassName}
          >
            {formatAxisValue(tick.value)}
          </text>
        </g>
      ))}
    </g>
  )
}
