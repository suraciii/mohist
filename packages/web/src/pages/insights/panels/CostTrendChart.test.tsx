// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { TEST_PROJECT } from '../../../../tests/test-utils'
import { setPrefersReducedMotion } from '../../../../tests/setup'
import type { AgentUsageTimeseriesDto } from '../../../entities/agent'

import { CostTrendChart, type AgentUsageHook } from './CostTrendChart'

function buildUsageData(overrides?: Partial<AgentUsageTimeseriesDto>): AgentUsageTimeseriesDto {
  return {
    rangeFrom: '2026-06-22T00:00:00',
    rangeTo: '2026-06-28T23:59:59',
    bucketGranularity: 'day',
    buckets: [
      { bucketStart: '2026-06-22T00:00:00', bucketEnd: '2026-06-22T23:59:59', inputTokens: 0, outputTokens: 0, totalTokens: 0, costAmount: 10, costCurrency: 'USD' },
      { bucketStart: '2026-06-23T00:00:00', bucketEnd: '2026-06-23T23:59:59', inputTokens: 0, outputTokens: 0, totalTokens: 0, costAmount: 25, costCurrency: 'USD' },
      { bucketStart: '2026-06-24T00:00:00', bucketEnd: '2026-06-24T23:59:59', inputTokens: 0, outputTokens: 0, totalTokens: 0, costAmount: 5, costCurrency: 'USD' },
      { bucketStart: '2026-06-25T00:00:00', bucketEnd: '2026-06-25T23:59:59', inputTokens: 0, outputTokens: 0, totalTokens: 0, costAmount: 30, costCurrency: 'USD' },
      { bucketStart: '2026-06-26T00:00:00', bucketEnd: '2026-06-26T23:59:59', inputTokens: 0, outputTokens: 0, totalTokens: 0, costAmount: 15, costCurrency: 'USD' },
      { bucketStart: '2026-06-27T00:00:00', bucketEnd: '2026-06-27T23:59:59', inputTokens: 0, outputTokens: 0, totalTokens: 0, costAmount: 20, costCurrency: 'USD' },
      { bucketStart: '2026-06-28T00:00:00', bucketEnd: '2026-06-28T23:59:59', inputTokens: 0, outputTokens: 0, totalTokens: 0, costAmount: 8, costCurrency: 'USD' },
    ],
    cumulativeCostPerShip: [
      { dayEnd: '2026-06-22T23:59:59', cumulativeCost: 10, currency: 'USD', cumulativeShippedCount: 1, costPerShip: 10 },
      { dayEnd: '2026-06-23T23:59:59', cumulativeCost: 35, currency: 'USD', cumulativeShippedCount: 2, costPerShip: 17.5 },
      { dayEnd: '2026-06-24T23:59:59', cumulativeCost: 40, currency: 'USD', cumulativeShippedCount: 2, costPerShip: 20 },
      { dayEnd: '2026-06-25T23:59:59', cumulativeCost: 70, currency: 'USD', cumulativeShippedCount: 3, costPerShip: 23.33 },
      { dayEnd: '2026-06-26T23:59:59', cumulativeCost: 85, currency: 'USD', cumulativeShippedCount: 4, costPerShip: 21.25 },
      { dayEnd: '2026-06-27T23:59:59', cumulativeCost: 105, currency: 'USD', cumulativeShippedCount: 5, costPerShip: 21 },
      { dayEnd: '2026-06-28T23:59:59', cumulativeCost: 113, currency: 'USD', cumulativeShippedCount: 6, costPerShip: 18.83 },
    ],
    ...overrides,
  }
}

function buildZeroSampleUsageData(): AgentUsageTimeseriesDto {
  const buckets = Array.from({ length: 7 }, (_, i) => {
    const day = 22 + i
    return {
      bucketStart: `2026-06-${day}T00:00:00`,
      bucketEnd: `2026-06-${day + 1}T00:00:00`,
      inputTokens: 0,
      outputTokens: 0,
      totalTokens: 0,
      costAmount: 0,
      costCurrency: null,
    }
  })

  return {
    rangeFrom: '2026-06-22T00:00:00',
    rangeTo: '2026-06-29T00:00:00',
    bucketGranularity: 'day',
    buckets,
    cumulativeCostPerShip: buckets.map((bucket) => ({
      dayEnd: bucket.bucketEnd,
      cumulativeCost: null,
      currency: null,
      cumulativeShippedCount: 0,
      costPerShip: null,
    })),
  }
}

let agentUsageResult: ReturnType<AgentUsageHook>

const agentUsageHook: AgentUsageHook = () => agentUsageResult

function mockUsageResponse(data: AgentUsageTimeseriesDto) {
  agentUsageResult = { data, isLoading: false, isError: false }
}

function mockUsagePending() {
  agentUsageResult = { data: undefined, isLoading: true, isError: false }
}

function mockUsageError() {
  agentUsageResult = { data: undefined, isLoading: false, isError: true }
}

function renderChart() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
        <MemoryRouter initialEntries={['/']}>
          <CostTrendChart range="30d" agentUsageHook={agentUsageHook} />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('CostTrendChart', () => {
  afterEach(() => {
    cleanup()
  })

  // --- Three-state routing ---

  it('renders loading state via ChartContainer', () => {
    mockUsagePending()

    renderChart()

    expect(screen.getByTestId('chart-container-loading')).toBeInTheDocument()
    expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
  })

  it('renders error state via ChartContainer', async () => {
    mockUsageError()

    renderChart()

    await waitFor(() => {
      expect(screen.getByTestId('chart-container-error')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
  })

  it('renders empty state with next action when buckets are empty', async () => {
    mockUsageResponse(buildUsageData({ buckets: [] }))

    renderChart()

    await waitFor(() => {
      const empty = screen.getByTestId('chart-container-empty')
      expect(empty).toBeInTheDocument()
      expect(empty.textContent).toContain('once an agent session reports usage')
    })
    expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
  })

  it('renders empty state for the server zero-sample seven-bucket shape', async () => {
    mockUsageResponse(buildZeroSampleUsageData())

    renderChart()

    await waitFor(() => {
      const empty = screen.getByTestId('chart-container-empty')
      expect(empty).toBeInTheDocument()
      expect(empty.textContent).toContain('once an agent session reports usage')
    })
    expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
  })

  it('renders resolved chart content with accessibility wrapper', async () => {
    mockUsageResponse(buildUsageData())

    renderChart()

    await waitFor(() => {
      expect(screen.getByTestId('chart-accessibility')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('chart-container-loading')).not.toBeInTheDocument()
    expect(screen.queryByTestId('chart-container-error')).not.toBeInTheDocument()
    expect(screen.queryByTestId('chart-container-empty')).not.toBeInTheDocument()
  })

  // --- Bar values ---

  it('renders one bar per bucket with height encoding cost', async () => {
    mockUsageResponse(buildUsageData())

    renderChart()

    await waitFor(() => {
      const barSeries = screen.getByTestId('bar-series')
      expect(barSeries.children).toHaveLength(7)
      for (let i = 0; i < 7; i++) {
        expect(screen.getByTestId(`bar-${i}`)).toBeInTheDocument()
      }
    })
  })

  it('zero-cost day renders a zero-height bar (not omitted)', async () => {
    const data = buildUsageData()
    data.buckets[2] = { ...data.buckets[2], costAmount: 0 }

    mockUsageResponse(data)

    renderChart()

    await waitFor(() => {
      const barSeries = screen.getByTestId('bar-series')
      expect(barSeries.children).toHaveLength(7)

      const bar = screen.getByTestId('bar-2')
      expect(bar.style.transform).toContain('scaleY(0)')
    })
  })

  // --- Trend values ---

  it('trend line renders with markers for valid costPerShip points', async () => {
    mockUsageResponse(buildUsageData())

    renderChart()

    await waitFor(() => {
      const lineSeries = screen.getByTestId('line-series')
      expect(lineSeries).toBeInTheDocument()

      const markers = lineSeries.querySelectorAll('circle')
      expect(markers.length).toBeGreaterThan(0)
    })
  })

  it('skips null costPerShip points (undefined)', async () => {
    const data = buildUsageData()
    if (data.cumulativeCostPerShip) {
      data.cumulativeCostPerShip[1] = { dayEnd: '2026-06-23T23:59:59', cumulativeCost: null, currency: null, cumulativeShippedCount: 0, costPerShip: null }
      data.cumulativeCostPerShip[3] = { dayEnd: '2026-06-25T23:59:59', cumulativeCost: null, currency: null, cumulativeShippedCount: 0, costPerShip: null }
    }

    mockUsageResponse(data)

    renderChart()

    await waitFor(() => {
      const lineSeries = screen.getByTestId('line-series')
      const markers = lineSeries.querySelectorAll('circle')
      expect(markers).toHaveLength(5)
    })
  })

  it('does not bridge null costPerShip gaps in the trend path', async () => {
    const data = buildUsageData()
    if (data.cumulativeCostPerShip) {
      data.cumulativeCostPerShip[1] = { dayEnd: '2026-06-23T23:59:59', cumulativeCost: null, currency: null, cumulativeShippedCount: 0, costPerShip: null }
    }

    mockUsageResponse(data)

    renderChart()

    await waitFor(() => {
      const paths = screen.getByTestId('line-series').querySelectorAll('path')
      expect(paths.length).toBeGreaterThan(1)
    })
  })

  it('genuine zero costPerShip plots at value 0 (not skipped)', async () => {
    const data = buildUsageData()
    if (data.cumulativeCostPerShip) {
      data.cumulativeCostPerShip[0] = { dayEnd: '2026-06-22T23:59:59', cumulativeCost: 0, currency: 'USD', cumulativeShippedCount: 2, costPerShip: 0 }
    }

    mockUsageResponse(data)

    renderChart()

    await waitFor(() => {
      const markers = screen.getByTestId('line-series').querySelectorAll('circle')
      expect(markers).toHaveLength(7)
    })
  })

  it('renders without crashing when costPerShip is omitted (server WhenWritingNull wire format)', async () => {
    // The server serializes null costPerShip with JsonIgnoreCondition.WhenWritingNull,
    // so the key is absent on the wire and parses as `undefined` (not `null`).
    const omittedPoints = buildUsageData().cumulativeCostPerShip!.map((p) => {
      const { costPerShip: _omit, ...rest } = p
      return rest
    }) as AgentUsageTimeseriesDto['cumulativeCostPerShip']

    mockUsageResponse(buildUsageData({ cumulativeCostPerShip: omittedPoints }))

    renderChart()

    await waitFor(() => {
      expect(screen.getByTestId('bar-series')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('line-series')).not.toBeInTheDocument()
  })

  it('bars-only when cumulativeCostPerShip is absent', async () => {
    const data = buildUsageData({ cumulativeCostPerShip: null })

    mockUsageResponse(data)

    renderChart()

    await waitFor(() => {
      expect(screen.getByTestId('bar-series')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('line-series')).not.toBeInTheDocument()
  })

  it('no legend when only one series (bars-only)', async () => {
    const data = buildUsageData({ cumulativeCostPerShip: null })

    mockUsageResponse(data)

    renderChart()

    await waitFor(() => {
      expect(screen.queryByTestId('chart-legend')).not.toBeInTheDocument()
    })
  })

  it('legend rendered when both series present, ordered bar then line', async () => {
    mockUsageResponse(buildUsageData())

    renderChart()

    await waitFor(() => {
      const legend = screen.getByTestId('chart-legend')
      expect(legend).toBeInTheDocument()
      expect(legend.textContent).toContain('Daily cost')
      expect(legend.textContent).toContain('Cost per ship')

      const entries = legend.querySelectorAll('span')
      const dailyCostEntry = [...entries].find((e) => e.textContent === 'Daily cost')
      const cpsEntry = [...entries].find((e) => e.textContent === 'Cost per ship')
      expect(dailyCostEntry).toBeTruthy()
      expect(cpsEntry).toBeTruthy()
    })
  })

  // --- Dual axis ---

  it('renders left and right axes when trend is present', async () => {
    mockUsageResponse(buildUsageData())

    renderChart()

    await waitFor(() => {
      expect(screen.getByTestId('chart-axis-left')).toBeInTheDocument()
      expect(screen.getByTestId('chart-axis-right')).toBeInTheDocument()
    })
  })

  it('only left axis when trend is absent', async () => {
    const data = buildUsageData({ cumulativeCostPerShip: null })

    mockUsageResponse(data)

    renderChart()

    await waitFor(() => {
      expect(screen.getByTestId('chart-axis-left')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('chart-axis-right')).not.toBeInTheDocument()
  })

  // --- Token colors ---

  it('bars use fill-chart-2 theme token', async () => {
    mockUsageResponse(buildUsageData())

    renderChart()

    await waitFor(() => {
      const bar = screen.getByTestId('bar-0')
      const classes = bar.getAttribute('class') ?? ''
      expect(classes).toContain('fill-chart-2')
      expect(bar.getAttribute('fill')).toBeNull()
    })
  })

  it('trend line uses stroke-chart-5 theme token', async () => {
    mockUsageResponse(buildUsageData())

    renderChart()

    await waitFor(() => {
      const path = screen.getByTestId('line-series').querySelector('path')
      const classes = path?.getAttribute('class') ?? ''
      expect(classes).toContain('stroke-chart-5')
    })
  })

  it('left axis uses stroke-chart-2 and fill-chart-2', async () => {
    mockUsageResponse(buildUsageData())

    renderChart()

    await waitFor(() => {
      const axis = screen.getByTestId('chart-axis-left')
      const axisLine = axis.querySelector('line')
      expect(axisLine?.getAttribute('class')).toContain('stroke-chart-2')
      const textEl = axis.querySelector('text')
      expect(textEl?.getAttribute('class')).toContain('fill-chart-2')
    })
  })

  it('right axis uses stroke-chart-5 and fill-chart-5', async () => {
    mockUsageResponse(buildUsageData())

    renderChart()

    await waitFor(() => {
      const axis = screen.getByTestId('chart-axis-right')
      const axisLine = axis.querySelector('line')
      expect(axisLine?.getAttribute('class')).toContain('stroke-chart-5')
      const textEl = axis.querySelector('text')
      expect(textEl?.getAttribute('class')).toContain('fill-chart-5')
    })
  })

  // --- Accessibility ---

  it('accessibility sr-only summary is rendered', async () => {
    mockUsageResponse(buildUsageData())

    renderChart()

    await waitFor(() => {
      const summary = screen.getByTestId('chart-sr-summary')
      expect(summary).toBeInTheDocument()
      expect(summary.textContent).toContain('Daily cost bar chart')
      expect(summary.textContent).toContain('Jun 22')
      expect(summary.textContent).toContain('Total window cost')
    })
  })

  it('sr-only summary uses the displayed last bucket day instead of exclusive rangeTo', async () => {
    mockUsageResponse(buildUsageData({ rangeTo: '2026-06-29T00:00:00' }))

    renderChart()

    await waitFor(() => {
      const summary = screen.getByTestId('chart-sr-summary')
      expect(summary.textContent).toContain('Jun 22 to Jun 28')
      expect(summary.textContent).not.toContain('Jun 29')
    })
  })

  it('chart svg has role=img and aria-label', async () => {
    mockUsageResponse(buildUsageData())

    renderChart()

    await waitFor(() => {
      const svg = document.querySelector('svg[role="img"]')
      expect(svg).toBeInTheDocument()
      expect(svg).toHaveAttribute('aria-label')
      expect(svg!.getAttribute('aria-label')).toContain('Cost trend')
    })
  })

  // --- tabular-nums ---

  it('numeric labels use tabular-nums', async () => {
    mockUsageResponse(buildUsageData())

    renderChart()

    await waitFor(() => {
      const axisTexts = document.querySelectorAll('.tabular-nums')
      expect(axisTexts.length).toBeGreaterThan(0)
    })
  })

  // --- Reduced motion ---

  it('bars have transform transition by default', async () => {
    mockUsageResponse(buildUsageData())

    renderChart()

    await waitFor(() => {
      const bar = screen.getByTestId('bar-0')
      expect(bar.style.transition).toContain('transform')
    })
  })

  it('bars disable animation when prefers-reduced-motion: reduce', async () => {
    setPrefersReducedMotion(true)
    mockUsageResponse(buildUsageData())

    renderChart()

    await waitFor(() => {
      const bar = screen.getByTestId('bar-0')
      expect(bar.style.transition).toBe('none')
    })

    setPrefersReducedMotion(false)
  })

  // --- Empty state next action ---

  it('empty state has concrete next action text', async () => {
    mockUsageResponse(buildUsageData({ buckets: [] }))

    renderChart()

    await waitFor(() => {
      const empty = screen.getByTestId('chart-container-empty')
      expect(empty.textContent).toContain('once an agent session reports usage on this project')
    })
  })

  // --- Widget section ---

  it('renders within a section with testid', async () => {
    mockUsageResponse(buildUsageData())

    renderChart()

    await waitFor(() => {
      const section = screen.getByTestId('cost-trend-chart')
      expect(section).toBeInTheDocument()
      expect(section).toHaveAttribute('aria-label', 'Cost Trend')
    })
  })

  it('shows section heading', async () => {
    mockUsageResponse(buildUsageData())

    renderChart()

    await waitFor(() => {
      expect(screen.getByText('Cost Trend')).toBeInTheDocument()
    })
  })

  // --- Day labels ---

  it('renders day label for each bucket', async () => {
    mockUsageResponse(buildUsageData())

    renderChart()

    await waitFor(() => {
      expect(screen.getByText('Jun 22')).toBeInTheDocument()
      expect(screen.getByText('Jun 28')).toBeInTheDocument()
    })
  })

  // --- Window annotation ---

  it('renders a window badge derived from rangeFrom/rangeTo when usage data is present', async () => {
    mockUsageResponse(buildUsageData({
      rangeFrom: '2026-06-22T00:00:00',
      rangeTo: '2026-06-28T23:59:59',
    }))

    renderChart()

    await waitFor(() => {
      const badge = screen.getByTestId('cost-trend-chart-window')
      expect(badge).toBeInTheDocument()
      expect(badge.textContent).toContain('Jun 22')
      expect(badge.textContent).toContain('Jun 28')
    })
  })

  it('hides the window badge in the empty state', async () => {
    mockUsageResponse(buildUsageData({ buckets: [] }))

    renderChart()

    await waitFor(() => {
      expect(screen.getByTestId('chart-container-empty')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('cost-trend-chart-window')).not.toBeInTheDocument()
  })
})
