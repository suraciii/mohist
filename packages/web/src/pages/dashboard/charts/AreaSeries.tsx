import { useReducedMotion } from './useReducedMotion'

export interface AreaPoint {
  x: number
  y: number
}

export interface AreaBand {
  /**
   * A series of (x, y) points describing the *upper* edge of the
   * band, in plot coordinates (left-to-right).
   */
  upper: AreaPoint[]
  /**
   * A series of (x, y) points describing the *lower* edge of the
   * band, in plot coordinates (left-to-right). When omitted, the band
   * closes to the chart's `baselineY`; only the bottom stacked band
   * should rely on that fallback.
   */
  lower?: AreaPoint[]
  /**
   * The category label used by tests and accessibility hooks to
   * identify the band (e.g. the workflow stage name).
   */
  label: string
}

export interface AreaSeriesProps {
  bands: AreaBand[]
  baselineY: number
  className?: string
  /** Per-band class names, applied in band order. Falls back to `className`. */
  bandClassName?: (band: AreaBand, index: number) => string
  animated?: boolean
}

/**
 * Stacked-area primitive. Each band renders as a closed SVG `<path>`
 * whose upper edge is the band's `upper` polyline and whose lower edge
 * is either `band.lower` or the chart's `baselineY` (for the bottom
 * band). Non-bottom bands must close to the previous cumulative edge,
 * not the floor, otherwise later opaque bands cover the stack below.
 *
 * The reveal animation is transform/opacity-based and honors
 * `prefers-reduced-motion` via `useReducedMotion()` — exactly the
 * pattern `LineSeries` uses.
 */
export function AreaSeries({
  bands,
  baselineY,
  className = 'fill-chart-2',
  bandClassName,
  animated = true,
}: AreaSeriesProps) {
  const reduced = useReducedMotion()
  const validBands = bands.filter((band) => band.upper.length > 0)

  if (validBands.length === 0) return null

  const motionStyle = !animated
    ? { transition: 'none' }
    : reduced
      ? { transition: 'none' }
      : { transition: 'opacity 0.5s ease-out' }

  return (
    <g data-testid="area-series">
      {validBands.map((band, i) => {
        const pathD = buildAreaPath(band.upper, band.lower, baselineY)
        const resolved = bandClassName
          ? bandClassName(band, i)
          : className

        return (
          <path
            key={i}
            data-testid={`area-band-${band.label}`}
            d={pathD}
            className={resolved}
            stroke="none"
            style={motionStyle}
          />
        )
      })}
    </g>
  )
}

function buildAreaPath(
  upper: AreaPoint[],
  lower: AreaPoint[] | undefined,
  baselineY: number,
): string {
  if (upper.length === 0) return ''
  const first = upper[0]
  const last = upper[upper.length - 1]

  const segments = upper
    .map((p, i) => `${i === 0 ? 'M' : 'L'}${p.x},${p.y}`)
    .join(' ')

  const lowerEdge = lower && lower.length > 0
    ? [...lower].reverse()
    : [
        { x: last.x, y: baselineY },
        { x: first.x, y: baselineY },
      ]

  const closeLine = lowerEdge.map((p) => `L${p.x},${p.y}`).join(' ')

  return `${segments} ${closeLine} Z`
}
