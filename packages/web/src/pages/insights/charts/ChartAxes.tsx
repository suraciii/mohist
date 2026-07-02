export interface AxisTick {
  value: number
  y?: number
  x?: number
}

export type AxisSide = 'left' | 'right' | 'bottom'

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
  formatValue?: (value: number) => string
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
  formatValue = formatAxisValue,
}: ChartAxesProps) {
  if (side === 'bottom') {
    const axisY = plotY + plotHeight
    return (
      <g data-testid="chart-axis-bottom">
        <line
          x1={plotX}
          y1={axisY}
          x2={plotX + plotWidth}
          y2={axisY}
          className={axisClassName}
          strokeWidth={1}
        />
        {ticks.map((tick, i) => {
          const x = tick.x ?? plotX
          return (
            <g key={i}>
              <line
                x1={x}
                y1={axisY}
                x2={x}
                y2={axisY + tickLength}
                className={axisClassName}
                strokeWidth={1}
              />
              <text
                x={x}
                y={axisY + tickLength + 10}
                textAnchor="middle"
                className={labelClassName}
              >
                {formatValue(tick.value)}
              </text>
            </g>
          )
        })}
      </g>
    )
  }

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
            y1={tick.y ?? plotY}
            x2={isLeft ? axisX - tickLength : axisX + tickLength}
            y2={tick.y ?? plotY}
            className={axisClassName}
            strokeWidth={1}
          />
          <text
            x={isLeft ? axisX - tickLength - 4 : axisX + tickLength + 4}
            y={tick.y ?? plotY}
            textAnchor={isLeft ? 'end' : 'start'}
            dominantBaseline="central"
            className={labelClassName}
          >
            {formatValue(tick.value)}
          </text>
        </g>
      ))}
    </g>
  )
}
