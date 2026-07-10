// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { ProjectProvider } from '../../../entities/project'
import { InsightsPage } from './InsightsPage'

useMswServer()

const EMPTY_HANDLERS = [
  http.get('*/api/projects/:projectId/agent/usage', () =>
    HttpResponse.json({
      success: true,
      data: {
        rangeFrom: '2026-01-01T00:00:00Z',
        rangeTo: '2026-01-31T23:59:59Z',
        bucketGranularity: 'day',
        buckets: [],
        cumulativeCostPerShip: [],
      },
    }),
  ),
  http.get('*/api/projects/:projectId/issues/metrics/completion', () =>
    HttpResponse.json({ success: true, data: { bucket: 'day', window: null, buckets: [] } }),
  ),
  http.get('*/api/projects/:projectId/issues/metrics/delivery-time', () =>
    HttpResponse.json({ success: true, data: { window: null, buckets: [] } }),
  ),
  http.get('*/api/projects/:projectId/issues/metrics/quality', () =>
    HttpResponse.json({ success: true, data: { window: null, ftrRate: null, bucketCount: 0, buckets: [] } }),
  ),
  http.get('*/api/projects/:projectId/issues/metrics/stage-duration', () =>
    HttpResponse.json({ success: true, data: { window: null, stages: null } }),
  ),
]

const TEST_PROJECT = {
  id: 'proj-1',
  name: 'demo',
  createdAt: '2024-01-01T00:00:00Z',
  updatedAt: '2024-01-01T00:00:00Z',
  repositories: [],
}

function renderPage() {
  server.use(...EMPTY_HANDLERS)
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

afterEach(() => {
  cleanup()
  window.localStorage.clear()
})

describe('InsightsPage structure', () => {
  it('renders the page with title, range selector, and charts section directly under the header', () => {
    renderPage()

    expect(screen.getByTestId('insights-page')).toBeInTheDocument()
    expect(screen.getByTestId('insights-title').textContent).toBe('Insights')
    expect(screen.getByTestId('insights-subtitle').textContent).toBe('最近做得怎么样。')
    expect(screen.getByTestId('insights-charts-section')).toBeInTheDocument()
    expect(screen.queryByTestId('insights-signal-section')).not.toBeInTheDocument()
    expect(screen.queryByTestId('signal-summary')).not.toBeInTheDocument()
  })

  it('does not render a Signal Summary heading on the page', () => {
    renderPage()

    expect(screen.queryByText('Signal Summary')).not.toBeInTheDocument()
  })

  it('does not frame the page as conclusion-first', () => {
    renderPage()

    const subtitle = screen.getByTestId('insights-subtitle').textContent
    expect(subtitle).not.toContain('先看结论')
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

  it('does not render the removed Investment or In-progress Epic progress panels', () => {
    renderPage()

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
