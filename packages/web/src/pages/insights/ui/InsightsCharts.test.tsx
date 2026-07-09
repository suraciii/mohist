// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { useMswServer } from '../../../../tests/support/msw'
import { ProjectProvider } from '../../../entities/project'
import type {
  CompletionTrendResponse,
  DeliveryTimeMetricsResponse,
  QualityMetricsResponse,
  StageDurationMetricsResponse,
} from '../../../entities/issue'
import type { AgentUsageTimeseriesDto } from '../../../entities/agent'

import { InsightsCharts } from './InsightsCharts'

let _completionDay: CompletionTrendResponse
let _completionWeek: CompletionTrendResponse
let _delivery: DeliveryTimeMetricsResponse
let _stageDuration: StageDurationMetricsResponse
let _quality: QualityMetricsResponse
let _agentUsage: AgentUsageTimeseriesDto

let _capturedUrls: string[]

const COMPLETION_PATH = '*/api/projects/:projectId/issues/metrics/completion'
const DELIVERY_TIME_PATH = '*/api/projects/:projectId/issues/metrics/delivery-time'
const STAGE_DURATION_PATH = '*/api/projects/:projectId/issues/metrics/stage-duration'
const QUALITY_PATH = '*/api/projects/:projectId/issues/metrics/quality'
const USAGE_PATH = '*/api/projects/:projectId/agent/usage'

useMswServer(
  http.get(COMPLETION_PATH, ({ request }) => {
    _capturedUrls.push(request.url)
    const bucket = new URL(request.url).searchParams.get('bucket')
    const data = bucket === 'week' ? _completionWeek : _completionDay
    return HttpResponse.json({ success: true, data })
  }),
  http.get(DELIVERY_TIME_PATH, ({ request }) => {
    _capturedUrls.push(request.url)
    return HttpResponse.json({ success: true, data: _delivery })
  }),
  http.get(STAGE_DURATION_PATH, ({ request }) => {
    _capturedUrls.push(request.url)
    return HttpResponse.json({ success: true, data: _stageDuration })
  }),
  http.get(QUALITY_PATH, ({ request }) => {
    _capturedUrls.push(request.url)
    return HttpResponse.json({ success: true, data: _quality })
  }),
  http.get(USAGE_PATH, ({ request }) => {
    _capturedUrls.push(request.url)
    return HttpResponse.json({ success: true, data: _agentUsage })
  }),
)

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
    window: {
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

beforeEach(() => {
  _capturedUrls = []
  _completionDay = buildThroughput()
  _completionWeek = buildCompletionTrend()
  _delivery = buildDeliveryTime()
  _stageDuration = buildStageDuration()
  _quality = buildQuality()
  _agentUsage = buildAgentUsage()
})

afterEach(() => {
  cleanup()
  window.localStorage.clear()
})

async function waitForChartGroupsLoaded(count: number) {
  await waitFor(() => {
    expect(screen.getAllByTestId('insights-chart-group')).toHaveLength(count)
  })
}

describe('InsightsCharts four fixed dimension groups', () => {
  it('renders exactly four dimension groups in the fixed order', async () => {
    renderCharts()

    await waitForChartGroupsLoaded(4)

    const groups = screen.getAllByTestId('insights-chart-group')
    expect(groups).toHaveLength(4)
    expect(groups.map((g) => g.getAttribute('data-dimension'))).toEqual([
      'output',
      'delivery',
      'quality',
      'investment',
    ])
  })

  it('does not render the M1 chart-placeholder zone', async () => {
    renderCharts()

    await waitForChartGroupsLoaded(4)

    expect(screen.queryByTestId('insights-chart-placeholder')).not.toBeInTheDocument()
  })

  it('does not render the removed Investment or In-progress Epic progress panels', async () => {
    renderCharts()

    await waitForChartGroupsLoaded(4)

    expect(screen.queryByTestId('productivity-investment')).not.toBeInTheDocument()
    expect(screen.queryByTestId('productivity-investment-toggle')).not.toBeInTheDocument()
    expect(screen.queryByTestId('productivity-investment-total-cost')).not.toBeInTheDocument()
    expect(screen.queryByTestId('productivity-investment-cost-per-ship')).not.toBeInTheDocument()
    expect(screen.queryByTestId('productivity-investment-done-issues')).not.toBeInTheDocument()
    expect(screen.queryByTestId('productivity-epic-list')).not.toBeInTheDocument()
    expect(screen.queryByTestId('productivity-epic-list-item-0')).not.toBeInTheDocument()
    expect(screen.queryByTestId('productivity-epic-list-bar-0')).not.toBeInTheDocument()
  })
})

describe('InsightsCharts group structure', () => {
  it('renders the 产出 group with Throughput and Completion Trend in fixed order', async () => {
    renderCharts()

    await waitFor(() => {
      const group = screen.getAllByTestId('insights-chart-group').find(
        (g) => g.getAttribute('data-dimension') === 'output',
      )!
      const chartContainer = group.querySelector('[data-testid="insights-chart-group-charts"]')!
      const charts = Array.from(chartContainer.querySelectorAll('[data-testid="throughput-chart"], [data-testid="productivity-trend"]'))
      expect(charts.map((el) => el.getAttribute('data-testid'))).toEqual([
        'throughput-chart',
        'productivity-trend',
      ])
    })

    const group = screen.getAllByTestId('insights-chart-group').find(
      (g) => g.getAttribute('data-dimension') === 'output',
    )!
    const heading = group.querySelector('[data-testid="insights-chart-group-title"]')!
    expect(heading.textContent).toBe('产出')
    const question = group.querySelector('[data-testid="insights-chart-group-question"]')!
    expect(question.textContent).toBe('你交付了多少？')
  })

  it('renders the 交付效率 group with its two charts', async () => {
    renderCharts()

    await waitFor(() => {
      const group = screen.getAllByTestId('insights-chart-group').find(
        (g) => g.getAttribute('data-dimension') === 'delivery',
      )!
      const chartContainer = group.querySelector('[data-testid="insights-chart-group-charts"]')!
      const charts = Array.from(chartContainer.querySelectorAll('[data-testid="cycle-time-chart"], [data-testid="stage-duration-chart"]'))
      expect(charts.map((el) => el.getAttribute('data-testid'))).toEqual([
        'cycle-time-chart',
        'stage-duration-chart',
      ])
    })

    const group = screen.getAllByTestId('insights-chart-group').find(
      (g) => g.getAttribute('data-dimension') === 'delivery',
    )!
    const heading = group.querySelector('[data-testid="insights-chart-group-title"]')!
    expect(heading.textContent).toBe('交付效率')
    const question = group.querySelector('[data-testid="insights-chart-group-question"]')!
    expect(question.textContent).toBe('多快？')
  })

  it('renders the 质量 group with its two charts', async () => {
    renderCharts()

    await waitFor(() => {
      const group = screen.getAllByTestId('insights-chart-group').find(
        (g) => g.getAttribute('data-dimension') === 'quality',
      )!
      const chartContainer = group.querySelector('[data-testid="insights-chart-group-charts"]')!
      const charts = Array.from(chartContainer.querySelectorAll('[data-testid="productivity-quality"], [data-testid="ftr-trend-chart"]'))
      expect(charts.map((el) => el.getAttribute('data-testid'))).toEqual([
        'productivity-quality',
        'ftr-trend-chart',
      ])
    })

    const group = screen.getAllByTestId('insights-chart-group').find(
      (g) => g.getAttribute('data-dimension') === 'quality',
    )!
    const heading = group.querySelector('[data-testid="insights-chart-group-title"]')!
    expect(heading.textContent).toBe('质量')
    const question = group.querySelector('[data-testid="insights-chart-group-question"]')!
    expect(question.textContent).toBe('一次做对了吗？')
  })

  it('renders the 投入 group with only Cost Trend', async () => {
    renderCharts()

    await waitFor(() => {
      const group = screen.getAllByTestId('insights-chart-group').find(
        (g) => g.getAttribute('data-dimension') === 'investment',
      )!
      const chartContainer = group.querySelector('[data-testid="insights-chart-group-charts"]')!
      const charts = Array.from(chartContainer.querySelectorAll('[data-testid="cost-trend-chart"]'))
      expect(charts.map((el) => el.getAttribute('data-testid'))).toEqual([
        'cost-trend-chart',
      ])
    })

    const group = screen.getAllByTestId('insights-chart-group').find(
      (g) => g.getAttribute('data-dimension') === 'investment',
    )!
    const heading = group.querySelector('[data-testid="insights-chart-group-title"]')!
    expect(heading.textContent).toBe('投入')
    const question = group.querySelector('[data-testid="insights-chart-group-question"]')!
    expect(question.textContent).toBe('花了多少？')
  })
})

describe('InsightsCharts time-window annotations', () => {
  it('renders the five D4 window badges in the populated state', async () => {
    renderCharts()

    await waitFor(() => {
      expect(screen.getByTestId('throughput-chart-window')).toBeInTheDocument()
      expect(screen.getByTestId('cycle-time-chart-window')).toBeInTheDocument()
      expect(screen.getByTestId('stage-duration-chart-window')).toBeInTheDocument()
      expect(screen.getByTestId('ftr-trend-chart-window')).toBeInTheDocument()
      expect(screen.getByTestId('cost-trend-chart-window')).toBeInTheDocument()
    })
  })

  it('does not render a time-range selector anywhere in the chart groups', async () => {
    renderCharts()

    await waitForChartGroupsLoaded(4)

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

  it('forwards the range to every chart panel hook', async () => {
    renderChartsWithRange('7d')

    await waitFor(() => {
      expect(_capturedUrls.length).toBeGreaterThan(0)
    })

    const completionUrls = _capturedUrls.filter((u) => u.includes('/issues/metrics/completion'))
    expect(completionUrls.length).toBe(2)
    expect(completionUrls.some((u) => u.includes('bucket=day') && u.includes('range=7d'))).toBe(true)
    expect(completionUrls.some((u) => u.includes('bucket=week') && u.includes('range=7d'))).toBe(true)
    expect(_capturedUrls.some((u) => u.includes('/issues/metrics/delivery-time') && u.includes('range=7d'))).toBe(true)
    expect(_capturedUrls.some((u) => u.includes('/issues/metrics/stage-duration') && u.includes('range=7d'))).toBe(true)
    expect(_capturedUrls.some((u) => u.includes('/issues/metrics/quality') && u.includes('range=7d'))).toBe(true)
    expect(_capturedUrls.some((u) => u.includes('/agent/usage') && u.includes('range=7d'))).toBe(true)
  })

  it('re-applies the new range when the prop changes from 30d to 90d', async () => {
    const { rerender } = renderChartsWithRange('30d')

    await waitFor(() => {
      expect(_capturedUrls.filter((u) => u.includes('range=30d')).length).toBeGreaterThan(0)
    })

    _capturedUrls = []

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

    await waitFor(() => {
      expect(_capturedUrls.filter((u) => u.includes('range=90d')).length).toBeGreaterThan(0)
    })
  })

  it('reflects the selected range on every retained chart window indicator', async () => {
    const rangeByCode = {
      '7d': {
        stage: { from: '2026-06-23T00:00:00+00:00', to: '2026-06-30T23:59:59+00:00' },
        ftr: { from: '2026-06-23T00:00:00+00:00', to: '2026-06-30T23:59:59+00:00' },
        cost: { rangeFrom: '2026-06-23T00:00:00', rangeTo: '2026-06-30T23:59:59' },
        qualityWindow: { from: '2026-06-23T00:00:00+00:00', to: '2026-06-30T23:59:59+00:00', sampleCount: 5, firstTimeRightRate: 0.8, stages: [] },
      },
      '30d': {
        stage: { from: '2026-06-01T00:00:00+00:00', to: '2026-07-01T00:00:00+00:00' },
        ftr: { from: '2026-06-01T00:00:00+00:00', to: '2026-06-30T23:59:59+00:00' },
        cost: { rangeFrom: '2026-06-01T00:00:00', rangeTo: '2026-06-29T23:59:59' },
        qualityWindow: { from: '2026-06-01T00:00:00+00:00', to: '2026-06-30T23:59:59+00:00', sampleCount: 25, firstTimeRightRate: 0.6, stages: [] },
      },
      '90d': {
        stage: { from: '2026-04-02T00:00:00+00:00', to: '2026-07-01T00:00:00+00:00' },
        ftr: { from: '2026-04-02T00:00:00+00:00', to: '2026-06-30T23:59:59+00:00' },
        cost: { rangeFrom: '2026-04-02T00:00:00', rangeTo: '2026-06-30T23:59:59' },
        qualityWindow: { from: '2026-04-02T00:00:00+00:00', to: '2026-06-30T23:59:59+00:00', sampleCount: 60, firstTimeRightRate: 0.7, stages: [] },
      },
    }

    function setupRange(range: '7d' | '30d' | '90d') {
      const cfg = rangeByCode[range]
      _stageDuration = {
        window: cfg.stage,
        stages: [
          { stage: 'plan', sampleCount: 3, averageSeconds: 1800, medianSeconds: 1500 },
          { stage: 'build', sampleCount: 3, averageSeconds: 5400, medianSeconds: 4800 },
        ],
        flowEfficiencyRatio: 0.6,
        waitBreakout: { averageApprovalGateWaitSeconds: 600, averageInactiveGapSeconds: 1200 },
      }
      _quality = {
        window: cfg.qualityWindow,
        trend: {
          bucket: 'day',
          from: cfg.ftr.from,
          to: cfg.ftr.to,
          points: [{ boundary: cfg.ftr.from, sampleCount: 3, firstTimeRightRate: 0.7, reworkRate: 0.1 }],
        },
      }
      _agentUsage = {
        rangeFrom: cfg.cost.rangeFrom,
        rangeTo: cfg.cost.rangeTo,
        bucketGranularity: 'day',
        buckets: [{ bucketStart: cfg.cost.rangeFrom, bucketEnd: cfg.cost.rangeTo, inputTokens: 0, outputTokens: 0, totalTokens: 0, costAmount: 5, costCurrency: 'USD' }],
        cumulativeCostPerShip: null,
      }
    }

    function renderWithRange(range: '7d' | '30d' | '90d') {
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

    setupRange('7d')
    const { rerender } = renderWithRange('7d')

    await waitFor(() => {
      expect(screen.getByTestId('throughput-chart-window').textContent).toBe('7d')
      expect(screen.getByTestId('cycle-time-chart-window').textContent).toBe('7d')
      expect(screen.getByTestId('stage-duration-chart-window').textContent).toContain('Jun 23')
      expect(screen.getByTestId('stage-duration-chart-window').textContent).toContain('Jun 30')
      expect(screen.getByTestId('ftr-trend-chart-window').textContent).toContain('Jun 23')
      expect(screen.getByTestId('ftr-trend-chart-window').textContent).toContain('Jun 30')
      expect(screen.getByTestId('cost-trend-chart-window').textContent).toContain('Jun 23')
      expect(screen.getByTestId('cost-trend-chart-window').textContent).toContain('Jun 30')
    })

    setupRange('90d')
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

    await waitFor(() => {
      expect(screen.getByTestId('throughput-chart-window').textContent).toBe('90d')
      expect(screen.getByTestId('cycle-time-chart-window').textContent).toBe('90d')
      expect(screen.getByTestId('stage-duration-chart-window').textContent).toContain('Apr 2')
      expect(screen.getByTestId('stage-duration-chart-window').textContent).toContain('Jul 1')
      expect(screen.getByTestId('ftr-trend-chart-window').textContent).toContain('Apr 2')
      expect(screen.getByTestId('ftr-trend-chart-window').textContent).toContain('Jun 30')
      expect(screen.getByTestId('cost-trend-chart-window').textContent).toContain('Apr 2')
      expect(screen.getByTestId('cost-trend-chart-window').textContent).toContain('Jun 30')
    })
  })
})
