// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type {
  CompletionTrendResponse,
  CumulativeFlowResponse,
  DeliveryTimeMetricsResponse,
  QualityMetricsResponse,
  StageDurationMetricsResponse,
} from '../../../entities/issue'
import type { AgentCostRollupDto, AgentUsageTimeseriesDto } from '../../../entities/agent'

const mocks = vi.hoisted(() => ({
  useCompletionThroughput: vi.fn(),
  useCompletionTrend: vi.fn(),
  useCumulativeFlow: vi.fn(),
  useDeliveryTime: vi.fn(),
  useStageDuration: vi.fn(),
  useQualityMetrics: vi.fn(),
  useEpics: vi.fn(),
  useCostRollup: vi.fn(),
  useAgentUsage: vi.fn(),
}))

vi.mock('../../../entities/issue', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/issue')>()
  return {
    ...actual,
    useCompletionThroughput: mocks.useCompletionThroughput,
    useCompletionTrend: mocks.useCompletionTrend,
    useCumulativeFlow: mocks.useCumulativeFlow,
    useDeliveryTime: mocks.useDeliveryTime,
    useStageDuration: mocks.useStageDuration,
    useQualityMetrics: mocks.useQualityMetrics,
  }
})

vi.mock('../../../entities/epic', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/epic')>()
  return {
    ...actual,
    useEpics: mocks.useEpics,
  }
})

vi.mock('../../../entities/agent', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/agent')>()
  return {
    ...actual,
    useCostRollup: mocks.useCostRollup,
    useAgentUsage: mocks.useAgentUsage,
  }
})

const TEST_PROJECT = {
  id: 'proj-1',
  name: 'demo',
  createdAt: '2024-01-01T00:00:00Z',
  updatedAt: '2024-01-01T00:00:00Z',
  repositories: [],
}

function renderCharts() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj-1" initialProjects={[TEST_PROJECT]}>
        <MemoryRouter initialEntries={['/demo/insights']}>
          <InsightsCharts range="30d" />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

function buildThroughput(): CompletionTrendResponse {
  return {
    bucket: 'day',
    window: { from: '2026-06-01T00:00:00+00:00', to: '2026-07-01T00:00:00+00:00' },
    buckets: [
      { boundary: '2026-06-01', completed: 5, failed: 1 },
      { boundary: '2026-06-02', completed: 8, failed: 0 },
      { boundary: '2026-06-03', completed: 3, failed: 2 },
    ],
  }
}

function buildCompletionTrend(): CompletionTrendResponse {
  return {
    bucket: 'week',
    window: { from: '2026-04-01T00:00:00+00:00', to: '2026-06-30T00:00:00+00:00' },
    buckets: [
      { boundary: '2026-04-05', completed: 4, failed: 1 },
      { boundary: '2026-04-12', completed: 7, failed: 0 },
      { boundary: '2026-04-19', completed: 5, failed: 2 },
      { boundary: '2026-04-26', completed: 9, failed: 1 },
      { boundary: '2026-05-03', completed: 6, failed: 0 },
      { boundary: '2026-05-10', completed: 10, failed: 1 },
      { boundary: '2026-05-17', completed: 4, failed: 2 },
      { boundary: '2026-05-24', completed: 8, failed: 0 },
      { boundary: '2026-05-31', completed: 11, failed: 1 },
      { boundary: '2026-06-07', completed: 7, failed: 0 },
      { boundary: '2026-06-14', completed: 9, failed: 1 },
      { boundary: '2026-06-21', completed: 12, failed: 2 },
    ],
  }
}

function buildCumulativeFlow(): CumulativeFlowResponse {
  return {
    rangeFrom: '2026-04-02',
    rangeTo: '2026-06-30',
    snapshots: [
      { day: '2026-04-02', backlog: 10, plan: 2, build: 1, check: 0, integrate: 0, done: 5 },
      { day: '2026-05-01', backlog: 12, plan: 3, build: 2, check: 1, integrate: 0, done: 8 },
      { day: '2026-05-30', backlog: 9, plan: 2, build: 3, check: 1, integrate: 1, done: 12 },
      { day: '2026-06-29', backlog: 7, plan: 1, build: 2, check: 2, integrate: 1, done: 16 },
    ],
  }
}

function buildDeliveryTime(): DeliveryTimeMetricsResponse {
  return {
    points: [
      { issueNumber: 1, completedAt: '2026-06-10T00:00:00Z', leadDays: 1, cycleDays: 5.2 / 24 },
      { issueNumber: 2, completedAt: '2026-06-12T00:00:00Z', leadDays: 2, cycleDays: 6.1 / 24 },
    ],
    previousCycleDays: 6.3 / 24,
  }
}

function buildStageDuration(): StageDurationMetricsResponse {
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

function buildQuality(): QualityMetricsResponse {
  return {
    window7d: {
      from: '2026-06-23T00:00:00Z',
      to: '2026-06-30T00:00:00Z',
      sampleCount: 4,
      firstTimeRightRate: 0.75,
      stages: [],
    },
    window30d: {
      from: '2026-06-01T00:00:00Z',
      to: '2026-07-01T00:00:00Z',
      sampleCount: 18,
      firstTimeRightRate: 0.72,
      stages: [],
    },
    previousFirstTimeRightRate: 0.81,
    previousSampleCount: 16,
    trend: {
      bucket: 'day',
      from: '2026-06-01T00:00:00+00:00',
      to: '2026-06-30T23:59:59+00:00',
      points: [
        { boundary: '2026-06-01', sampleCount: 1, firstTimeRightRate: 1, reworkRate: 0 },
        { boundary: '2026-06-02', sampleCount: 0, firstTimeRightRate: null, reworkRate: null },
        { boundary: '2026-06-03', sampleCount: 2, firstTimeRightRate: 0.5, reworkRate: 0.5 },
        { boundary: '2026-06-04', sampleCount: 1, firstTimeRightRate: 1, reworkRate: 0 },
      ],
    },
  }
}

function buildCostRollup(): AgentCostRollupDto {
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

function buildAgentUsage(): AgentUsageTimeseriesDto {
  return {
    rangeFrom: '2026-06-01T00:00:00',
    rangeTo: '2026-06-29T00:00:00',
    bucketGranularity: 'day',
    buckets: [
      { bucketStart: '2026-06-01T00:00:00', bucketEnd: '2026-06-01T23:59:59', inputTokens: 0, outputTokens: 0, totalTokens: 0, costAmount: 10, costCurrency: 'USD' },
      { bucketStart: '2026-06-02T00:00:00', bucketEnd: '2026-06-02T23:59:59', inputTokens: 0, outputTokens: 0, totalTokens: 0, costAmount: 25, costCurrency: 'USD' },
      { bucketStart: '2026-06-03T00:00:00', bucketEnd: '2026-06-03T23:59:59', inputTokens: 0, outputTokens: 0, totalTokens: 0, costAmount: 5, costCurrency: 'USD' },
    ],
    cumulativeCostPerShip: [
      { dayEnd: '2026-06-01T23:59:59', cumulativeCost: 10, currency: 'USD', cumulativeShippedCount: 1, costPerShip: 10 },
      { dayEnd: '2026-06-02T23:59:59', cumulativeCost: 35, currency: 'USD', cumulativeShippedCount: 2, costPerShip: 17.5 },
      { dayEnd: '2026-06-03T23:59:59', cumulativeCost: 40, currency: 'USD', cumulativeShippedCount: 2, costPerShip: 20 },
    ],
  }
}

function buildEpics() {
  return [
    {
      id: 'epic-1',
      projectId: 'proj-1',
      number: 1,
      title: 'Auth refactor',
      status: 'in_progress',
      priority: 'high',
      progress: { completed: 4, total: 10 },
      updatedAt: '2026-06-30T00:00:00Z',
    },
    {
      id: 'epic-2',
      projectId: 'proj-1',
      number: 2,
      title: 'Search overhaul',
      status: 'in_progress',
      priority: 'medium',
      progress: { completed: 2, total: 6 },
      updatedAt: '2026-06-30T00:00:00Z',
    },
  ]
}

import { InsightsCharts } from './InsightsCharts'

beforeEach(() => {
  vi.clearAllMocks()
  mocks.useCompletionThroughput.mockReturnValue({ data: buildThroughput(), isLoading: false, isError: false })
  mocks.useCompletionTrend.mockReturnValue({ data: buildCompletionTrend(), isLoading: false, isError: false })
  mocks.useCumulativeFlow.mockReturnValue({ data: buildCumulativeFlow(), isLoading: false, isError: false })
  mocks.useDeliveryTime.mockReturnValue({ data: buildDeliveryTime(), isLoading: false, isError: false })
  mocks.useStageDuration.mockReturnValue({ data: buildStageDuration(), isLoading: false, isError: false })
  mocks.useQualityMetrics.mockReturnValue({ data: buildQuality(), isLoading: false, isError: false })
  mocks.useEpics.mockReturnValue({ data: buildEpics(), isLoading: false, isError: false })
  mocks.useCostRollup.mockReturnValue({ data: buildCostRollup(), isLoading: false, isError: false })
  mocks.useAgentUsage.mockReturnValue({ data: buildAgentUsage(), isLoading: false, isError: false })
})

afterEach(() => {
  cleanup()
  window.localStorage.clear()
})

describe('InsightsCharts four fixed dimension groups', () => {
  it('renders exactly four dimension groups in the fixed order', () => {
    renderCharts()

    const groups = screen.getAllByTestId('insights-chart-group')
    expect(groups).toHaveLength(4)
    expect(groups.map((g) => g.getAttribute('data-dimension'))).toEqual([
      'output',
      'delivery',
      'quality',
      'investment',
    ])
  })

  it('does not render the M1 chart-placeholder zone', () => {
    renderCharts()

    expect(screen.queryByTestId('insights-chart-placeholder')).not.toBeInTheDocument()
  })
})

describe('InsightsCharts group structure', () => {
  it('renders the 产出 group with its four charts in fixed order (EpicProgressList first)', () => {
    renderCharts()

    const group = screen.getAllByTestId('insights-chart-group').find(
      (g) => g.getAttribute('data-dimension') === 'output',
    )!
    const chartContainer = group.querySelector('[data-testid="insights-chart-group-charts"]')!
    const charts = Array.from(chartContainer.querySelectorAll('[data-testid="throughput-chart"], [data-testid="productivity-trend"], [data-testid="cumulative-flow-chart"], [data-testid="productivity-epic-list"]'))

    expect(charts.map((el) => el.getAttribute('data-testid'))).toEqual([
      'productivity-epic-list',
      'throughput-chart',
      'productivity-trend',
      'cumulative-flow-chart',
    ])

    const heading = group.querySelector('[data-testid="insights-chart-group-title"]')!
    expect(heading.textContent).toBe('产出')
    const question = group.querySelector('[data-testid="insights-chart-group-question"]')!
    expect(question.textContent).toBe('你交付了多少？')
  })

  it('renders the 交付效率 group with its two charts', () => {
    renderCharts()

    const group = screen.getAllByTestId('insights-chart-group').find(
      (g) => g.getAttribute('data-dimension') === 'delivery',
    )!
    const chartContainer = group.querySelector('[data-testid="insights-chart-group-charts"]')!
    const charts = Array.from(chartContainer.querySelectorAll('[data-testid="cycle-time-chart"], [data-testid="stage-duration-chart"]'))
    expect(charts.map((el) => el.getAttribute('data-testid'))).toEqual([
      'cycle-time-chart',
      'stage-duration-chart',
    ])

    const heading = group.querySelector('[data-testid="insights-chart-group-title"]')!
    expect(heading.textContent).toBe('交付效率')
    const question = group.querySelector('[data-testid="insights-chart-group-question"]')!
    expect(question.textContent).toBe('多快？')
  })

  it('renders the 质量 group with its two charts', () => {
    renderCharts()

    const group = screen.getAllByTestId('insights-chart-group').find(
      (g) => g.getAttribute('data-dimension') === 'quality',
    )!
    const chartContainer = group.querySelector('[data-testid="insights-chart-group-charts"]')!
    const charts = Array.from(chartContainer.querySelectorAll('[data-testid="productivity-quality"], [data-testid="ftr-trend-chart"]'))
    expect(charts.map((el) => el.getAttribute('data-testid'))).toEqual([
      'productivity-quality',
      'ftr-trend-chart',
    ])

    const heading = group.querySelector('[data-testid="insights-chart-group-title"]')!
    expect(heading.textContent).toBe('质量')
    const question = group.querySelector('[data-testid="insights-chart-group-question"]')!
    expect(question.textContent).toBe('一次做对了吗？')
  })

  it('renders the 投入 group with its two charts', () => {
    renderCharts()

    const group = screen.getAllByTestId('insights-chart-group').find(
      (g) => g.getAttribute('data-dimension') === 'investment',
    )!
    const chartContainer = group.querySelector('[data-testid="insights-chart-group-charts"]')!
    const charts = Array.from(chartContainer.querySelectorAll('[data-testid="productivity-investment"], [data-testid="cost-trend-chart"]'))
    expect(charts.map((el) => el.getAttribute('data-testid'))).toEqual([
      'productivity-investment',
      'cost-trend-chart',
    ])

    const heading = group.querySelector('[data-testid="insights-chart-group-title"]')!
    expect(heading.textContent).toBe('投入')
    const question = group.querySelector('[data-testid="insights-chart-group-question"]')!
    expect(question.textContent).toBe('花了多少？')
  })
})

describe('InsightsCharts time-window annotations', () => {
  it('renders the six D4 window badges in the populated state', () => {
    renderCharts()

    expect(screen.getByTestId('throughput-chart-window')).toBeInTheDocument()
    expect(screen.getByTestId('cycle-time-chart-window')).toBeInTheDocument()
    expect(screen.getByTestId('cumulative-flow-chart-window')).toBeInTheDocument()
    expect(screen.getByTestId('stage-duration-chart-window')).toBeInTheDocument()
    expect(screen.getByTestId('ftr-trend-chart-window')).toBeInTheDocument()
    expect(screen.getByTestId('cost-trend-chart-window')).toBeInTheDocument()
  })

  it('does not render a time-range selector anywhere in the chart groups', () => {
    renderCharts()

    const groups = screen.getAllByTestId('insights-chart-group')
    for (const group of groups) {
      expect(group.querySelector('[data-testid$="-range-selector"]')).toBeNull()
      expect(group.querySelector('input[type="range"]')).toBeNull()
      expect(group.querySelector('select[name*="range" i], select[name*="window" i]')).toBeNull()
    }
  })
})

describe('InsightsCharts range forwarding to panel hooks', () => {
  function renderChartsWithRange(range: '7d' | '30d' | '90d') {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    return render(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId="proj-1" initialProjects={[TEST_PROJECT]}>
          <MemoryRouter initialEntries={['/demo/insights']}>
            <InsightsCharts range={range} />
          </MemoryRouter>
        </ProjectProvider>
      </QueryClientProvider>,
    )
  }

  it('forwards the range to every chart panel hook (except useEpics)', () => {
    mocks.useCompletionThroughput.mockClear()
    mocks.useCompletionTrend.mockClear()
    mocks.useCumulativeFlow.mockClear()
    mocks.useDeliveryTime.mockClear()
    mocks.useStageDuration.mockClear()
    mocks.useQualityMetrics.mockClear()
    mocks.useCostRollup.mockClear()
    mocks.useAgentUsage.mockClear()
    mocks.useEpics.mockClear()

    renderChartsWithRange('7d')

    expect(mocks.useCompletionThroughput).toHaveBeenCalledWith('7d')
    expect(mocks.useCompletionTrend).toHaveBeenCalledWith('7d')
    expect(mocks.useCumulativeFlow).toHaveBeenCalledWith('7d')
    expect(mocks.useDeliveryTime).toHaveBeenCalledWith('7d')
    expect(mocks.useStageDuration).toHaveBeenCalledWith('7d')
    expect(mocks.useQualityMetrics).toHaveBeenCalledWith('7d')
    expect(mocks.useCostRollup).toHaveBeenCalledWith('7d')
    expect(mocks.useAgentUsage).toHaveBeenCalledWith('7d')

    expect(mocks.useEpics).not.toHaveBeenCalledWith('7d')
  })

  it('re-applies the new range when the prop changes from 30d to 90d', () => {
    mocks.useCompletionThroughput.mockClear()
    mocks.useCompletionTrend.mockClear()
    mocks.useCumulativeFlow.mockClear()
    mocks.useDeliveryTime.mockClear()
    mocks.useStageDuration.mockClear()
    mocks.useQualityMetrics.mockClear()
    mocks.useCostRollup.mockClear()
    mocks.useAgentUsage.mockClear()

    const { rerender } = renderChartsWithRange('30d')

    expect(mocks.useCompletionThroughput).toHaveBeenLastCalledWith('30d')

    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    rerender(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId="proj-1" initialProjects={[TEST_PROJECT]}>
          <MemoryRouter initialEntries={['/demo/insights']}>
            <InsightsCharts range="90d" />
          </MemoryRouter>
        </ProjectProvider>
      </QueryClientProvider>,
    )

    expect(mocks.useCompletionThroughput).toHaveBeenLastCalledWith('90d')
    expect(mocks.useCumulativeFlow).toHaveBeenLastCalledWith('90d')
    expect(mocks.useDeliveryTime).toHaveBeenLastCalledWith('90d')
    expect(mocks.useStageDuration).toHaveBeenLastCalledWith('90d')
    expect(mocks.useQualityMetrics).toHaveBeenLastCalledWith('90d')
    expect(mocks.useCostRollup).toHaveBeenLastCalledWith('90d')
    expect(mocks.useAgentUsage).toHaveBeenLastCalledWith('90d')
  })
})
