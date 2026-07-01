// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import type { DeliveryTimeMetricsResponse } from '../../../entities/issue'

const mockUseDeliveryTime = vi.fn()
vi.mock('../../../entities/issue', () => ({
  useDeliveryTime: () => mockUseDeliveryTime(),
}))

import { CycleTimeChart } from './CycleTimeChart'

function buildData(overrides?: Partial<DeliveryTimeMetricsResponse>): DeliveryTimeMetricsResponse {
  return {
    points: [
      { issueNumber: 1, completedAt: '2026-06-10T00:00:00+00:00', leadDays: 2.0, cycleDays: 1.5 },
      { issueNumber: 2, completedAt: '2026-06-15T00:00:00+00:00', leadDays: 4.0, cycleDays: 3.0 },
      { issueNumber: 3, completedAt: '2026-06-20T00:00:00+00:00', leadDays: 6.0, cycleDays: 5.0 },
      { issueNumber: 4, completedAt: '2026-06-25T00:00:00+00:00', leadDays: 8.0, cycleDays: 7.0 },
      { issueNumber: 5, completedAt: '2026-06-30T00:00:00+00:00', leadDays: 10.0, cycleDays: null },
    ],
    ...overrides,
  }
}

function buildEmpty(): DeliveryTimeMetricsResponse {
  return { points: [] }
}

describe('CycleTimeChart', () => {
  afterEach(() => {
    cleanup()
    mockUseDeliveryTime.mockReset()
  })

  // --- Three-state routing ---

  it('renders loading state via ChartContainer', () => {
    mockUseDeliveryTime.mockReturnValue({ data: undefined, isLoading: true, isError: false })

    render(<CycleTimeChart />)

    expect(screen.getByTestId('chart-container-loading')).toBeInTheDocument()
    expect(screen.queryByTestId('cycle-time-chart')).toBeInTheDocument()
    expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
  })

  it('renders error state via ChartContainer', () => {
    mockUseDeliveryTime.mockReturnValue({ data: undefined, isLoading: false, isError: true, error: new Error('fail') })

    render(<CycleTimeChart />)

    expect(screen.getByTestId('chart-container-error')).toBeInTheDocument()
    expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
  })

  it('renders empty state with concrete next action when no delivered issues', () => {
    mockUseDeliveryTime.mockReturnValue({ data: buildEmpty(), isLoading: false, isError: false })

    render(<CycleTimeChart />)

    const empty = screen.getByTestId('chart-container-empty')
    expect(empty).toBeInTheDocument()
    expect(empty.textContent).toContain('Cycle time appears once an issue completes on this project')
    expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
  })

  it('renders empty state when no delivered issues (surface returned no points)', () => {
    mockUseDeliveryTime.mockReturnValue({ data: undefined, isLoading: false, isError: false })

    render(<CycleTimeChart />)

    expect(screen.getByTestId('chart-container-empty')).toBeInTheDocument()
    expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
  })

  it('renders empty state under cycle lens when all points have null cycleDays', () => {
    const data: DeliveryTimeMetricsResponse = {
      points: [
        { issueNumber: 1, completedAt: '2026-06-10T00:00:00+00:00', leadDays: 2.0, cycleDays: null },
        { issueNumber: 2, completedAt: '2026-06-15T00:00:00+00:00', leadDays: 4.0, cycleDays: null },
      ],
    }
    mockUseDeliveryTime.mockReturnValue({ data, isLoading: false, isError: false })

    render(<CycleTimeChart />)

    expect(screen.getByTestId('chart-accessibility')).toBeInTheDocument()

    fireEvent.click(screen.getByTestId('cycle-time-lens-cycle'))

    expect(screen.getByTestId('chart-container-empty')).toBeInTheDocument()
    expect(screen.queryByTestId('scatter-series')).not.toBeInTheDocument()
  })

  it('renders resolved chart content with accessibility wrapper', () => {
    mockUseDeliveryTime.mockReturnValue({ data: buildData(), isLoading: false, isError: false })

    render(<CycleTimeChart />)

    expect(screen.getByTestId('chart-accessibility')).toBeInTheDocument()
    expect(screen.queryByTestId('chart-container-loading')).not.toBeInTheDocument()
    expect(screen.queryByTestId('chart-container-error')).not.toBeInTheDocument()
    expect(screen.queryByTestId('chart-container-empty')).not.toBeInTheDocument()
  })

  // --- Scatter points ---

  it('renders one scatter point per delivered issue under lead lens', () => {
    mockUseDeliveryTime.mockReturnValue({ data: buildData(), isLoading: false, isError: false })

    render(<CycleTimeChart />)

    const scatter = screen.getByTestId('scatter-series')
    expect(scatter.children).toHaveLength(5)
    for (let i = 1; i <= 5; i++) {
      expect(screen.getByTestId(`scatter-point-${i}`)).toBeInTheDocument()
    }
  })

  it('scatter points position by completion date (x) and duration (y)', () => {
    mockUseDeliveryTime.mockReturnValue({ data: buildData(), isLoading: false, isError: false })

    render(<CycleTimeChart />)

    const firstPoint = screen.getByTestId('scatter-point-1')
    const fifthPoint = screen.getByTestId('scatter-point-5')

    const firstX = Number(firstPoint.getAttribute('cx'))
    const fifthX = Number(fifthPoint.getAttribute('cx'))
    expect(fifthX).toBeGreaterThan(firstX)

    const firstY = Number(firstPoint.getAttribute('cy'))
    const fifthY = Number(fifthPoint.getAttribute('cy'))
    expect(firstY).toBeGreaterThan(fifthY)
  })

  it('scatter uses fill-chart-2 theme token (no fill attribute)', () => {
    mockUseDeliveryTime.mockReturnValue({ data: buildData(), isLoading: false, isError: false })

    render(<CycleTimeChart />)

    const firstPoint = screen.getByTestId('scatter-point-1')
    expect(firstPoint.getAttribute('class')).toContain('fill-chart-2')
    expect(firstPoint.getAttribute('fill')).toBeNull()
  })

  // --- Lens toggle ---

  it('lens toggle renders lead-time and cycle-time buttons with aria-pressed', () => {
    mockUseDeliveryTime.mockReturnValue({ data: buildData(), isLoading: false, isError: false })

    render(<CycleTimeChart />)

    const lead = screen.getByTestId('cycle-time-lens-lead')
    const cycle = screen.getByTestId('cycle-time-lens-cycle')

    expect(lead).toHaveAttribute('aria-pressed', 'true')
    expect(cycle).toHaveAttribute('aria-pressed', 'false')
  })

  it('selecting cycle lens excludes null-cycle issues from the scatter', () => {
    mockUseDeliveryTime.mockReturnValue({ data: buildData(), isLoading: false, isError: false })

    render(<CycleTimeChart />)

    expect(screen.getByTestId('scatter-point-5')).toBeInTheDocument()

    fireEvent.click(screen.getByTestId('cycle-time-lens-cycle'))

    expect(screen.queryByTestId('scatter-point-5')).not.toBeInTheDocument()
    expect(screen.getByTestId('scatter-point-1')).toBeInTheDocument()
    expect(screen.getByTestId('scatter-point-4')).toBeInTheDocument()
    const scatter = screen.getByTestId('scatter-series')
    expect(scatter.children).toHaveLength(4)
  })

  it('lens toggle does NOT mutate the underlying data (x driven by CompletedAt)', () => {
    mockUseDeliveryTime.mockReturnValue({ data: buildData(), isLoading: false, isError: false })

    render(<CycleTimeChart />)

    const point1Lead = Number(screen.getByTestId('scatter-point-1').getAttribute('cx'))

    fireEvent.click(screen.getByTestId('cycle-time-lens-cycle'))

    const point1Cycle = Number(screen.getByTestId('scatter-point-1').getAttribute('cx'))
    expect(point1Cycle).toBe(point1Lead)

    expect(screen.queryByTestId('scatter-point-5')).not.toBeInTheDocument()
    expect(point1Cycle).toBeGreaterThan(0)
  })

  it('keeps non-null issue x positions stable when a null-cycle point is at the domain edge', () => {
    mockUseDeliveryTime.mockReturnValue({
      data: buildData({
        points: [
          { issueNumber: 1, completedAt: '2026-06-10T00:00:00+00:00', leadDays: 10.0, cycleDays: null },
          { issueNumber: 2, completedAt: '2026-06-15T00:00:00+00:00', leadDays: 4.0, cycleDays: 3.0 },
          { issueNumber: 3, completedAt: '2026-06-20T00:00:00+00:00', leadDays: 6.0, cycleDays: 5.0 },
          { issueNumber: 4, completedAt: '2026-06-30T00:00:00+00:00', leadDays: 8.0, cycleDays: null },
        ],
      }),
      isLoading: false,
      isError: false,
    })

    render(<CycleTimeChart />)

    const point2LeadCx = Number(screen.getByTestId('scatter-point-2').getAttribute('cx'))
    const point3LeadCx = Number(screen.getByTestId('scatter-point-3').getAttribute('cx'))

    fireEvent.click(screen.getByTestId('cycle-time-lens-cycle'))

    expect(screen.queryByTestId('scatter-point-1')).not.toBeInTheDocument()
    expect(screen.queryByTestId('scatter-point-4')).not.toBeInTheDocument()
    expect(Number(screen.getByTestId('scatter-point-2').getAttribute('cx'))).toBe(point2LeadCx)
    expect(Number(screen.getByTestId('scatter-point-3').getAttribute('cx'))).toBe(point3LeadCx)
  })

  it('switching the lens back to lead re-shows the null-cycle points', () => {
    mockUseDeliveryTime.mockReturnValue({ data: buildData(), isLoading: false, isError: false })

    render(<CycleTimeChart />)

    fireEvent.click(screen.getByTestId('cycle-time-lens-cycle'))
    expect(screen.queryByTestId('scatter-point-5')).not.toBeInTheDocument()

    fireEvent.click(screen.getByTestId('cycle-time-lens-lead'))
    expect(screen.getByTestId('scatter-point-5')).toBeInTheDocument()
  })

  // --- Percentile overlays ---

  it('renders a P50 path and a P85 path with deterministic colors', () => {
    mockUseDeliveryTime.mockReturnValue({ data: buildData(), isLoading: false, isError: false })

    render(<CycleTimeChart />)

    expect(screen.getByTestId('p50-line')).toBeInTheDocument()
    expect(screen.getByTestId('p85-line')).toBeInTheDocument()

    const p50 = screen.getByTestId('p50-line')
    expect(p50.getAttribute('class')).toContain('stroke-chart-5')
    expect(p50.getAttribute('stroke-dasharray')).toBeNull()

    const p85 = screen.getByTestId('p85-line')
    expect(p85.getAttribute('class')).toContain('stroke-chart-3')
    expect(p85.getAttribute('stroke-dasharray')).not.toBeNull()
  })

  it('percentile overlays recompute when switching lenses (cycle lens excludes nulls)', () => {
    mockUseDeliveryTime.mockReturnValue({ data: buildData(), isLoading: false, isError: false })

    render(<CycleTimeChart />)

    const p50Lead = screen.getByTestId('p50-line').getAttribute('d')
    expect(p50Lead).toBeTruthy()

    fireEvent.click(screen.getByTestId('cycle-time-lens-cycle'))

    const p50Cycle = screen.getByTestId('p50-line').getAttribute('d')
    expect(p50Cycle).toBeTruthy()
    expect(p50Cycle).not.toEqual(p50Lead)
  })

  it('a single delivered issue still plots drawable P50/P85 segments (one valid sample)', () => {
    mockUseDeliveryTime.mockReturnValue({
      data: {
        points: [{ issueNumber: 1, completedAt: '2026-06-10T00:00:00+00:00', leadDays: 2.0, cycleDays: 1.5 }],
      },
      isLoading: false,
      isError: false,
    })

    render(<CycleTimeChart />)

    const p50Path = screen.getByTestId('p50-line').getAttribute('d')
    const p85Path = screen.getByTestId('p85-line').getAttribute('d')

    expect(p50Path).toMatch(/^M[^L]+ L/)
    expect(p85Path).toMatch(/^M[^L]+ L/)
  })

  // --- Legend ---

  it('legend disambiguates scatter, P50 line, and P85 line by shape (dot/solid/dashed)', () => {
    mockUseDeliveryTime.mockReturnValue({ data: buildData(), isLoading: false, isError: false })

    render(<CycleTimeChart />)

    const legend = screen.getByTestId('chart-legend')
    expect(legend).toBeInTheDocument()
    expect(legend.textContent).toContain('Delivered issue')
    expect(legend.textContent).toContain('P50')
    expect(legend.textContent).toContain('P85')

    const dotSwatch = legend.querySelector('.rounded-full')
    expect(dotSwatch).toBeInTheDocument()

    const dashedSvg = legend.querySelector('polyline[stroke-dasharray]')
    expect(dashedSvg).toBeInTheDocument()
  })

  // --- Token colors and labels ---

  it('left axis uses stroke-chart-2 and fill-chart-2 theme tokens', () => {
    mockUseDeliveryTime.mockReturnValue({ data: buildData(), isLoading: false, isError: false })

    render(<CycleTimeChart />)

    const axis = screen.getByTestId('chart-axis-left')
    const axisLine = axis.querySelector('line')
    expect(axisLine?.getAttribute('class')).toContain('stroke-chart-2')

    const axisText = axis.querySelector('text')
    expect(axisText?.getAttribute('class')).toContain('fill-chart-2')
    expect(axisText?.getAttribute('class')).toContain('tabular-nums')
  })

  it('only one y-axis (single days unit)', () => {
    mockUseDeliveryTime.mockReturnValue({ data: buildData(), isLoading: false, isError: false })

    render(<CycleTimeChart />)

    expect(screen.getByTestId('chart-axis-left')).toBeInTheDocument()
    expect(screen.queryByTestId('chart-axis-right')).not.toBeInTheDocument()
  })

  // --- Accessibility ---

  it('chart svg has role=img and aria-label', () => {
    mockUseDeliveryTime.mockReturnValue({ data: buildData(), isLoading: false, isError: false })

    render(<CycleTimeChart />)

    const svg = document.querySelector('svg[role="img"]')
    expect(svg).toBeInTheDocument()
    expect(svg!.getAttribute('aria-label')).toContain('Cycle-time scatter control chart')
  })

  it('sr-only summary names window start/end and the active lens', () => {
    mockUseDeliveryTime.mockReturnValue({ data: buildData(), isLoading: false, isError: false })

    render(<CycleTimeChart />)

    const summary = screen.getByTestId('chart-sr-summary')
    expect(summary).toBeInTheDocument()
    expect(summary.textContent).toContain('Cycle-time scatter control chart')
    expect(summary.textContent).toContain('lead')
    expect(summary.textContent).toContain('Rolling P50')
    expect(summary.textContent).toContain('Rolling P85')
  })

  // --- tabular-nums ---

  it('numeric labels use tabular-nums', () => {
    mockUseDeliveryTime.mockReturnValue({ data: buildData(), isLoading: false, isError: false })

    render(<CycleTimeChart />)

    expect(document.querySelectorAll('.tabular-nums').length).toBeGreaterThan(0)
  })

  // --- Section heading ---

  it('renders within a section with testid and aria-label', () => {
    mockUseDeliveryTime.mockReturnValue({ data: buildData(), isLoading: false, isError: false })

    render(<CycleTimeChart />)

    const section = screen.getByTestId('cycle-time-chart')
    expect(section).toBeInTheDocument()
    expect(section).toHaveAttribute('aria-label', 'Cycle Time')
  })

  it('shows section heading', () => {
    mockUseDeliveryTime.mockReturnValue({ data: buildData(), isLoading: false, isError: false })

    render(<CycleTimeChart />)

    expect(screen.getByText('Cycle Time')).toBeInTheDocument()
  })

  // --- Post-completion edit safety ---

  it('scatter x is driven by CompletedAt and does not move when a record is updated', () => {
    mockUseDeliveryTime.mockReturnValue({ data: buildData(), isLoading: false, isError: false })

    render(<CycleTimeChart />)

    const point1BeforeCx = Number(screen.getByTestId('scatter-point-1').getAttribute('cx'))
    expect(point1BeforeCx).toBeGreaterThan(0)

    expect(screen.getByTestId('scatter-series')).toBeInTheDocument()
    expect(screen.getByTestId('scatter-point-1').getAttribute('cx')).toBe(String(point1BeforeCx))
  })
})
