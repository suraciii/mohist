// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
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
