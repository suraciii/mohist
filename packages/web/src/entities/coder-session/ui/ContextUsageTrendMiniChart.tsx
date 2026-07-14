import { cn } from '@/shared/lib/utils'
import type { ContextUsageHistoryEntry } from '../model/types'

export interface ContextUsageTrendMiniChartProps {
  /**
   * Bounded usage history for a single session (target ~24 samples,
   * last-N with coarse time bucketing — see T-002). The chart degrades
   * to `null` whenever fewer than two samples are available so a freshly
   * started session does not render a broken or empty-axis chart.
   */
  history?: ContextUsageHistoryEntry[] | null
  /**
   * Pixel width of the rendered SVG. Height scales to keep a 4:1
   * aspect ratio so the line stays legible at compact card sizes.
   * Defaults to `80`.
   */
  width?: number
  /**
   * Pixel height of the rendered SVG. Defaults to `20`.
   */
  height?: number
  className?: string
}

const VIEWBOX_WIDTH = 80
const VIEWBOX_HEIGHT = 20
const PADDING_X = 1
const PADDING_Y = 2

const STROKE_CLASS = 'stroke-gray-400'
const FILL_CLASS = 'fill-gray-400/15'

/**
 * Build a polyline `d` attribute for the supplied samples, mapping the
 * oldest sample to the left edge of the inner viewBox and the newest
 * to the right edge. Y is inverted so a higher percent sits higher on
 * the chart (matching the snapshot bar above this widget).
 */
function buildPath(samples: ContextUsageHistoryEntry[], innerWidth: number, innerHeight: number): string {
  const points: Array<{ x: number; y: number }> = []
  const stepCount = samples.length - 1
  for (let i = 0; i < samples.length; i += 1) {
    const xRatio = stepCount === 0 ? 0.5 : i / stepCount
    const x = xRatio * innerWidth
    const clamped = clamp(samples[i]!.percent)
    const yRatio = 1 - clamped / 100
    const y = yRatio * innerHeight
    points.push({ x, y })
  }
  return points
    .map((point, index) => `${index === 0 ? 'M' : 'L'}${point.x.toFixed(2)},${point.y.toFixed(2)}`)
    .join(' ')
}

function buildFillPath(linePath: string, innerWidth: number, innerHeight: number): string {
  return `${linePath} L${innerWidth.toFixed(2)},${innerHeight.toFixed(2)} L0,${innerHeight.toFixed(2)} Z`
}

/**
 * Filter samples to those whose `percent` resolves to a finite number.
 * Non-finite values (NaN, ±Infinity) cannot be plotted, so they are
 * dropped. Out-of-range values (negative or >100) are clamped into the
 * [0, 100] band rather than dropped — the chart tolerates these so a
 * buggy upstream writer cannot hide a still-plotable trend.
 */
function sanitize(samples: ContextUsageHistoryEntry[]): ContextUsageHistoryEntry[] {
  return samples
    .filter((s) => typeof s?.percent === 'number' && Number.isFinite(s.percent))
    .map((s) => ({ at: s.at, percent: clamp(s.percent) }))
}

function clamp(percent: number): number {
  if (percent < 0) return 0
  if (percent > 100) return 100
  return percent
}

/**
 * Lightweight context-usage trend sparkline for Pulse compact cards.
 *
 * Renders an inline SVG with a polyline (and a soft fill) over a
 * fixed viewBox so the chart stays crisp regardless of where the card
 * sits in the layout. No charting library is involved — every
 * computation is in this file and is independently testable. The
 * widget degrades to `null` (renders nothing) whenever fewer than two
 * usable samples are available, so a freshly started session does not
 * show a broken or empty-axis chart.
 *
 * The stroke stays neutral because server-provided healthStatus is the
 * only source for traffic-light context health classification.
 */
export function ContextUsageTrendMiniChart({
  history,
  width = VIEWBOX_WIDTH,
  height = VIEWBOX_HEIGHT,
  className,
}: ContextUsageTrendMiniChartProps) {
  const cleaned = sanitize(history ?? [])
  if (cleaned.length < 2) return null

  const innerWidth = VIEWBOX_WIDTH - PADDING_X * 2
  const innerHeight = VIEWBOX_HEIGHT - PADDING_Y * 2
  const linePath = buildPath(cleaned, innerWidth, innerHeight)
  const fillPath = buildFillPath(linePath, innerWidth, innerHeight)
  const latest = cleaned[cleaned.length - 1]!

  const offsetX = PADDING_X
  const offsetY = PADDING_Y
  const firstAt = cleaned[0]!.at
  const lastAt = latest.at
  const headline = `Context usage trend from ${cleaned.length} samples (latest ${Math.round(latest.percent)}%)`

  return (
    <svg
      width={width}
      height={height}
      viewBox={`0 0 ${VIEWBOX_WIDTH} ${VIEWBOX_HEIGHT}`}
      role="img"
      aria-label={headline}
      data-testid="context-usage-trend-mini-chart"
      data-history-length={cleaned.length}
      data-latest-percent={latest.percent}
      data-first-at={firstAt}
      data-last-at={lastAt}
      className={cn('block overflow-visible', className)}
    >
      <title>{headline}</title>
      <g transform={`translate(${offsetX}, ${offsetY})`}>
        <path d={fillPath} className={FILL_CLASS} aria-hidden="true" />
        <path
          d={linePath}
          fill="none"
          strokeWidth="1.25"
          strokeLinecap="round"
          strokeLinejoin="round"
          className={STROKE_CLASS}
        />
      </g>
    </svg>
  )
}
