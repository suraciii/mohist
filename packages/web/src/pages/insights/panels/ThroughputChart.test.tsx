// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { ProjectProvider } from '../../../entities/project'
import { TEST_PROJECT } from '../../../../tests/test-utils'
import { setPrefersReducedMotion } from '../../../../tests/setup'
import type { CompletionTrendResponse } from '../../../entities/issue'

import { ThroughputChart } from './ThroughputChart'

useMswServer()

type RangeCode = '7d' | '30d' | '90d'

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

const THROUGHPUT_PATH = '*/api/projects/:projectId/issues/metrics/completion'

function mockThroughputResponse(data: CompletionTrendResponse) {
  server.use(
    http.get(THROUGHPUT_PATH, () => HttpResponse.json({ success: true, data })),
  )
}

function mockThroughputPending() {
  server.use(
    http.get(THROUGHPUT_PATH, () => new Promise(() => {})),
  )
}

function mockThroughputError() {
  server.use(
    http.get(THROUGHPUT_PATH, () => HttpResponse.json({ success: false, error: { message: 'boom' } }, { status: 500 })),
  )
}

function renderChart(range: RangeCode = '30d') {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
        <MemoryRouter initialEntries={['/']}>
          <ThroughputChart range={range} />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('ThroughputChart', () => {
  afterEach(() => {
    cleanup()
  })

  // --- Three-state routing ---

  it('renders loading state via ChartContainer', () => {
    mockThroughputPending()

    renderChart()

    expect(screen.getByTestId('chart-container-loading')).toBeInTheDocument()
    expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
  })

  it('renders error state via ChartContainer', async () => {
    mockThroughputError()

    renderChart()

    await waitFor(() => {
      expect(screen.getByTestId('chart-container-error')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
  })

  it('renders empty state with next action when all buckets are zero', async () => {
    mockThroughputResponse(buildAllZeroData())

    renderChart()

    await waitFor(() => {
      const empty = screen.getByTestId('chart-container-empty')
      expect(empty).toBeInTheDocument()
      expect(empty.textContent).toContain('Throughput appears once an issue completes on this project')
    })
    expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
  })

  it('renders empty state when buckets are empty', async () => {
    mockThroughputResponse(buildThroughputData({ buckets: [] }))

    renderChart()

    await waitFor(() => {
      const empty = screen.getByTestId('chart-container-empty')
      expect(empty).toBeInTheDocument()
    })
    expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
  })

  it('failure-only throughput is NOT empty (failed bars render)', async () => {
    const data = buildAllZeroData()
    data.buckets[2] = { boundary: '2026-06-03', completed: 0, failed: 5 }

    mockThroughputResponse(data)

    renderChart()

    await waitFor(() => {
      expect(screen.queryByTestId('chart-container-empty')).not.toBeInTheDocument()
      expect(screen.getByTestId('chart-accessibility')).toBeInTheDocument()
    })
  })

  it('renders resolved chart content with accessibility wrapper', async () => {
    mockThroughputResponse(buildThroughputData())

    renderChart()

    await waitFor(() => {
      expect(screen.getByTestId('chart-accessibility')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('chart-container-loading')).not.toBeInTheDocument()
    expect(screen.queryByTestId('chart-container-error')).not.toBeInTheDocument()
    expect(screen.queryByTestId('chart-container-empty')).not.toBeInTheDocument()
  })

  // --- Bar completed/failed values from daily buckets ---

  it('renders one segmented bar per bucket with completed and failed segments', async () => {
    const data = buildThroughputData()
    mockThroughputResponse(data)

    renderChart()

    await waitFor(() => {
      const barSeries = screen.getByTestId('segmented-bar-series')
      expect(barSeries.children).toHaveLength(data.buckets.length)

      for (let i = 0; i < data.buckets.length; i++) {
        expect(screen.getByTestId(`segmented-bar-${i}`)).toBeInTheDocument()
        expect(screen.getByTestId(`segment-${i}-0`)).toBeInTheDocument()
        expect(screen.getByTestId(`segment-${i}-1`)).toBeInTheDocument()
      }
    })
  })

  it('completed segment fill uses fill-chart-2 and failed uses fill-chart-4', async () => {
    mockThroughputResponse(buildThroughputData())

    renderChart()

    await waitFor(() => {
      const completedSeg = screen.getByTestId('segment-0-0')
      expect(completedSeg.getAttribute('class')).toContain('fill-chart-2')
      expect(completedSeg.getAttribute('fill')).toBeNull()

      const failedSeg = screen.getByTestId('segment-0-1')
      expect(failedSeg.getAttribute('class')).toContain('fill-chart-4')
      expect(failedSeg.getAttribute('fill')).toBeNull()
    })
  })

  it('day with completed=0 renders a zero-height bar (not a gap) and encodes failed count', async () => {
    const data = buildThroughputData()
    data.buckets[2] = { boundary: '2026-06-03', completed: 0, failed: 3 }

    mockThroughputResponse(data)

    renderChart()

    await waitFor(() => {
      const completedSeg = screen.getByTestId('segment-2-0')
      expect(completedSeg.style.transform).toContain('scaleY(0)')

      const failedSeg = screen.getByTestId('segment-2-1')
      expect(failedSeg).toBeInTheDocument()
      const failedScale = Number(failedSeg.style.transform.match(/scaleY\(([\d.]+)\)/)?.[1] ?? 0)
      expect(failedScale).toBeCloseTo(3 / 30)
    })
  })

  it('encodes the full failed count when failures exceed completions', async () => {
    const data = buildThroughputData()
    data.buckets[2] = { boundary: '2026-06-03', completed: 2, failed: 5 }

    mockThroughputResponse(data)

    renderChart()

    await waitFor(() => {
      const completedScale = Number(screen.getByTestId('segment-2-0').style.transform.match(/scaleY\(([\d.]+)\)/)?.[1] ?? 0)
      const completedSeg = screen.getByTestId('segment-2-0')
      const failedSeg = screen.getByTestId('segment-2-1')
      const failedScale = Number(failedSeg.style.transform.match(/scaleY\(([\d.]+)\)/)?.[1] ?? 0)
      expect(completedScale).toBeCloseTo(2 / 30)
      expect(failedScale).toBeCloseTo(5 / 30)
      expect(Number(failedSeg.getAttribute('width'))).toBeLessThan(Number(completedSeg.getAttribute('width')))
    })
  })

  // --- MA values ---

  it('renders line series for 7-day moving average computed over completed counts', async () => {
    mockThroughputResponse(buildThroughputData())

    renderChart()

    await waitFor(() => {
      const lineSeries = screen.getByTestId('line-series')
      expect(lineSeries).toBeInTheDocument()

      const markers = lineSeries.querySelectorAll('circle')
      expect(markers.length).toBeGreaterThan(0)
    })
  })

  it('MA uses stroke-chart-5 and fill-chart-5 theme tokens', async () => {
    mockThroughputResponse(buildThroughputData())

    renderChart()

    await waitFor(() => {
      const path = screen.getByTestId('line-series').querySelector('path')
      const pathClasses = path?.getAttribute('class') ?? ''
      expect(pathClasses).toContain('stroke-chart-5')

      const marker = screen.getByTestId('line-marker-0')
      expect(marker.getAttribute('class')).toContain('fill-chart-5')
    })
  })

  // --- Legend entries ---

  it('renders ChartLegend with three entries (Completed, Failed, 7-day average)', async () => {
    mockThroughputResponse(buildThroughputData())

    renderChart()

    await waitFor(() => {
      const legend = screen.getByTestId('chart-legend')
      expect(legend).toBeInTheDocument()
      expect(legend.textContent).toContain('Completed')
      expect(legend.textContent).toContain('Failed')
      expect(legend.textContent).toContain('7-day average')
    })
  })

  it('legend entries are disambiguated by label and shape (non-color channels)', async () => {
    mockThroughputResponse(buildThroughputData())

    renderChart()

    await waitFor(() => {
      const legend = screen.getByTestId('chart-legend')
      const entries = legend.querySelectorAll('span')

      const completedEntry = [...entries].find(e => e.textContent === 'Completed')
      const failedEntry = [...entries].find(e => e.textContent === 'Failed')
      const avgEntry = [...entries].find(e => e.textContent === '7-day average')

      expect(completedEntry).toBeTruthy()
      expect(failedEntry).toBeTruthy()
      expect(avgEntry).toBeTruthy()
    })
  })

  // --- Empty state next action ---

  it('empty state has concrete next action text', async () => {
    mockThroughputResponse(buildThroughputData({ buckets: [] }))

    renderChart()

    await waitFor(() => {
      const empty = screen.getByTestId('chart-container-empty')
      expect(empty.textContent).toContain('Throughput appears once an issue completes on this project')
    })
  })

  // --- Token colors ---

  it('left axis uses stroke-chart-2 and fill-chart-2', async () => {
    mockThroughputResponse(buildThroughputData())

    renderChart()

    await waitFor(() => {
      const axis = screen.getByTestId('chart-axis-left')
      const axisLine = axis.querySelector('line')
      expect(axisLine?.getAttribute('class')).toContain('stroke-chart-2')
      const textEl = axis.querySelector('text')
      expect(textEl?.getAttribute('class')).toContain('fill-chart-2')
    })
  })

  // --- Accessibility ---

  it('accessibility sr-only summary is rendered with window, peak, total, average', async () => {
    mockThroughputResponse(buildThroughputData())

    renderChart()

    await waitFor(() => {
      const summary = screen.getByTestId('chart-sr-summary')
      expect(summary).toBeInTheDocument()
      expect(summary.textContent).toContain('Daily throughput bar chart')
      expect(summary.textContent).toContain('Jun 1')
      expect(summary.textContent).toContain('Total completed')
      expect(summary.textContent).toContain('Average completed')
      expect(summary.textContent).toContain('Peak day')
    })
  })

  it('formats date-only bucket boundaries as local calendar labels without UTC day shift', async () => {
    mockThroughputResponse(buildThroughputData())

    renderChart()

    await waitFor(() => {
      expect(screen.getByText('Jun 1')).toBeInTheDocument()
      expect(screen.getByText('Jun 7')).toBeInTheDocument()
      expect(screen.getByTestId('chart-sr-summary').textContent).toContain('Jun 1 to Jun 7')
    })
  })

  it('chart svg has role=img and aria-label', async () => {
    mockThroughputResponse(buildThroughputData())

    renderChart()

    await waitFor(() => {
      const svg = document.querySelector('svg[role="img"]')
      expect(svg).toBeInTheDocument()
      expect(svg).toHaveAttribute('aria-label')
      expect(svg!.getAttribute('aria-label')).toContain('Throughput trend')
    })
  })

  // --- tabular-nums ---

  it('numeric labels use tabular-nums', async () => {
    mockThroughputResponse(buildThroughputData())

    renderChart()

    await waitFor(() => {
      const axisTexts = document.querySelectorAll('.tabular-nums')
      expect(axisTexts.length).toBeGreaterThan(0)
    })
  })

  // --- Reduced motion ---

  it('segments have transform transition by default', async () => {
    mockThroughputResponse(buildThroughputData())

    renderChart()

    await waitFor(() => {
      const seg = screen.getByTestId('segment-0-0')
      expect(seg.style.transition).toContain('transform')
    })
  })

  it('segments disable animation when prefers-reduced-motion: reduce', async () => {
    setPrefersReducedMotion(true)
    mockThroughputResponse(buildThroughputData())

    renderChart()

    await waitFor(() => {
      const seg = screen.getByTestId('segment-0-0')
      expect(seg.style.transition).toBe('none')
    })

    setPrefersReducedMotion(false)
  })

  // --- Widget section ---

  it('renders within a section with testid and aria-label', async () => {
    mockThroughputResponse(buildThroughputData())

    renderChart()

    await waitFor(() => {
      const section = screen.getByTestId('throughput-chart')
      expect(section).toBeInTheDocument()
      expect(section).toHaveAttribute('aria-label', 'Throughput')
    })
  })

  it('shows section heading', async () => {
    mockThroughputResponse(buildThroughputData())

    renderChart()

    await waitFor(() => {
      expect(screen.getByText('Throughput')).toBeInTheDocument()
    })
  })

  // --- Day labels ---

  it('renders first and last day label', async () => {
    mockThroughputResponse(buildThroughputData())

    renderChart()

    await waitFor(() => {
      expect(screen.getByText('Jun 1')).toBeInTheDocument()
      expect(screen.getByText('Jun 7')).toBeInTheDocument()
    })
  })

  it('renders fewer than 30 day labels for 30-day window', async () => {
    mockThroughputResponse(build30dayData())

    renderChart()

    await waitFor(() => {
      const labelTexts = document.querySelectorAll('svg text')
      const dayLabels = [...labelTexts].filter(t =>
        /^[A-Z][a-z]{2} \d{1,2}$/.test(t.textContent ?? ''),
      )
      expect(dayLabels.length).toBeGreaterThan(0)
      expect(dayLabels.length).toBeLessThan(30)
    })
  })

  // --- Window annotation ---

  it('shows a 30d window badge in the header chrome', async () => {
    mockThroughputResponse(buildThroughputData())

    renderChart()

    await waitFor(() => {
      const badge = screen.getByTestId('throughput-chart-window')
      expect(badge).toBeInTheDocument()
      expect(badge.textContent).toBe('30d')
    })
  })

  it.each<RangeCode>(['7d', '90d'])(
    'renders the throughput window badge with the %s range code',
    async (range) => {
      mockThroughputResponse(buildThroughputData())

      renderChart(range)

      await waitFor(() => {
        const badge = screen.getByTestId('throughput-chart-window')
        expect(badge).toBeInTheDocument()
        expect(badge.textContent).toBe(range)
      })
    },
  )

  it('updates the throughput window badge when the page range changes', async () => {
    mockThroughputResponse(buildThroughputData())

    const { rerender } = render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
          <MemoryRouter initialEntries={['/']}>
            <ThroughputChart range="7d" />
          </MemoryRouter>
        </ProjectProvider>
      </QueryClientProvider>,
    )
    await waitFor(() => {
      expect(screen.getByTestId('throughput-chart-window').textContent).toBe('7d')
    })

    rerender(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
          <MemoryRouter initialEntries={['/']}>
            <ThroughputChart range="30d" />
          </MemoryRouter>
        </ProjectProvider>
      </QueryClientProvider>,
    )
    await waitFor(() => {
      expect(screen.getByTestId('throughput-chart-window').textContent).toBe('30d')
    })

    rerender(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
          <MemoryRouter initialEntries={['/']}>
            <ThroughputChart range="90d" />
          </MemoryRouter>
        </ProjectProvider>
      </QueryClientProvider>,
    )
    await waitFor(() => {
      expect(screen.getByTestId('throughput-chart-window').textContent).toBe('90d')
    })
  })
})
