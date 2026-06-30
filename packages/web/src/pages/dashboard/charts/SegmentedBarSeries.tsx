import { useReducedMotion } from './useReducedMotion'

export interface Segment {
  value: number
  fill: string
  maxValue?: number
  widthRatio?: number
}

export interface SegmentedBarDatum {
  segments: Segment[]
  label: string
}

export interface SegmentedBarSeriesProps {
  data: SegmentedBarDatum[]
  max: number
  plotX: number
  plotY: number
  plotWidth: number
  plotHeight: number
  barGap?: number
  animated?: boolean
}

export function SegmentedBarSeries({
  data,
  max,
  plotX,
  plotY,
  plotWidth,
  plotHeight,
  barGap = 2,
  animated = true,
}: SegmentedBarSeriesProps) {
  const reduced = useReducedMotion()
  const safeMax = max > 0 ? max : 1
  const barCount = data.length
  const totalGap = barGap * (barCount - 1)
  const barWidth = barCount > 0 ? (plotWidth - totalGap) / barCount : 0
  const originY = plotY + plotHeight

  return (
    <g data-testid="segmented-bar-series">
      {data.map((d, i) => {
        const barX = plotX + i * (barWidth + barGap)
        const centerX = barX + barWidth / 2

        return (
          <g key={i} data-testid={`segmented-bar-${i}`}>
            {d.segments.map((seg, s) => {
              const visibleValue = seg.maxValue === undefined
                ? seg.value
                : Math.min(seg.value, seg.maxValue)
              const ratio = safeMax > 0 ? visibleValue / safeMax : 0
              const widthRatio = seg.widthRatio === undefined
                ? 1
                : Math.max(0, Math.min(1, seg.widthRatio))
              const segmentWidth = barWidth * widthRatio
              const segmentX = barX + (barWidth - segmentWidth) / 2

              return (
                <rect
                  key={s}
                  x={segmentX}
                  y={plotY}
                  width={segmentWidth}
                  height={plotHeight}
                  className={seg.fill}
                  data-testid={`segment-${i}-${s}`}
                  rx={1}
                  style={{
                    transform: `scaleY(${ratio})`,
                    transformOrigin: `${centerX}px ${originY}px`,
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
      })}
    </g>
  )
}
