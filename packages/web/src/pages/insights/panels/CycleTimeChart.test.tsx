import '@testing-library/jest-dom'
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { TEST_PROJECT } from '../../../../tests/test-utils'
import type { DeliveryTimeMetricsResponse } from '../../../entities/issue'

import { CycleTimeChart, type DeliveryTimeHook } from './CycleTimeChart'

type RangeCode = '7d' | '30d' | '90d'

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

let deliveryTimeResult: ReturnType<DeliveryTimeHook>

const deliveryTimeHook: DeliveryTimeHook = () => deliveryTimeResult

function mockDeliveryResponse(data: DeliveryTimeMetricsResponse) {
  deliveryTimeResult = { data, isLoading: false, isError: false }
}

function mockDeliveryPending() {
  deliveryTimeResult = { data: undefined, isLoading: true, isError: false }
}

function mockDeliveryError() {
  deliveryTimeResult = { data: undefined, isLoading: false, isError: true }
}

function renderChart(range: RangeCode = '30d') {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
        <MemoryRouter initialEntries={['/']}>
          <CycleTimeChart deliveryTimeHook={deliveryTimeHook} range={range} />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('CycleTimeChart', () => {
  afterEach(() => {
    cleanup()
  })

  // --- Three-state routing ---

  it('renders loading state via ChartContainer', () => {
    mockDeliveryPending()

    renderChart()

    expect(screen.getByTestId('chart-container-loading')).toBeInTheDocument()
    expect(screen.queryByTestId('cycle-time-chart')).toBeInTheDocument()
    expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
  })

  it('renders error state via ChartContainer', async () => {
    mockDeliveryError()

    renderChart()

    await waitFor(() => {
      expect(screen.getByTestId('chart-container-error')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
  })

  it('renders empty state with concrete next action when no delivered issues', async () => {
    mockDeliveryResponse(buildEmpty())

    renderChart()

    await waitFor(() => {
      const empty = screen.getByTestId('chart-container-empty')
      expect(empty).toBeInTheDocument()
      expect(empty.textContent).toContain('Cycle time appears once an issue completes on this project')
    })
    expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
  })

  it('renders empty state when no delivered issues (surface returned no points)', async () => {
    mockDeliveryResponse(buildEmpty())

    renderChart()

    await waitFor(() => {
      expect(screen.getByTestId('chart-container-empty')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
  })

  it('renders empty state under cycle lens when all points have null cycleDays', async () => {
    const data: DeliveryTimeMetricsResponse = {
      points: [
        { issueNumber: 1, completedAt: '2026-06-10T00:00:00+00:00', leadDays: 2.0, cycleDays: null },
        { issueNumber: 2, completedAt: '2026-06-15T00:00:00+00:00', leadDays: 4.0, cycleDays: null },
      ],
    }
    mockDeliveryResponse(data)

    renderChart()

    await waitFor(() => {
      expect(screen.getByTestId('chart-accessibility')).toBeInTheDocument()
    })

    fireEvent.click(screen.getByTestId('cycle-time-lens-cycle'))

    expect(screen.getByTestId('chart-container-empty')).toBeInTheDocument()
    expect(screen.queryByTestId('scatter-series')).not.toBeInTheDocument()
  })

  it('renders resolved chart content with accessibility wrapper', async () => {
    mockDeliveryResponse(buildData())

    renderChart()

    await waitFor(() => {
      expect(screen.getByTestId('chart-accessibility')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('chart-container-loading')).not.toBeInTheDocument()
    expect(screen.queryByTestId('chart-container-error')).not.toBeInTheDocument()
    expect(screen.queryByTestId('chart-container-empty')).not.toBeInTheDocument()
  })

  // --- Scatter points ---

  it('renders one scatter point per delivered issue under lead lens', async () => {
    mockDeliveryResponse(buildData())

    renderChart()

    await waitFor(() => {
      const scatter = screen.getByTestId('scatter-series')
      expect(scatter.children).toHaveLength(5)
      for (let i = 1; i <= 5; i++) {
        expect(screen.getByTestId(`scatter-point-${i}`)).toBeInTheDocument()
      }
    })
  })

  it('scatter points position by completion date (x) and duration (y)', async () => {
    mockDeliveryResponse(buildData())

    renderChart()

    await waitFor(() => {
      const firstPoint = screen.getByTestId('scatter-point-1')
      const fifthPoint = screen.getByTestId('scatter-point-5')

      const firstX = Number(firstPoint.getAttribute('cx'))
      const fifthX = Number(fifthPoint.getAttribute('cx'))
      expect(fifthX).toBeGreaterThan(firstX)

      const firstY = Number(firstPoint.getAttribute('cy'))
      const fifthY = Number(fifthPoint.getAttribute('cy'))
      expect(firstY).toBeGreaterThan(fifthY)
    })
  })

  it('scatter uses fill-chart-2 theme token (no fill attribute)', async () => {
    mockDeliveryResponse(buildData())

    renderChart()

    await waitFor(() => {
      const firstPoint = screen.getByTestId('scatter-point-1')
      expect(firstPoint.getAttribute('class')).toContain('fill-chart-2')
      expect(firstPoint.getAttribute('fill')).toBeNull()
    })
  })

  // --- Lens toggle ---

  it('lens toggle renders lead-time and cycle-time buttons with aria-pressed', async () => {
    mockDeliveryResponse(buildData())

    renderChart()

    await waitFor(() => {
      const lead = screen.getByTestId('cycle-time-lens-lead')
      const cycle = screen.getByTestId('cycle-time-lens-cycle')

      expect(lead).toHaveAttribute('aria-pressed', 'true')
      expect(cycle).toHaveAttribute('aria-pressed', 'false')
    })
  })

  it('selecting cycle lens excludes null-cycle issues from the scatter', async () => {
    mockDeliveryResponse(buildData())

    renderChart()

    await waitFor(() => {
      expect(screen.getByTestId('scatter-point-5')).toBeInTheDocument()
    })

    fireEvent.click(screen.getByTestId('cycle-time-lens-cycle'))

    expect(screen.queryByTestId('scatter-point-5')).not.toBeInTheDocument()
    expect(screen.getByTestId('scatter-point-1')).toBeInTheDocument()
    expect(screen.getByTestId('scatter-point-4')).toBeInTheDocument()
    const scatter = screen.getByTestId('scatter-series')
    expect(scatter.children).toHaveLength(4)
  })

  it('lens toggle does NOT mutate the underlying data (x driven by CompletedAt)', async () => {
    mockDeliveryResponse(buildData())

    renderChart()

    await waitFor(() => {
      expect(screen.getByTestId('scatter-point-1')).toBeInTheDocument()
    })
    const point1Lead = Number(screen.getByTestId('scatter-point-1').getAttribute('cx'))

    fireEvent.click(screen.getByTestId('cycle-time-lens-cycle'))

    const point1Cycle = Number(screen.getByTestId('scatter-point-1').getAttribute('cx'))
    expect(point1Cycle).toBe(point1Lead)

    expect(screen.queryByTestId('scatter-point-5')).not.toBeInTheDocument()
    expect(point1Cycle).toBeGreaterThan(0)
  })

  it('keeps non-null issue x positions stable when a null-cycle point is at the domain edge', async () => {
    mockDeliveryResponse(buildData({
      points: [
        { issueNumber: 1, completedAt: '2026-06-10T00:00:00+00:00', leadDays: 10.0, cycleDays: null },
        { issueNumber: 2, completedAt: '2026-06-15T00:00:00+00:00', leadDays: 4.0, cycleDays: 3.0 },
        { issueNumber: 3, completedAt: '2026-06-20T00:00:00+00:00', leadDays: 6.0, cycleDays: 5.0 },
        { issueNumber: 4, completedAt: '2026-06-30T00:00:00+00:00', leadDays: 8.0, cycleDays: null },
      ],
    }))

    renderChart()

    await waitFor(() => {
      expect(screen.getByTestId('scatter-point-2')).toBeInTheDocument()
    })
    const point2LeadCx = Number(screen.getByTestId('scatter-point-2').getAttribute('cx'))
    const point3LeadCx = Number(screen.getByTestId('scatter-point-3').getAttribute('cx'))

    fireEvent.click(screen.getByTestId('cycle-time-lens-cycle'))

    expect(screen.queryByTestId('scatter-point-1')).not.toBeInTheDocument()
    expect(screen.queryByTestId('scatter-point-4')).not.toBeInTheDocument()
    expect(Number(screen.getByTestId('scatter-point-2').getAttribute('cx'))).toBe(point2LeadCx)
    expect(Number(screen.getByTestId('scatter-point-3').getAttribute('cx'))).toBe(point3LeadCx)
  })

  it('switching the lens back to lead re-shows the null-cycle points', async () => {
    mockDeliveryResponse(buildData())

    renderChart()

    await waitFor(() => {
      expect(screen.getByTestId('scatter-point-1')).toBeInTheDocument()
    })

    fireEvent.click(screen.getByTestId('cycle-time-lens-cycle'))
    expect(screen.queryByTestId('scatter-point-5')).not.toBeInTheDocument()

    fireEvent.click(screen.getByTestId('cycle-time-lens-lead'))
    expect(screen.getByTestId('scatter-point-5')).toBeInTheDocument()
  })

  // --- Percentile overlays ---

  it('renders a P50 path and a P85 path with deterministic colors', async () => {
    mockDeliveryResponse(buildData())

    renderChart()

    await waitFor(() => {
      expect(screen.getByTestId('p50-line')).toBeInTheDocument()
      expect(screen.getByTestId('p85-line')).toBeInTheDocument()

      const p50 = screen.getByTestId('p50-line')
      expect(p50.getAttribute('class')).toContain('stroke-chart-5')
      expect(p50.getAttribute('stroke-dasharray')).toBeNull()

      const p85 = screen.getByTestId('p85-line')
      expect(p85.getAttribute('class')).toContain('stroke-chart-3')
      expect(p85.getAttribute('stroke-dasharray')).not.toBeNull()
    })
  })

  it('percentile overlays recompute when switching lenses (cycle lens excludes nulls)', async () => {
    mockDeliveryResponse(buildData())

    renderChart()

    await waitFor(() => {
      const p50Lead = screen.getByTestId('p50-line').getAttribute('d')
      expect(p50Lead).toBeTruthy()
    })

    const p50Lead = screen.getByTestId('p50-line').getAttribute('d')

    fireEvent.click(screen.getByTestId('cycle-time-lens-cycle'))

    const p50Cycle = screen.getByTestId('p50-line').getAttribute('d')
    expect(p50Cycle).toBeTruthy()
    expect(p50Cycle).not.toEqual(p50Lead)
  })

  it('a single delivered issue still plots drawable P50/P85 segments (one valid sample)', async () => {
    mockDeliveryResponse({
      points: [{ issueNumber: 1, completedAt: '2026-06-10T00:00:00+00:00', leadDays: 2.0, cycleDays: 1.5 }],
    })

    renderChart()

    await waitFor(() => {
      const p50Path = screen.getByTestId('p50-line').getAttribute('d')
      const p85Path = screen.getByTestId('p85-line').getAttribute('d')

      expect(p50Path).toMatch(/^M[^L]+ L/)
      expect(p85Path).toMatch(/^M[^L]+ L/)
    })
  })

  // --- Legend ---

  it('legend disambiguates scatter, P50 line, and P85 line by shape (dot/solid/dashed)', async () => {
    mockDeliveryResponse(buildData())

    renderChart()

    await waitFor(() => {
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
  })

  // --- Token colors and labels ---

  it('left axis uses stroke-chart-2 and fill-chart-2 theme tokens', async () => {
    mockDeliveryResponse(buildData())

    renderChart()

    await waitFor(() => {
      const axis = screen.getByTestId('chart-axis-left')
      const axisLine = axis.querySelector('line')
      expect(axisLine?.getAttribute('class')).toContain('stroke-chart-2')

      const axisText = axis.querySelector('text')
      expect(axisText?.getAttribute('class')).toContain('fill-chart-2')
      expect(axisText?.getAttribute('class')).toContain('tabular-nums')
    })
  })

  it('only one y-axis (single days unit)', async () => {
    mockDeliveryResponse(buildData())

    renderChart()

    await waitFor(() => {
      expect(screen.getByTestId('chart-axis-left')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('chart-axis-right')).not.toBeInTheDocument()
  })

  // --- Accessibility ---

  it('chart svg has role=img and aria-label', async () => {
    mockDeliveryResponse(buildData())

    renderChart()

    await waitFor(() => {
      const svg = document.querySelector('svg[role="img"]')
      expect(svg).toBeInTheDocument()
      expect(svg!.getAttribute('aria-label')).toContain('Cycle-time scatter control chart')
    })
  })

  it('sr-only summary names window start/end and the active lens', async () => {
    mockDeliveryResponse(buildData())

    renderChart()

    await waitFor(() => {
      const summary = screen.getByTestId('chart-sr-summary')
      expect(summary).toBeInTheDocument()
      expect(summary.textContent).toContain('Cycle-time scatter control chart')
      expect(summary.textContent).toContain('lead')
      expect(summary.textContent).toContain('Rolling P50')
      expect(summary.textContent).toContain('Rolling P85')
    })
  })

  // --- tabular-nums ---

  it('numeric labels use tabular-nums', async () => {
    mockDeliveryResponse(buildData())

    renderChart()

    await waitFor(() => {
      expect(document.querySelectorAll('.tabular-nums').length).toBeGreaterThan(0)
    })
  })

  // --- Section heading ---

  it('renders within a section with testid and aria-label', async () => {
    mockDeliveryResponse(buildData())

    renderChart()

    await waitFor(() => {
      const section = screen.getByTestId('cycle-time-chart')
      expect(section).toBeInTheDocument()
      expect(section).toHaveAttribute('aria-label', 'Cycle Time')
    })
  })

  it('shows section heading', async () => {
    mockDeliveryResponse(buildData())

    renderChart()

    await waitFor(() => {
      expect(screen.getByText('Lead Time')).toBeInTheDocument()
    })
  })

  it('renders the default lead-lens title aligned with the default lens caliber', async () => {
    mockDeliveryResponse(buildData())

    renderChart()

    await waitFor(() => {
      const heading = screen.getByRole('heading', { level: 3, name: 'Lead Time' })
      expect(heading).toBeInTheDocument()

      expect(screen.getByTestId('cycle-time-lens-lead')).toHaveAttribute('aria-pressed', 'true')
      expect(screen.getByTestId('cycle-time-lens-cycle')).toHaveAttribute('aria-pressed', 'false')
    })
  })

  it('updates the card title to match the active lens when switching to cycle', async () => {
    mockDeliveryResponse(buildData())

    renderChart()

    await waitFor(() => {
      expect(screen.getByRole('heading', { level: 3, name: 'Lead Time' })).toBeInTheDocument()
    })

    fireEvent.click(screen.getByTestId('cycle-time-lens-cycle'))

    expect(screen.getByRole('heading', { level: 3, name: 'Cycle Time' })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { level: 3, name: 'Lead Time' })).not.toBeInTheDocument()
  })

  it('restores the lead-lens title when toggling back to lead', async () => {
    mockDeliveryResponse(buildData())

    renderChart()

    await waitFor(() => {
      expect(screen.getByRole('heading', { level: 3, name: 'Lead Time' })).toBeInTheDocument()
    })

    fireEvent.click(screen.getByTestId('cycle-time-lens-cycle'))
    expect(screen.getByRole('heading', { level: 3, name: 'Cycle Time' })).toBeInTheDocument()

    fireEvent.click(screen.getByTestId('cycle-time-lens-lead'))
    expect(screen.getByRole('heading', { level: 3, name: 'Lead Time' })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { level: 3, name: 'Cycle Time' })).not.toBeInTheDocument()
  })

  it('keeps the section aria-label stable across lens switches (h3 tracks the lens instead)', async () => {
    mockDeliveryResponse(buildData())

    renderChart()

    await waitFor(() => {
      expect(screen.getByTestId('cycle-time-chart')).toHaveAttribute('aria-label', 'Cycle Time')
    })

    fireEvent.click(screen.getByTestId('cycle-time-lens-cycle'))
    expect(screen.getByTestId('cycle-time-chart')).toHaveAttribute('aria-label', 'Cycle Time')

    fireEvent.click(screen.getByTestId('cycle-time-lens-lead'))
    expect(screen.getByTestId('cycle-time-chart')).toHaveAttribute('aria-label', 'Cycle Time')
  })

  // --- Post-completion edit safety ---

  it('scatter x is driven by CompletedAt and does not move when a record is updated', async () => {
    mockDeliveryResponse(buildData())

    renderChart()

    await waitFor(() => {
      expect(screen.getByTestId('scatter-point-1')).toBeInTheDocument()
    })

    const point1BeforeCx = Number(screen.getByTestId('scatter-point-1').getAttribute('cx'))
    expect(point1BeforeCx).toBeGreaterThan(0)

    expect(screen.getByTestId('scatter-series')).toBeInTheDocument()
    expect(screen.getByTestId('scatter-point-1').getAttribute('cx')).toBe(String(point1BeforeCx))
  })

  // --- Window annotation ---

  it('shows a 30d window badge in the header chrome', async () => {
    mockDeliveryResponse(buildData())

    renderChart()

    await waitFor(() => {
      const badge = screen.getByTestId('cycle-time-chart-window')
      expect(badge).toBeInTheDocument()
      expect(badge.textContent).toBe('30d')
    })
  })

  it('keeps the window badge rendered when no delivered issues are present (empty state)', async () => {
    mockDeliveryResponse(buildEmpty())

    renderChart()

    await waitFor(() => {
      expect(screen.getByTestId('cycle-time-chart-window')).toBeInTheDocument()
      expect(screen.getByTestId('chart-container-empty')).toBeInTheDocument()
    })
  })

  it.each<RangeCode>(['7d', '90d'])(
    'renders the window badge with the %s range code under the lead lens',
    async (range) => {
      mockDeliveryResponse(buildData())

      renderChart(range)

      await waitFor(() => {
        const badge = screen.getByTestId('cycle-time-chart-window')
        expect(badge).toBeInTheDocument()
        expect(badge.textContent).toBe(range)
      })
    },
  )

  it('updates the window badge when the page range changes', async () => {
    mockDeliveryResponse(buildData())

    const { rerender } = render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
          <MemoryRouter initialEntries={['/']}>
            <CycleTimeChart deliveryTimeHook={deliveryTimeHook} range="7d" />
          </MemoryRouter>
        </ProjectProvider>
      </QueryClientProvider>,
    )
    await waitFor(() => {
      expect(screen.getByTestId('cycle-time-chart-window').textContent).toBe('7d')
    })

    rerender(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
          <MemoryRouter initialEntries={['/']}>
            <CycleTimeChart deliveryTimeHook={deliveryTimeHook} range="30d" />
          </MemoryRouter>
        </ProjectProvider>
      </QueryClientProvider>,
    )
    await waitFor(() => {
      expect(screen.getByTestId('cycle-time-chart-window').textContent).toBe('30d')
    })

    rerender(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
          <MemoryRouter initialEntries={['/']}>
            <CycleTimeChart deliveryTimeHook={deliveryTimeHook} range="90d" />
          </MemoryRouter>
        </ProjectProvider>
      </QueryClientProvider>,
    )
    await waitFor(() => {
      expect(screen.getByTestId('cycle-time-chart-window').textContent).toBe('90d')
    })
  })
})
