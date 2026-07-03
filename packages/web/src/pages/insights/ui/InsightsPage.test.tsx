// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { CompletionTrendResponse } from '../../../entities/issue'
import type { DeliveryTimeMetricsResponse } from '../../../entities/issue'
import type { QualityMetricsResponse } from '../../../entities/issue'
import type { StageDurationMetricsResponse } from '../../../entities/issue'
import type { AgentCostRollupDto } from '../../../entities/agent'
import { InsightsPage } from './InsightsPage'

const mocks = vi.hoisted(() => ({
  useCompletionThroughput: vi.fn(),
  useDeliveryTime: vi.fn(),
  useQualityMetrics: vi.fn(),
  useCostRollup: vi.fn(),
  useStageDuration: vi.fn(),
}))

vi.mock('../../../entities/issue', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/issue')>()
  return {
    ...actual,
    useCompletionThroughput: mocks.useCompletionThroughput,
    useDeliveryTime: mocks.useDeliveryTime,
    useQualityMetrics: mocks.useQualityMetrics,
    useStageDuration: mocks.useStageDuration,
  }
})

vi.mock('../../../entities/agent', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/agent')>()
  return {
    ...actual,
    useCostRollup: mocks.useCostRollup,
  }
})

const TEST_PROJECT = {
  id: 'proj-1',
  name: 'demo',
  createdAt: '2024-01-01T00:00:00Z',
  updatedAt: '2024-01-01T00:00:00Z',
  repositories: [],
}

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj-1" initialProjects={[TEST_PROJECT]}>
        <MemoryRouter initialEntries={['/demo/insights']}>
          <InsightsPage />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

function populatedCompletion(): CompletionTrendResponse {
  return {
    bucket: 'day',
    window: { from: '2026-06-01T00:00:00+00:00', to: '2026-07-01T00:00:00+00:00' },
    buckets: [],
    currentTotal: { completed: 5, failed: 0, sampleCount: 5 },
    previousTotal: { completed: 3, failed: 0, sampleCount: 3 },
  }
}

function populatedDelivery(): DeliveryTimeMetricsResponse {
  return {
    points: [
      { issueNumber: 1, completedAt: '2026-06-10T00:00:00Z', leadDays: 1, cycleDays: 5.2 / 24 },
      { issueNumber: 2, completedAt: '2026-06-12T00:00:00Z', leadDays: 1, cycleDays: 5.2 / 24 },
    ],
    previousCycleDays: 6.3 / 24,
  }
}

function populatedQuality(): QualityMetricsResponse {
  return {
    window7d: { from: '2026-06-23T00:00:00Z', to: '2026-06-30T00:00:00Z', sampleCount: 0, firstTimeRightRate: null, stages: [] },
    window30d: {
      from: '2026-06-01T00:00:00Z',
      to: '2026-07-01T00:00:00Z',
      sampleCount: 10,
      firstTimeRightRate: 0.73,
      stages: [],
    },
    previousFirstTimeRightRate: 0.81,
    previousSampleCount: 8,
  }
}

function populatedCost(): AgentCostRollupDto {
  return {
    totalCost: { amount: 500, currency: 'USD', sampleCount: 50 },
    todayCost: { amount: 5, currency: 'USD', sampleCount: 1 },
    doneIssuesCount: 12,
    costPerShip: { amount: 42, currency: 'USD', sampleCount: 12 },
    currentWindow: {
      spend: { amount: 182, currency: 'USD', sampleCount: 5 },
      perIssueCost: { amount: 36, currency: 'USD', sampleCount: 5 },
    },
    previousWindow: {
      spend: { amount: 150, currency: 'USD', sampleCount: 3 },
      perIssueCost: { amount: 50, currency: 'USD', sampleCount: 3 },
    },
  }
}

function populatedStageDuration(): StageDurationMetricsResponse {
  return {
    window: { from: '2026-06-01T00:00:00+00:00', to: '2026-07-01T00:00:00+00:00' },
    stages: [
      { stage: 'plan', sampleCount: 5, averageSeconds: 60, medianSeconds: 60 },
      { stage: 'build', sampleCount: 5, averageSeconds: 600, medianSeconds: 580 },
      { stage: 'review', sampleCount: 5, averageSeconds: 120, medianSeconds: 110 },
    ],
    flowEfficiencyRatio: null,
    waitBreakout: null,
  }
}

beforeEach(() => {
  vi.clearAllMocks()
  mocks.useCompletionThroughput.mockReturnValue({ data: undefined, isLoading: false })
  mocks.useDeliveryTime.mockReturnValue({ data: undefined, isLoading: false })
  mocks.useQualityMetrics.mockReturnValue({ data: undefined, isLoading: false })
  mocks.useCostRollup.mockReturnValue({ data: undefined, isLoading: false })
  mocks.useStageDuration.mockReturnValue({ data: undefined, isLoading: false })
})

afterEach(() => {
  cleanup()
  window.localStorage.clear()
})

describe('InsightsPage structure', () => {
  it('renders the page with title and four verdicts', () => {
    renderPage()

    expect(screen.getByTestId('insights-page')).toBeInTheDocument()
    expect(screen.getByTestId('insights-title').textContent).toBe('Insights')
    expect(screen.getByTestId('insights-signal-section')).toBeInTheDocument()
    expect(screen.getByTestId('signal-summary')).toBeInTheDocument()
    expect(screen.getByTestId('insights-throughput')).toBeInTheDocument()
    expect(screen.getByTestId('insights-delivery')).toBeInTheDocument()
    expect(screen.getByTestId('insights-quality')).toBeInTheDocument()
    expect(screen.getByTestId('insights-investment')).toBeInTheDocument()
  })

  it('renders exactly four verdict cards and no fifth', () => {
    renderPage()

    const summary = screen.getByTestId('signal-summary')
    const verdictTestIds = ['insights-throughput', 'insights-delivery', 'insights-quality', 'insights-investment']
    for (const id of verdictTestIds) {
      expect(summary.querySelector(`[data-testid="${id}"]`)).not.toBeNull()
    }
    // No extra verdict-like child nodes: count children and ensure only 4 verdict cards.
    const verdictCards = summary.querySelectorAll('[data-state]')
    expect(verdictCards.length).toBe(4)
  })

  it('renders the four chart groups in the fixed dimension order', () => {
    renderPage()

    const groups = screen.getAllByTestId('insights-chart-group')
    expect(groups).toHaveLength(4)
    expect(groups.map((g) => g.getAttribute('data-dimension'))).toEqual([
      'output',
      'delivery',
      'quality',
      'investment',
    ])
    expect(screen.queryByTestId('insights-chart-placeholder')).not.toBeInTheDocument()
  })
})

describe('InsightsPage empty state', () => {
  it('renders all four verdicts as insufficient when no data is loaded', () => {
    renderPage()

    for (const id of ['insights-throughput', 'insights-delivery', 'insights-quality', 'insights-investment']) {
      const card = screen.getByTestId(id)
      expect(card.getAttribute('data-state')).toBe('insufficient')
    }
    expect(screen.getAllByTestId('insights-insufficient').length).toBeGreaterThanOrEqual(4)
  })
})

describe('InsightsPage populated project', () => {
  beforeEach(() => {
    mocks.useCompletionThroughput.mockReturnValue({ data: populatedCompletion(), isLoading: false })
    mocks.useDeliveryTime.mockReturnValue({ data: populatedDelivery(), isLoading: false })
    mocks.useQualityMetrics.mockReturnValue({ data: populatedQuality(), isLoading: false })
    mocks.useCostRollup.mockReturnValue({ data: populatedCost(), isLoading: false })
    mocks.useStageDuration.mockReturnValue({ data: populatedStageDuration(), isLoading: false })
  })

  it('renders throughput verdict with up arrow when current=5, previous=3', () => {
    renderPage()
    const card = screen.getByTestId('insights-throughput')
    expect(card.getAttribute('data-state')).toBe('full')
    const upArrow = card.querySelector('[data-testid="insights-trend-up"]')
    expect(upArrow).not.toBeNull()
  })

  it('renders delivery verdict with down arrow (faster) and slowest stage', () => {
    renderPage()
    const card = screen.getByTestId('insights-delivery')
    expect(card.getAttribute('data-state')).toBe('full')
    expect(card.querySelector('[data-testid="insights-trend-down"]')).not.toBeNull()
    expect(card.textContent).toContain('build')
  })

  it('renders quality verdict with down arrow (unfavorable) when FTR drops 81% -> 73%', () => {
    renderPage()
    const card = screen.getByTestId('insights-quality')
    expect(card.getAttribute('data-state')).toBe('full')
    expect(card.querySelector('[data-testid="insights-trend-down"]')).not.toBeNull()
    const magnitude = card.querySelector('[data-testid="insights-magnitude"]')
    expect(magnitude).not.toBeNull()
    expect(magnitude!.textContent).toContain('8')
  })

  it('renders investment verdict with full data and down arrow (cheaper)', () => {
    renderPage()
    const card = screen.getByTestId('insights-investment')
    expect(card.getAttribute('data-state')).toBe('full')
    expect(card.querySelector('[data-testid="insights-trend-down"]')).not.toBeNull()
  })
})

describe('InsightsPage graceful degradation', () => {
  it('hides trend/magnitude when throughput has no previous window', () => {
    mocks.useCompletionThroughput.mockReturnValue({
      data: {
        bucket: 'day',
        window: { from: '2026-06-01T00:00:00+00:00', to: '2026-07-01T00:00:00+00:00' },
        buckets: [],
        currentTotal: { completed: 5, failed: 0, sampleCount: 5 },
        // no previousTotal
      },
      isLoading: false,
    })
    renderPage()

    const card = screen.getByTestId('insights-throughput')
    expect(card.getAttribute('data-state')).toBe('current-only')
    expect(card.querySelector('[data-testid="insights-trend-up"]')).toBeNull()
    expect(card.querySelector('[data-testid="insights-trend-down"]')).toBeNull()
    expect(card.querySelector('[data-testid="insights-magnitude"]')).toBeNull()
    expect(card.textContent).toContain('本周完成 5 个')
  })

  it('marks the verdict as insufficient when no current-window samples', () => {
    mocks.useQualityMetrics.mockReturnValue({
      data: {
        window7d: { from: '2026-06-23T00:00:00Z', to: '2026-06-30T00:00:00Z', sampleCount: 0, firstTimeRightRate: null, stages: [] },
        window30d: { from: '2026-06-01T00:00:00Z', to: '2026-07-01T00:00:00Z', sampleCount: 0, firstTimeRightRate: null, stages: [] },
      },
      isLoading: false,
    })
    renderPage()

    const card = screen.getByTestId('insights-quality')
    expect(card.getAttribute('data-state')).toBe('insufficient')
    expect(card.textContent).toContain('数据不足')
  })

  it('evaluates insufficiency independently per verdict', () => {
    // throughput has full data, quality has no previous baseline
    mocks.useCompletionThroughput.mockReturnValue({
      data: populatedCompletion(),
      isLoading: false,
    })
    mocks.useQualityMetrics.mockReturnValue({
      data: {
        window7d: { from: '2026-06-23T00:00:00Z', to: '2026-06-30T00:00:00Z', sampleCount: 0, firstTimeRightRate: null, stages: [] },
        window30d: {
          from: '2026-06-01T00:00:00Z',
          to: '2026-07-01T00:00:00Z',
          sampleCount: 5,
          firstTimeRightRate: 0.5,
          stages: [],
        },
        // no previous fields
      },
      isLoading: false,
    })
    renderPage()

    const throughput = screen.getByTestId('insights-throughput')
    expect(throughput.getAttribute('data-state')).toBe('full')
    const quality = screen.getByTestId('insights-quality')
    expect(quality.getAttribute('data-state')).toBe('current-only')
  })

  it('omits the slowest stage when no stage-duration samples exist', () => {
    mocks.useDeliveryTime.mockReturnValue({
      data: populatedDelivery(),
      isLoading: false,
    })
    mocks.useStageDuration.mockReturnValue({
      data: {
        window: { from: '2026-06-01T00:00:00+00:00', to: '2026-07-01T00:00:00+00:00' },
        stages: [
          { stage: 'plan', sampleCount: 0, averageSeconds: null, medianSeconds: null },
          { stage: 'build', sampleCount: 0, averageSeconds: null, medianSeconds: null },
        ],
        flowEfficiencyRatio: null,
        waitBreakout: null,
      },
      isLoading: false,
    })
    renderPage()

    const card = screen.getByTestId('insights-delivery')
    expect(card.getAttribute('data-state')).toBe('full')
    // The slowest stage clause should not be present
    expect(card.textContent).not.toContain('最慢是')
  })
})

describe('InsightsPage investment sub-trend breakdown', () => {
  it('renders the per-metric sub-trend rows when currentWindow is populated', () => {
    mocks.useCostRollup.mockReturnValue({ data: populatedCost(), isLoading: false })
    renderPage()

    const subRows = screen.getAllByTestId('insights-investment-sub-trend')
    expect(subRows.length).toBe(2)
    expect(subRows[0].getAttribute('data-metric')).toBe('spend')
    expect(subRows[1].getAttribute('data-metric')).toBe('perIssue')
  })

  it('marks a sub-trend as currentOnly when its previous window has no samples', () => {
    mocks.useCostRollup.mockReturnValue({
      data: {
        totalCost: { amount: 0, currency: 'USD', sampleCount: 0 },
        todayCost: { amount: 0, currency: 'USD', sampleCount: 0 },
        doneIssuesCount: 5,
        costPerShip: { amount: 0, currency: 'USD', sampleCount: 0 },
        currentWindow: {
          spend: { amount: 182, currency: 'USD', sampleCount: 5 },
          perIssueCost: { amount: 36, currency: 'USD', sampleCount: 5 },
        },
        previousWindow: {
          spend: { amount: 150, currency: 'USD', sampleCount: 3 },
          perIssueCost: { amount: null, currency: 'USD', sampleCount: 0 },
        },
      },
      isLoading: false,
    })
    renderPage()

    const rows = screen.getAllByTestId('insights-investment-sub-trend')
    const spendRow = rows.find((row) => row.getAttribute('data-metric') === 'spend')!
    const perIssueRow = rows.find((row) => row.getAttribute('data-metric') === 'perIssue')!
    expect(spendRow.getAttribute('data-state')).toBe('full')
    expect(perIssueRow.getAttribute('data-state')).toBe('currentOnly')
  })
})

describe('InsightsPage global time-range selector', () => {
  it('renders exactly three presets (7d / 30d / 90d) with no custom from/to picker', () => {
    renderPage()

    expect(screen.getByTestId('insights-range-selector')).toBeInTheDocument()
    expect(screen.getByTestId('insights-range-option-7d')).toBeInTheDocument()
    expect(screen.getByTestId('insights-range-option-30d')).toBeInTheDocument()
    expect(screen.getByTestId('insights-range-option-90d')).toBeInTheDocument()

    const selector = screen.getByTestId('insights-range-selector')
    expect(selector.querySelector('input[type="date"]')).toBeNull()
    expect(selector.querySelector('input[type="range"]')).toBeNull()
  })

  it('defaults to 30d on first load (no preset pre-selected other than 30d)', () => {
    renderPage()

    const page = screen.getByTestId('insights-page')
    expect(page.getAttribute('data-range')).toBe('30d')

    const option7d = screen.getByTestId('insights-range-option-7d')
    const option30d = screen.getByTestId('insights-range-option-30d')
    const option90d = screen.getByTestId('insights-range-option-90d')
    expect(option7d.getAttribute('data-active')).toBe('false')
    expect(option30d.getAttribute('data-active')).toBe('true')
    expect(option90d.getAttribute('data-active')).toBe('false')
  })

  it('invokes the five page-level hooks with the default 30d range on first render', () => {
    renderPage()

    expect(mocks.useCompletionThroughput).toHaveBeenCalledWith('30d')
    expect(mocks.useDeliveryTime).toHaveBeenCalledWith('30d')
    expect(mocks.useQualityMetrics).toHaveBeenCalledWith('30d')
    expect(mocks.useCostRollup).toHaveBeenCalledWith('30d')
    expect(mocks.useStageDuration).toHaveBeenCalledWith('30d')
  })

  it('re-applies the new range to the five page-level hooks when the operator switches the selector', () => {
    mocks.useCompletionThroughput.mockReturnValue({ data: populatedCompletion(), isLoading: false })
    mocks.useDeliveryTime.mockReturnValue({ data: populatedDelivery(), isLoading: false })
    mocks.useQualityMetrics.mockReturnValue({ data: populatedQuality(), isLoading: false })
    mocks.useCostRollup.mockReturnValue({ data: populatedCost(), isLoading: false })
    mocks.useStageDuration.mockReturnValue({ data: populatedStageDuration(), isLoading: false })

    renderPage()

    mocks.useCompletionThroughput.mockClear()
    mocks.useDeliveryTime.mockClear()
    mocks.useQualityMetrics.mockClear()
    mocks.useCostRollup.mockClear()
    mocks.useStageDuration.mockClear()

    fireEvent.click(screen.getByTestId('insights-range-option-7d'))

    expect(mocks.useCompletionThroughput).toHaveBeenCalledWith('7d')
    expect(mocks.useDeliveryTime).toHaveBeenCalledWith('7d')
    expect(mocks.useQualityMetrics).toHaveBeenCalledWith('7d')
    expect(mocks.useCostRollup).toHaveBeenCalledWith('7d')
    expect(mocks.useStageDuration).toHaveBeenCalledWith('7d')
    expect(screen.getByTestId('insights-page').getAttribute('data-range')).toBe('7d')

    fireEvent.click(screen.getByTestId('insights-range-option-90d'))

    expect(mocks.useCompletionThroughput).toHaveBeenLastCalledWith('90d')
    expect(mocks.useDeliveryTime).toHaveBeenLastCalledWith('90d')
    expect(mocks.useQualityMetrics).toHaveBeenLastCalledWith('90d')
    expect(mocks.useCostRollup).toHaveBeenLastCalledWith('90d')
    expect(mocks.useStageDuration).toHaveBeenLastCalledWith('90d')
    expect(screen.getByTestId('insights-page').getAttribute('data-range')).toBe('90d')
  })

  it('passes the current range to InsightsCharts so chart panels re-render', () => {
    renderPage()

    const charts = screen.getByTestId('insights-charts')
    expect(charts.getAttribute('data-range')).toBe('30d')

    fireEvent.click(screen.getByTestId('insights-range-option-90d'))

    expect(screen.getByTestId('insights-charts').getAttribute('data-range')).toBe('90d')
  })

  it('forwards the new range to each chart panel header via the throughput window badge', () => {
    renderPage()

    expect(screen.getByTestId('throughput-chart-window').textContent).toBe('30d')

    fireEvent.click(screen.getByTestId('insights-range-option-7d'))

    expect(screen.getByTestId('throughput-chart-window').textContent).toBe('7d')

    fireEvent.click(screen.getByTestId('insights-range-option-90d'))

    expect(screen.getByTestId('throughput-chart-window').textContent).toBe('90d')
  })
})