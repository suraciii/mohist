import { useReducedMotion } from './useReducedMotion'

export interface ScatterPoint {
  x: number
  y: number
  id?: string | number
}

export interface ScatterSeriesProps {
  points: ScatterPoint[]
  radius?: number
  className?: string
  animated?: boolean
}

export function ScatterSeries({
  points,
  radius = 3,
  className = 'fill-chart-2',
  animated = true,
}: ScatterSeriesProps) {
  const reduced = useReducedMotion()

  if (points.length === 0) return null

  const transition = animated && !reduced
    ? 'opacity 0.4s ease-out, transform 0.4s ease-out'
    : 'none'
  const opacity = animated && !reduced ? 0.85 : 1

  return (
    <g data-testid="scatter-series">
      {points.map((point, i) => {
        const key = point.id ?? i
        return (
          <circle
            key={key}
            cx={point.x}
            cy={point.y}
            r={radius}
            className={className}
            data-testid={`scatter-point-${key}`}
            opacity={opacity}
            style={{ transition }}
          />
        )
      })}
    </g>
  )
}
