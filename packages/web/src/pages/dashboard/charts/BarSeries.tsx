import { useReducedMotion } from './useReducedMotion'

export interface BarDatum {
  value: number
  label: string
}

export interface BarSeriesProps {
  data: BarDatum[]
  plotX: number
  plotY: number
  plotWidth: number
  plotHeight: number
  barGap?: number
  className?: string
  animated?: boolean
}

export function BarSeries({
  data,
  plotX,
  plotY,
  plotWidth,
  plotHeight,
  barGap = 2,
  className = 'fill-chart-2',
  animated = true,
}: BarSeriesProps) {
  const reduced = useReducedMotion()
  const max = Math.max(...data.map((d) => d.value), 0) || 1
  const barCount = data.length
  const totalGap = barGap * (barCount - 1)
  const barWidth = barCount > 0 ? (plotWidth - totalGap) / barCount : 0
  const originY = plotY + plotHeight

  return (
    <g data-testid="bar-series">
      {data.map((d, i) => {
        const ratio = d.value / max
        const barX = plotX + i * (barWidth + barGap)

        return (
          <rect
            key={i}
            x={barX}
            y={plotY}
            width={barWidth}
            height={plotHeight}
            className={className}
            data-testid={`bar-${i}`}
            rx={1}
            style={{
              transform: `scaleY(${ratio})`,
              transformOrigin: `${barX + barWidth / 2}px ${originY}px`,
              transition:
                animated && !reduced
                  ? 'transform 0.5s ease-out'
                  : 'none',
            }}
          />
        )
      })}
    </g>
  )
}
