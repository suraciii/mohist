import { useReducedMotion } from './useReducedMotion'

export interface LinePoint {
  x: number
  y: number
}

export interface LineSeriesProps {
  points: (LinePoint | null)[]
  className?: string
  markerClassName?: string
  animated?: boolean
}

function splitSegments(points: (LinePoint | null)[]): LinePoint[][] {
  const segments: LinePoint[][] = []
  let current: LinePoint[] = []

  for (const point of points) {
    if (point === null) {
      if (current.length > 0) {
        segments.push(current)
        current = []
      }
      continue
    }

    current.push(point)
  }

  if (current.length > 0) segments.push(current)

  return segments
}

export function LineSeries({
  points,
  className = 'stroke-chart-5',
  markerClassName = 'fill-chart-5',
  animated = true,
}: LineSeriesProps) {
  const reduced = useReducedMotion()
  const validPoints = points.filter((p): p is LinePoint => p !== null)
  const segments = splitSegments(points)

  if (validPoints.length === 0) return null

  const motionStyle = animated && !reduced ? { transition: 'opacity 0.4s ease-out' } : {}

  return (
    <g data-testid="line-series">
      {segments.map((segment, i) => {
        const pathD = segment
          .map((p, pointIndex) => `${pointIndex === 0 ? 'M' : 'L'}${p.x},${p.y}`)
          .join(' ')

        return (
          <path
            key={i}
            data-testid={`line-segment-${i}`}
            d={pathD}
            className={`fill-none ${className}`}
            strokeWidth={2}
            strokeLinejoin="round"
            strokeLinecap="round"
            style={motionStyle}
          />
        )
      })}
      {validPoints.map((p, i) => (
        <circle
          key={i}
          cx={p.x}
          cy={p.y}
          r={3}
          className={markerClassName}
          data-testid={`line-marker-${i}`}
          style={motionStyle}
        />
      ))}
    </g>
  )
}
