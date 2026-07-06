// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { setPrefersReducedMotion } from '../../../../tests/setup'
import type { QualityMetricsResponse, QualityTrendDto, QualityTrendPointDto } from '../../../entities/issue'

const useQualityMetricsMock = vi.fn()
vi.mock('../../../entities/issue/api/quality-metrics', () => ({
  useQualityMetrics: (...args: unknown[]) => useQualityMetricsMock(...args),
}))

import { FtrTrendChart } from './FtrTrendChart'

function makeWindow(sampleCount: number, firstTimeRightRate: number | null) {
  return {
    from: '2026-06-01T00:00:00+00:00',
    to: '2026-06-30T23:59:59+00:00',
    sampleCount,
    firstTimeRightRate,
    stages: [],
  }
}

function makePoint(
  boundary: string,
  sampleCount: number,
  ftr: number | null,
  rework: number | null,
): QualityTrendPointDto {
  return { boundary, sampleCount, firstTimeRightRate: ftr, reworkRate: rework }
}

function buildTrendData(): QualityMetricsResponse {
  const points: QualityTrendPointDto[] = [
    makePoint('2026-06-01', 0, null, null),
    makePoint('2026-06-02', 3, 0.66, 0.33),
    makePoint('2026-06-03', 0, null, null),
    makePoint('2026-06-04', 5, 0.8, 0.2),
    makePoint('2026-06-05', 4, 0.5, 0.5),
    makePoint('2026-06-06', 0, null, null),
    makePoint('2026-06-07', 2, 1.0, 0.0),
  ]
  return {
    window: makeWindow(20, 0.65),
    trend: {
      bucket: 'day',
      from: '2026-06-01T00:00:00+00:00',
      to: '2026-06-30T23:59:59+00:00',
      points,
    },
  }
}

function renderChart() {
  return render(<FtrTrendChart range="30d" />)
}

describe('FtrTrendChart', () => {
  afterEach(() => {
    cleanup()
    useQualityMetricsMock.mockReset()
  })

  // --- Three-state routing ---

  it('renders loading state via ChartContainer', () => {
    useQualityMetricsMock.mockReturnValue({ data: undefined, isLoading: true, isError: false })

    renderChart()

    expect(screen.getByTestId('chart-container-loading')).toBeInTheDocument()
    expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
  })

  it('renders error state via ChartContainer', () => {
    useQualityMetricsMock.mockReturnValue({
      data: undefined,
      isLoading: false,
      isError: true,
      error: new Error('fail'),
    })

    renderChart()

    expect(screen.getByTestId('chart-container-error')).toBeInTheDocument()
    expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
  })

  it('renders empty state with next action when trend is missing', () => {
    useQualityMetricsMock.mockReturnValue({
      data: {
        window: makeWindow(20, 0.65),
      },
      isLoading: false,
      isError: false,
    })

    renderChart()

    const empty = screen.getByTestId('chart-container-empty')
    expect(empty).toBeInTheDocument()
    expect(empty.textContent).toContain('once an issue ships within the trailing window')
    expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
  })

  it('renders empty state when all trend points have zero samples (graceful older server)', () => {
    const emptyTrend: QualityTrendDto = {
      bucket: 'day',
      from: '2026-06-01T00:00:00+00:00',
      to: '2026-06-30T23:59:59+00:00',
      points: Array.from({ length: 30 }, (_, i) =>
        makePoint(`2026-06-${String(i + 1).padStart(2, '0')}`, 0, null, null),
      ),
    }
    useQualityMetricsMock.mockReturnValue({
      data: {
        window: makeWindow(0, null),
        trend: emptyTrend,
      },
      isLoading: false,
      isError: false,
    })

    renderChart()

    const empty = screen.getByTestId('chart-container-empty')
    expect(empty).toBeInTheDocument()
    expect(empty.textContent).toContain('once an issue ships within the trailing window')
    expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
  })

  it('treats an undefined trend as empty (graceful degradation against older server)', () => {
    useQualityMetricsMock.mockReturnValue({
      data: {
        window: makeWindow(20, 0.65),
      } as QualityMetricsResponse,
      isLoading: false,
      isError: false,
    })

    renderChart()

    const empty = screen.getByTestId('chart-container-empty')
    expect(empty).toBeInTheDocument()
    expect(empty.textContent).toContain('once an issue ships within the trailing window')
  })

  it('renders resolved chart content with accessibility wrapper', () => {
    useQualityMetricsMock.mockReturnValue({
      data: buildTrendData(),
      isLoading: false,
      isError: false,
    })

    renderChart()

    expect(screen.getByTestId('chart-accessibility')).toBeInTheDocument()
    expect(screen.queryByTestId('chart-container-loading')).not.toBeInTheDocument()
    expect(screen.queryByTestId('chart-container-error')).not.toBeInTheDocument()
    expect(screen.queryByTestId('chart-container-empty')).not.toBeInTheDocument()
  })

  // --- FTR line series ---

  it('renders one marker per non-null FTR bucket sourced from the per-bucket series', () => {
    useQualityMetricsMock.mockReturnValue({
      data: buildTrendData(),
      isLoading: false,
      isError: false,
    })

    renderChart()

    const lineSeries = screen.getByTestId('line-series')
    const markers = lineSeries.querySelectorAll('circle')
    expect(markers).toHaveLength(4)
  })

  it('null-bucket produces no marker and gaps the line (no 0%/100% plotted)', () => {
    useQualityMetricsMock.mockReturnValue({
      data: buildTrendData(),
      isLoading: false,
      isError: false,
    })

    renderChart()

    const lineSeries = screen.getByTestId('line-series')
    const segments = lineSeries.querySelectorAll('path')
    const firstSegment = segments[0] as unknown as SVGPathElement
    expect(segments.length).toBeGreaterThan(1)
    expect(firstSegment.getAttribute('d')).not.toContain('NaN')
  })

  it('FTR line uses stroke-chart-5 theme token', () => {
    useQualityMetricsMock.mockReturnValue({
      data: buildTrendData(),
      isLoading: false,
      isError: false,
    })

    renderChart()

    const path = screen.getByTestId('line-series').querySelector('path')
    expect(path?.getAttribute('class')).toContain('stroke-chart-5')
    expect(path?.getAttribute('class')).toContain('fill-none')
  })

  it('axis renders with fixed 0/25/50/75/100 percentage ticks', () => {
    useQualityMetricsMock.mockReturnValue({
      data: buildTrendData(),
      isLoading: false,
      isError: false,
    })

    renderChart()

    const axis = screen.getByTestId('chart-axis-left')
    const tickLabels = axis.querySelectorAll('text')
    const texts = [...tickLabels].map((t) => t.textContent)
    expect(texts).toContain('0%')
    expect(texts).toContain('25%')
    expect(texts).toContain('50%')
    expect(texts).toContain('75%')
    expect(texts).toContain('100%')
  })

  // --- Legend / accessibility ---

  it('hides the legend when overlay is off (single series)', () => {
    useQualityMetricsMock.mockReturnValue({
      data: buildTrendData(),
      isLoading: false,
      isError: false,
    })

    renderChart()

    expect(screen.queryByTestId('chart-legend')).not.toBeInTheDocument()
  })

  it('legend distinguishes FTR from rework by shape (line vs dashedLine)', () => {
    useQualityMetricsMock.mockReturnValue({
      data: buildTrendData(),
      isLoading: false,
      isError: false,
    })

    renderChart()

    fireEvent.click(screen.getByLabelText('Toggle rework rate overlay'))

    const legend = screen.getByTestId('chart-legend')
    expect(legend.textContent).toContain('First-time-right')
    expect(legend.textContent).toContain('Rework rate')

    const ftrEntry = [...legend.querySelectorAll('span')].find(
      (e) => e.textContent === 'First-time-right',
    )
    const reworkEntry = [...legend.querySelectorAll('span')].find(
      (e) => e.textContent === 'Rework rate',
    )
    expect(ftrEntry).toBeTruthy()
    expect(reworkEntry).toBeTruthy()

    const ftrSvg = ftrEntry!.querySelector('svg')
    const reworkSvg = reworkEntry!.querySelector('svg')
    expect(ftrSvg).toBeTruthy()
    expect(reworkSvg).toBeTruthy()

    const ftrPolyline = ftrSvg!.querySelector('polyline')
    const reworkPolyline = reworkSvg!.querySelector('polyline')
    expect(ftrPolyline).toBeTruthy()
    expect(reworkPolyline).toBeTruthy()
    expect(ftrPolyline!.getAttribute('stroke-dasharray')).toBeNull()
    expect(reworkPolyline!.getAttribute('stroke-dasharray')).toBe('2 2')
  })

  it('SR summary describes the window, first/last FTR, and peak rework day', () => {
    useQualityMetricsMock.mockReturnValue({
      data: buildTrendData(),
      isLoading: false,
      isError: false,
    })

    renderChart()

    fireEvent.click(screen.getByLabelText('Toggle rework rate overlay'))

    const summary = screen.getByTestId('chart-sr-summary')
    expect(summary).toBeInTheDocument()
    expect(summary.textContent).toContain('Jun 1')
    expect(summary.textContent).toContain('Jun 7')
    expect(summary.textContent).toContain('First-time-right from')
    expect(summary.textContent).toContain('Peak rework day')
  })

  it('SVG has role=img and aria-label naming the FTR trend', () => {
    useQualityMetricsMock.mockReturnValue({
      data: buildTrendData(),
      isLoading: false,
      isError: false,
    })

    renderChart()

    const svg = document.querySelector('svg[role="img"]')
    expect(svg).toBeInTheDocument()
    expect(svg).toHaveAttribute('aria-label')
    expect(svg!.getAttribute('aria-label')).toContain('First-time-right')
  })

  // --- Rework overlay ---

  it('does not render a second LineSeries when overlay is off', () => {
    useQualityMetricsMock.mockReturnValue({
      data: buildTrendData(),
      isLoading: false,
      isError: false,
    })

    renderChart()

    expect(screen.getAllByTestId('line-series')).toHaveLength(1)
  })

  it('toggling the overlay renders the rework LineSeries on the same axis', () => {
    useQualityMetricsMock.mockReturnValue({
      data: buildTrendData(),
      isLoading: false,
      isError: false,
    })

    renderChart()

    expect(screen.getAllByTestId('line-series')).toHaveLength(1)

    fireEvent.click(screen.getByLabelText('Toggle rework rate overlay'))

    expect(screen.getAllByTestId('line-series')).toHaveLength(2)
  })

  it('rework LineSeries uses stroke-chart-4, a dashed path, and per-bucket rates from the series', () => {
    useQualityMetricsMock.mockReturnValue({
      data: buildTrendData(),
      isLoading: false,
      isError: false,
    })

    renderChart()

    fireEvent.click(screen.getByLabelText('Toggle rework rate overlay'))

    const seriesGroups = screen.getAllByTestId('line-series')
    const reworkGroup = seriesGroups[1]
    const reworkPath = reworkGroup.querySelector('path')
    expect(reworkPath?.getAttribute('class')).toContain('stroke-chart-4')
    expect(reworkPath?.getAttribute('class')).toContain('fill-none')
    expect(reworkPath?.getAttribute('stroke-dasharray')).toBe('2 2')

    const ftrMarkers = seriesGroups[0].querySelectorAll('circle')
    const reworkMarkers = reworkGroup.querySelectorAll('circle')
    expect(reworkMarkers.length).toBe(4)
    expect(reworkMarkers.length).toBe(ftrMarkers.length)
  })

  it('overlay toggle is disabled when the series carries no rework samples', () => {
    const noReworkTrend: QualityTrendDto = {
      bucket: 'day',
      from: '2026-06-01T00:00:00+00:00',
      to: '2026-06-30T23:59:59+00:00',
      points: [
        makePoint('2026-06-01', 3, 0.66, null),
        makePoint('2026-06-02', 2, 1.0, null),
      ],
    }
    useQualityMetricsMock.mockReturnValue({
      data: {
        window: makeWindow(5, 0.8),
        trend: noReworkTrend,
      },
      isLoading: false,
      isError: false,
    })

    renderChart()

    const toggle = screen.getByLabelText('Toggle rework rate overlay') as HTMLInputElement
    expect(toggle.disabled).toBe(true)
  })

  it('overlay toggle persists across re-renders via local state', () => {
    useQualityMetricsMock.mockReturnValue({
      data: buildTrendData(),
      isLoading: false,
      isError: false,
    })

    const { rerender } = renderChart()

    fireEvent.click(screen.getByLabelText('Toggle rework rate overlay'))
    expect(screen.getAllByTestId('line-series')).toHaveLength(2)

    rerender(<FtrTrendChart range="30d" />)
    expect(screen.getAllByTestId('line-series')).toHaveLength(2)
  })

  // --- Conventions ---

  it('numeric labels use tabular-nums', () => {
    useQualityMetricsMock.mockReturnValue({
      data: buildTrendData(),
      isLoading: false,
      isError: false,
    })

    renderChart()

    expect(document.querySelectorAll('.tabular-nums').length).toBeGreaterThan(0)
  })

  it('line markers apply transition (transform-based motion) by default', () => {
    useQualityMetricsMock.mockReturnValue({
      data: buildTrendData(),
      isLoading: false,
      isError: false,
    })

    renderChart()

    const marker = screen.getByTestId('line-series').querySelector('circle')
    expect(marker).toBeTruthy()
    expect((marker as SVGElement).style.transition).toContain('opacity')
  })

  it('line markers disable animation when prefers-reduced-motion: reduce', () => {
    setPrefersReducedMotion(true)

    useQualityMetricsMock.mockReturnValue({
      data: buildTrendData(),
      isLoading: false,
      isError: false,
    })

    renderChart()

    const marker = screen.getByTestId('line-series').querySelector('circle')
    expect(marker).toBeTruthy()
    expect((marker as SVGElement).style.transition).toBe('')

    setPrefersReducedMotion(false)
  })

  // --- Section shape ---

  it('renders within a section with testid', () => {
    useQualityMetricsMock.mockReturnValue({
      data: buildTrendData(),
      isLoading: false,
      isError: false,
    })

    renderChart()

    const section = screen.getByTestId('ftr-trend-chart')
    expect(section).toBeInTheDocument()
    expect(section).toHaveAttribute('aria-label', 'First-Time-Right Trend')
  })

  it('shows section heading', () => {
    useQualityMetricsMock.mockReturnValue({
      data: buildTrendData(),
      isLoading: false,
      isError: false,
    })

    renderChart()

    expect(screen.getByText('First-Time-Right Trend')).toBeInTheDocument()
  })

  it('renders day labels for the trailing buckets', () => {
    useQualityMetricsMock.mockReturnValue({
      data: buildTrendData(),
      isLoading: false,
      isError: false,
    })

    renderChart()

    expect(screen.getByText('Jun 1')).toBeInTheDocument()
    expect(screen.getByText('Jun 7')).toBeInTheDocument()
  })

  // --- Window annotation ---

  it('renders a window badge derived from trend.from/trend.to when trend is present', () => {
    useQualityMetricsMock.mockReturnValue({
      data: buildTrendData(),
      isLoading: false,
      isError: false,
    })

    renderChart()

    const badge = screen.getByTestId('ftr-trend-chart-window')
    expect(badge).toBeInTheDocument()
    expect(badge.textContent).toContain('Jun 1')
    expect(badge.textContent).toContain('Jun 30')
  })

  it('hides the window badge in the empty state when trend is missing', () => {
    useQualityMetricsMock.mockReturnValue({
      data: {
        window: makeWindow(20, 0.65),
      },
      isLoading: false,
      isError: false,
    })

    renderChart()

    expect(screen.getByTestId('chart-container-empty')).toBeInTheDocument()
    expect(screen.queryByTestId('ftr-trend-chart-window')).not.toBeInTheDocument()
  })
})
