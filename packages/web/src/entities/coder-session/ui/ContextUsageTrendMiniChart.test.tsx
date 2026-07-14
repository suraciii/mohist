import '@testing-library/jest-dom'
import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import type { ContextUsageHistoryEntry } from '../model/types'
import { ContextUsageTrendMiniChart } from './ContextUsageTrendMiniChart'

function makeHistory(count: number, opts: { firstPercent?: number; lastPercent?: number; spacingSeconds?: number } = {}): ContextUsageHistoryEntry[] {
  const { firstPercent = 10, lastPercent = 80, spacingSeconds = 60 } = opts
  if (count < 2) throw new Error('need at least 2 samples')
  const out: ContextUsageHistoryEntry[] = []
  for (let i = 0; i < count; i += 1) {
    const ratio = count === 1 ? 0 : i / (count - 1)
    const percent = firstPercent + (lastPercent - firstPercent) * ratio
    const at = new Date(2026, 0, 1, 0, 0, 0, 0).toISOString()
    out.push({ at: at.replace(/T.*/, `T00:00:${String(i * spacingSeconds).padStart(2, '0')}Z`), percent })
  }
  return out
}

function queryChart() {
  return screen.queryByTestId('context-usage-trend-mini-chart') as SVGSVGElement | null
}

describe('ContextUsageTrendMiniChart', () => {
  it('renders nothing when history is null', () => {
    render(<ContextUsageTrendMiniChart history={null} />)

    expect(queryChart()).toBeNull()
    expect(screen.queryByRole('img')).toBeNull()
  })

  it('renders nothing when history is undefined', () => {
    render(<ContextUsageTrendMiniChart />)

    expect(queryChart()).toBeNull()
  })

  it('renders nothing when history has zero samples', () => {
    render(<ContextUsageTrendMiniChart history={[]} />)

    expect(queryChart()).toBeNull()
  })

  it('renders nothing when history has only a single sample (insufficient to plot a trend)', () => {
    render(<ContextUsageTrendMiniChart history={[{ at: '2026-01-01T00:00:00Z', percent: 30 }]} />)

    expect(queryChart()).toBeNull()
  })

  it('renders nothing when history contains a single usable sample after sanitization', () => {
    // Even though there are two entries, only one has a finite percent in range — the
    // chart must still degrade to hidden rather than plot a degenerate line.
    render(
      <ContextUsageTrendMiniChart
        history={[
          { at: '2026-01-01T00:00:00Z', percent: 25 },
          { at: '2026-01-01T00:01:00Z', percent: Number.NaN },
        ]}
      />,
    )

    expect(queryChart()).toBeNull()
  })

  it('renders an inline SVG with a polyline when history has two or more usable samples', () => {
    const history = makeHistory(4, { firstPercent: 20, lastPercent: 60 })

    render(<ContextUsageTrendMiniChart history={history} />)

    const svg = queryChart()
    expect(svg).not.toBeNull()
    expect(svg!.tagName.toLowerCase()).toBe('svg')
    expect(svg!.getAttribute('role')).toBe('img')
    expect(svg!.getAttribute('data-testid')).toBe('context-usage-trend-mini-chart')
    expect(svg!.getAttribute('data-history-length')).toBe('4')
    expect(svg!.getAttribute('data-status')).toBeNull()
    expect(svg!.getAttribute('data-latest-percent')).toBe('60')
    const paths = svg!.querySelectorAll('path')
    expect(paths.length).toBe(2)
    expect(paths[0]!.getAttribute('d')).toMatch(/^M/) // fill area
    expect(paths[1]!.getAttribute('d')).toMatch(/^M/) // polyline
    expect(paths[1]!.getAttribute('stroke')).toBeNull()
    expect(paths[1]!.getAttribute('class')).toContain('stroke-gray-400')
  })

  it('plots a polyline connecting the available samples (oldest left, newest right)', () => {
    const history = makeHistory(3, { firstPercent: 30, lastPercent: 70 })

    render(<ContextUsageTrendMiniChart history={history} />)

    const svg = queryChart()!
    const linePath = svg.querySelectorAll('path')[1]!.getAttribute('d') ?? ''

    const coords = linePath
      .split(/[ML]/)
      .filter((p) => p.length > 0)
      .map((p) => p.split(',').map((n) => Number(n)))

    expect(coords.length).toBe(3)
    // Oldest sits at the left, newest at the right.
    expect(coords[0]![0]).toBeLessThan(coords[coords.length - 1]![0]!)
    // Both end-points should be inside the inner viewBox (width = 78, height = 16).
    for (const [x, y] of coords) {
      expect(x).toBeGreaterThanOrEqual(0)
      expect(x).toBeLessThanOrEqual(78)
      expect(y).toBeGreaterThanOrEqual(0)
      expect(y).toBeLessThanOrEqual(16)
    }
  })

  it('clamps out-of-range percent values into the [0, 100] viewBox', () => {
    const history = [
      { at: '2026-01-01T00:00:00Z', percent: -50 },
      { at: '2026-01-01T00:01:00Z', percent: 250 },
    ]

    render(<ContextUsageTrendMiniChart history={history} />)

    const svg = queryChart()!
    const linePath = svg.querySelectorAll('path')[1]!.getAttribute('d') ?? ''
    const coords = linePath
      .split(/[ML]/)
      .filter((p) => p.length > 0)
      .map((p) => p.split(',').map((n) => Number(n)))

    // The -50 maps to y=16 (bottom) and 250 maps to y=0 (top).
    expect(coords[0]![1]).toBe(16)
    expect(coords[1]![1]).toBe(0)
  })

  it('drops non-finite percent values before plotting but keeps the trend visible when enough samples remain', () => {
    const history: ContextUsageHistoryEntry[] = [
      { at: '2026-01-01T00:00:00Z', percent: 10 },
      { at: '2026-01-01T00:01:00Z', percent: Number.NaN },
      { at: '2026-01-01T00:02:00Z', percent: 30 },
      { at: '2026-01-01T00:03:00Z', percent: 50 },
    ]

    render(<ContextUsageTrendMiniChart history={history} />)

    const svg = queryChart()
    expect(svg).not.toBeNull()
    // Two clean samples were skipped — three remain.
    expect(svg!.getAttribute('data-history-length')).toBe('3')
  })

  it('uses neutral stroke when the latest sample is below 60%', () => {
    const history = makeHistory(3, { firstPercent: 10, lastPercent: 40 })

    render(<ContextUsageTrendMiniChart history={history} />)

    const svg = queryChart()!
    expect(svg.getAttribute('data-status')).toBeNull()
    const linePath = svg.querySelectorAll('path')[1]!
    expect(linePath.getAttribute('class')).toContain('stroke-gray-400')
  })

  it('uses neutral stroke when the latest sample is in [60, 80)', () => {
    const history = makeHistory(3, { firstPercent: 30, lastPercent: 72 })

    render(<ContextUsageTrendMiniChart history={history} />)

    const svg = queryChart()!
    expect(svg.getAttribute('data-status')).toBeNull()
    const linePath = svg.querySelectorAll('path')[1]!
    expect(linePath.getAttribute('class')).toContain('stroke-gray-400')
  })

  it('uses neutral stroke when the latest sample is 80% or above', () => {
    const history = makeHistory(3, { firstPercent: 40, lastPercent: 90 })

    render(<ContextUsageTrendMiniChart history={history} />)

    const svg = queryChart()!
    expect(svg.getAttribute('data-status')).toBeNull()
    const linePath = svg.querySelectorAll('path')[1]!
    expect(linePath.getAttribute('class')).toContain('stroke-gray-400')
  })

  it('exposes the first and last sample timestamps and the sample count via data attributes', () => {
    const history = makeHistory(5, { firstPercent: 20, lastPercent: 80 })

    render(<ContextUsageTrendMiniChart history={history} />)

    const svg = queryChart()!
    expect(svg.getAttribute('data-history-length')).toBe('5')
    expect(svg.getAttribute('data-first-at')).toBe(history[0]!.at)
    expect(svg.getAttribute('data-last-at')).toBe(history[history.length - 1]!.at)
  })

  it('renders a descriptive aria-label and <title> summarizing the trend', () => {
    const history = makeHistory(6, { firstPercent: 10, lastPercent: 65 })

    render(<ContextUsageTrendMiniChart history={history} />)

    const svg = queryChart()!
    const ariaLabel = svg.getAttribute('aria-label') ?? ''
    expect(ariaLabel).toMatch(/Context usage trend/i)
    expect(ariaLabel).toMatch(/6 samples/)
    expect(ariaLabel).toMatch(/latest 65%/)

    const titleEl = svg.querySelector('title')
    expect(titleEl).not.toBeNull()
    expect(titleEl!.textContent).toBe(ariaLabel)
  })

  it('honours custom width / height props for the rendered SVG', () => {
    const history = makeHistory(2)

    render(<ContextUsageTrendMiniChart history={history} width={120} height={30} />)

    const svg = queryChart()!
    expect(svg.getAttribute('width')).toBe('120')
    expect(svg.getAttribute('height')).toBe('30')
    // The viewBox stays fixed so the chart stays crisp regardless of pixel size.
    expect(svg.getAttribute('viewBox')).toBe('0 0 80 20')
  })
})
