// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { setPrefersReducedMotion } from '../../../../tests/setup'
import type { CompletionTrendResponse } from '../../../entities/issue'

const mockUseCompletionThroughput = vi.fn()
vi.mock('../../../entities/issue', () => ({
  useCompletionThroughput: () => mockUseCompletionThroughput(),
}))

import { ThroughputChart } from './ThroughputChart'

function buildThroughputData(overrides?: Partial<CompletionTrendResponse>): CompletionTrendResponse {
  return {
    bucket: 'day',
    window: { from: '2026-06-01T00:00:00', to: '2026-06-07T23:59:59' },
    buckets: [
      { boundary: '2026-06-01', completed: 10, failed: 3 },
      { boundary: '2026-06-02', completed: 25, failed: 5 },
      { boundary: '2026-06-03', completed: 5, failed: 0 },
      { boundary: '2026-06-04', completed: 30, failed: 2 },
      { boundary: '2026-06-05', completed: 15, failed: 4 },
      { boundary: '2026-06-06', completed: 20, failed: 1 },
      { boundary: '2026-06-07', completed: 8, failed: 0 },
    ],
    ...overrides,
  }
}

function buildAllZeroData(): CompletionTrendResponse {
  return {
    bucket: 'day',
    window: { from: '2026-06-01T00:00:00', to: '2026-06-07T23:59:59' },
    buckets: Array.from({ length: 7 }, (_, i) => ({
      boundary: `2026-06-${String(i + 1).padStart(2, '0')}`,
      completed: 0,
      failed: 0,
    })),
  }
}

function build30dayData(): CompletionTrendResponse {
  const buckets = Array.from({ length: 30 }, (_, i) => ({
    boundary: `2026-06-${String(i + 1).padStart(2, '0')}`,
    completed: i % 7,
    failed: i % 4 === 0 ? 1 : 0,
  }))
  return {
    bucket: 'day',
    window: { from: '2026-06-01T00:00:00', to: '2026-06-30T23:59:59' },
    buckets,
  }
}

describe('ThroughputChart', () => {
  afterEach(() => {
    cleanup()
    mockUseCompletionThroughput.mockReset()
  })

  // --- Three-state routing ---

  it('renders loading state via ChartContainer', () => {
    mockUseCompletionThroughput.mockReturnValue({ data: undefined, isLoading: true, isError: false })

    render(<ThroughputChart />)

    expect(screen.getByTestId('chart-container-loading')).toBeInTheDocument()
    expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
  })

  it('renders error state via ChartContainer', () => {
    mockUseCompletionThroughput.mockReturnValue({ data: undefined, isLoading: false, isError: true, error: new Error('fail') })

    render(<ThroughputChart />)

    expect(screen.getByTestId('chart-container-error')).toBeInTheDocument()
    expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
  })

  it('renders empty state with next action when all buckets are zero', () => {
    mockUseCompletionThroughput.mockReturnValue({
      data: buildAllZeroData(),
      isLoading: false,
      isError: false,
    })

    render(<ThroughputChart />)

    const empty = screen.getByTestId('chart-container-empty')
    expect(empty).toBeInTheDocument()
    expect(empty.textContent).toContain('Throughput appears once an issue completes on this project')
    expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
  })

  it('renders empty state when buckets are empty', () => {
    mockUseCompletionThroughput.mockReturnValue({
      data: buildThroughputData({ buckets: [] }),
      isLoading: false,
      isError: false,
    })

    render(<ThroughputChart />)

    const empty = screen.getByTestId('chart-container-empty')
    expect(empty).toBeInTheDocument()
    expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
  })

  it('failure-only throughput is NOT empty (failed bars render)', () => {
    const data = buildAllZeroData()
    data.buckets[2] = { boundary: '2026-06-03', completed: 0, failed: 5 }

    mockUseCompletionThroughput.mockReturnValue({
      data,
      isLoading: false,
      isError: false,
    })

    render(<ThroughputChart />)

    expect(screen.queryByTestId('chart-container-empty')).not.toBeInTheDocument()
    expect(screen.getByTestId('chart-accessibility')).toBeInTheDocument()
  })

  it('renders resolved chart content with accessibility wrapper', () => {
    mockUseCompletionThroughput.mockReturnValue({ data: buildThroughputData(), isLoading: false, isError: false })

    render(<ThroughputChart />)

    expect(screen.getByTestId('chart-accessibility')).toBeInTheDocument()
    expect(screen.queryByTestId('chart-container-loading')).not.toBeInTheDocument()
    expect(screen.queryByTestId('chart-container-error')).not.toBeInTheDocument()
    expect(screen.queryByTestId('chart-container-empty')).not.toBeInTheDocument()
  })

  // --- Bar completed/failed values from daily buckets ---

  it('renders one segmented bar per bucket with completed and failed segments', () => {
    const data = buildThroughputData()
    mockUseCompletionThroughput.mockReturnValue({ data, isLoading: false, isError: false })

    render(<ThroughputChart />)

    const barSeries = screen.getByTestId('segmented-bar-series')
    expect(barSeries.children).toHaveLength(data.buckets.length)

    for (let i = 0; i < data.buckets.length; i++) {
      expect(screen.getByTestId(`segmented-bar-${i}`)).toBeInTheDocument()
      expect(screen.getByTestId(`segment-${i}-0`)).toBeInTheDocument()
      expect(screen.getByTestId(`segment-${i}-1`)).toBeInTheDocument()
    }
  })

  it('completed segment fill uses fill-chart-2 and failed uses fill-chart-4', () => {
    mockUseCompletionThroughput.mockReturnValue({ data: buildThroughputData(), isLoading: false, isError: false })

    render(<ThroughputChart />)

    const completedSeg = screen.getByTestId('segment-0-0')
    expect(completedSeg.getAttribute('class')).toContain('fill-chart-2')
    expect(completedSeg.getAttribute('fill')).toBeNull()

    const failedSeg = screen.getByTestId('segment-0-1')
    expect(failedSeg.getAttribute('class')).toContain('fill-chart-4')
    expect(failedSeg.getAttribute('fill')).toBeNull()
  })

  it('day with completed=0 renders a zero-height bar (not a gap) and encodes failed count', () => {
    const data = buildThroughputData()
    data.buckets[2] = { boundary: '2026-06-03', completed: 0, failed: 3 }

    mockUseCompletionThroughput.mockReturnValue({ data, isLoading: false, isError: false })

    render(<ThroughputChart />)

    const completedSeg = screen.getByTestId('segment-2-0')
    expect(completedSeg.style.transform).toContain('scaleY(0)')

    const failedSeg = screen.getByTestId('segment-2-1')
    expect(failedSeg).toBeInTheDocument()
    const failedScale = Number(failedSeg.style.transform.match(/scaleY\(([\d.]+)\)/)?.[1] ?? 0)
    expect(failedScale).toBeCloseTo(3 / 30)
  })

  it('encodes the full failed count when failures exceed completions', () => {
    const data = buildThroughputData()
    data.buckets[2] = { boundary: '2026-06-03', completed: 2, failed: 5 }

    mockUseCompletionThroughput.mockReturnValue({ data, isLoading: false, isError: false })

    render(<ThroughputChart />)

    const completedScale = Number(screen.getByTestId('segment-2-0').style.transform.match(/scaleY\(([\d.]+)\)/)?.[1] ?? 0)
    const completedSeg = screen.getByTestId('segment-2-0')
    const failedSeg = screen.getByTestId('segment-2-1')
    const failedScale = Number(failedSeg.style.transform.match(/scaleY\(([\d.]+)\)/)?.[1] ?? 0)
    expect(completedScale).toBeCloseTo(2 / 30)
    expect(failedScale).toBeCloseTo(5 / 30)
    expect(Number(failedSeg.getAttribute('width'))).toBeLessThan(Number(completedSeg.getAttribute('width')))
  })

  // --- MA values ---

  it('renders line series for 7-day moving average computed over completed counts', () => {
    mockUseCompletionThroughput.mockReturnValue({ data: buildThroughputData(), isLoading: false, isError: false })

    render(<ThroughputChart />)

    const lineSeries = screen.getByTestId('line-series')
    expect(lineSeries).toBeInTheDocument()

    const markers = lineSeries.querySelectorAll('circle')
    expect(markers.length).toBeGreaterThan(0)
  })

  it('MA uses stroke-chart-5 and fill-chart-5 theme tokens', () => {
    mockUseCompletionThroughput.mockReturnValue({ data: buildThroughputData(), isLoading: false, isError: false })

    render(<ThroughputChart />)

    const path = screen.getByTestId('line-series').querySelector('path')
    const pathClasses = path?.getAttribute('class') ?? ''
    expect(pathClasses).toContain('stroke-chart-5')

    const marker = screen.getByTestId('line-marker-0')
    expect(marker.getAttribute('class')).toContain('fill-chart-5')
  })

  // --- Legend entries ---

  it('renders ChartLegend with three entries (Completed, Failed, 7-day average)', () => {
    mockUseCompletionThroughput.mockReturnValue({ data: buildThroughputData(), isLoading: false, isError: false })

    render(<ThroughputChart />)

    const legend = screen.getByTestId('chart-legend')
    expect(legend).toBeInTheDocument()
    expect(legend.textContent).toContain('Completed')
    expect(legend.textContent).toContain('Failed')
    expect(legend.textContent).toContain('7-day average')
  })

  it('legend entries are disambiguated by label and shape (non-color channels)', () => {
    mockUseCompletionThroughput.mockReturnValue({ data: buildThroughputData(), isLoading: false, isError: false })

    render(<ThroughputChart />)

    const legend = screen.getByTestId('chart-legend')
    const entries = legend.querySelectorAll('span')

    const completedEntry = [...entries].find(e => e.textContent === 'Completed')
    const failedEntry = [...entries].find(e => e.textContent === 'Failed')
    const avgEntry = [...entries].find(e => e.textContent === '7-day average')

    expect(completedEntry).toBeTruthy()
    expect(failedEntry).toBeTruthy()
    expect(avgEntry).toBeTruthy()
  })

  // --- Empty state next action ---

  it('empty state has concrete next action text', () => {
    mockUseCompletionThroughput.mockReturnValue({
      data: buildThroughputData({ buckets: [] }),
      isLoading: false,
      isError: false,
    })

    render(<ThroughputChart />)

    const empty = screen.getByTestId('chart-container-empty')
    expect(empty.textContent).toContain('Throughput appears once an issue completes on this project')
  })

  // --- Token colors ---

  it('left axis uses stroke-chart-2 and fill-chart-2', () => {
    mockUseCompletionThroughput.mockReturnValue({ data: buildThroughputData(), isLoading: false, isError: false })

    render(<ThroughputChart />)

    const axis = screen.getByTestId('chart-axis-left')
    const axisLine = axis.querySelector('line')
    expect(axisLine?.getAttribute('class')).toContain('stroke-chart-2')
    const textEl = axis.querySelector('text')
    expect(textEl?.getAttribute('class')).toContain('fill-chart-2')
  })

  // --- Accessibility ---

  it('accessibility sr-only summary is rendered with window, peak, total, average', () => {
    mockUseCompletionThroughput.mockReturnValue({ data: buildThroughputData(), isLoading: false, isError: false })

    render(<ThroughputChart />)

    const summary = screen.getByTestId('chart-sr-summary')
    expect(summary).toBeInTheDocument()
    expect(summary.textContent).toContain('Daily throughput bar chart')
    expect(summary.textContent).toContain('Jun 1')
    expect(summary.textContent).toContain('Total completed')
    expect(summary.textContent).toContain('Average completed')
    expect(summary.textContent).toContain('Peak day')
  })

  it('formats date-only bucket boundaries as local calendar labels without UTC day shift', () => {
    mockUseCompletionThroughput.mockReturnValue({ data: buildThroughputData(), isLoading: false, isError: false })

    render(<ThroughputChart />)

    expect(screen.getByText('Jun 1')).toBeInTheDocument()
    expect(screen.getByText('Jun 7')).toBeInTheDocument()
    expect(screen.getByTestId('chart-sr-summary').textContent).toContain('Jun 1 to Jun 7')
  })

  it('chart svg has role=img and aria-label', () => {
    mockUseCompletionThroughput.mockReturnValue({ data: buildThroughputData(), isLoading: false, isError: false })

    render(<ThroughputChart />)

    const svg = document.querySelector('svg[role="img"]')
    expect(svg).toBeInTheDocument()
    expect(svg).toHaveAttribute('aria-label')
    expect(svg!.getAttribute('aria-label')).toContain('Throughput trend')
  })

  // --- tabular-nums ---

  it('numeric labels use tabular-nums', () => {
    mockUseCompletionThroughput.mockReturnValue({ data: buildThroughputData(), isLoading: false, isError: false })

    render(<ThroughputChart />)

    const axisTexts = document.querySelectorAll('.tabular-nums')
    expect(axisTexts.length).toBeGreaterThan(0)
  })

  // --- Reduced motion ---

  it('segments have transform transition by default', () => {
    mockUseCompletionThroughput.mockReturnValue({ data: buildThroughputData(), isLoading: false, isError: false })

    render(<ThroughputChart />)

    const seg = screen.getByTestId('segment-0-0')
    expect(seg.style.transition).toContain('transform')
  })

  it('segments disable animation when prefers-reduced-motion: reduce', () => {
    setPrefersReducedMotion(true)
    mockUseCompletionThroughput.mockReturnValue({ data: buildThroughputData(), isLoading: false, isError: false })

    render(<ThroughputChart />)

    const seg = screen.getByTestId('segment-0-0')
    expect(seg.style.transition).toBe('none')

    setPrefersReducedMotion(false)
  })

  // --- Widget section ---

  it('renders within a section with testid and aria-label', () => {
    mockUseCompletionThroughput.mockReturnValue({ data: buildThroughputData(), isLoading: false, isError: false })

    render(<ThroughputChart />)

    const section = screen.getByTestId('throughput-chart')
    expect(section).toBeInTheDocument()
    expect(section).toHaveAttribute('aria-label', 'Throughput')
  })

  it('shows section heading', () => {
    mockUseCompletionThroughput.mockReturnValue({ data: buildThroughputData(), isLoading: false, isError: false })

    render(<ThroughputChart />)

    expect(screen.getByText('Throughput')).toBeInTheDocument()
  })

  // --- Day labels ---

  it('renders first and last day label', () => {
    mockUseCompletionThroughput.mockReturnValue({ data: buildThroughputData(), isLoading: false, isError: false })

    render(<ThroughputChart />)

    expect(screen.getByText('Jun 1')).toBeInTheDocument()
    expect(screen.getByText('Jun 7')).toBeInTheDocument()
  })

  it('renders fewer than 30 day labels for 30-day window', () => {
    mockUseCompletionThroughput.mockReturnValue({ data: build30dayData(), isLoading: false, isError: false })

    render(<ThroughputChart />)

    const labelTexts = document.querySelectorAll('svg text')
    const dayLabels = [...labelTexts].filter(t =>
      /^[A-Z][a-z]{2} \d{1,2}$/.test(t.textContent ?? ''),
    )
    expect(dayLabels.length).toBeGreaterThan(0)
    expect(dayLabels.length).toBeLessThan(30)
  })

  // --- Window annotation ---

  it('shows a 30d window badge in the header chrome', () => {
    mockUseCompletionThroughput.mockReturnValue({ data: buildThroughputData(), isLoading: false, isError: false })

    render(<ThroughputChart />)

    const badge = screen.getByTestId('throughput-chart-window')
    expect(badge).toBeInTheDocument()
    expect(badge.textContent).toBe('30d')
  })
})
