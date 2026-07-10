import '@testing-library/jest-dom'
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { TEST_PROJECT } from '../../../../tests/test-utils'
import type { CompletionBucketPoint, CompletionTrendResponse } from '../../../entities/issue'

import { CompletionTrend, type CompletionTrendHook } from './CompletionTrend'

function makeBuckets(completedByWeek: number[]): CompletionBucketPoint[] {
  return completedByWeek.map((completed, index) => {
    const day = String((index % 28) + 1).padStart(2, '0')
    const month = String((Math.floor(index / 28) % 12) + 1).padStart(2, '0')
    return {
      boundary: `2026-${month}-${day}`,
      completed,
      failed: 0,
    }
  })
}

function makeTrendResponse(buckets: CompletionBucketPoint[]): CompletionTrendResponse {
  return {
    bucket: 'week',
    window: { from: '2026-04-06T00:00:00+00:00', to: '2026-06-29T00:00:00+00:00' },
    buckets,
  }
}

let completionTrendResult: ReturnType<CompletionTrendHook>

const completionTrendHook: CompletionTrendHook = () => completionTrendResult

function mockTrendResponse(data: CompletionTrendResponse) {
  completionTrendResult = { data, isLoading: false, isError: false }
}

function mockTrendPending() {
  completionTrendResult = { data: undefined, isLoading: true, isError: false }
}

function mockTrendError() {
  completionTrendResult = { data: undefined, isLoading: false, isError: true }
}

function renderTrend() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
        <MemoryRouter initialEntries={['/']}>
          <CompletionTrend range="30d" completionTrendHook={completionTrendHook} />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('CompletionTrend', () => {
  afterEach(() => {
    cleanup()
  })

  it('renders through the shared chart baseline with one marker per returned weekly bucket in order', async () => {
    const counts = [1, 2, 3, 5, 4, 6, 7, 9, 8, 11, 10, 12]
    mockTrendResponse(makeTrendResponse(makeBuckets(counts)))

    const { container } = renderTrend()

    await waitFor(() => {
      const section = screen.getByTestId('productivity-trend')
      expect(section).toBeInTheDocument()
      expect(section).not.toHaveAttribute('data-state', 'empty')

      expect(screen.getByTestId('chart-accessibility')).toBeInTheDocument()

      const markers = screen.getByTestId('line-series').querySelectorAll('circle')
      expect(markers).toHaveLength(counts.length)

      expect(screen.getByTestId('productivity-trend-baseline')).toBeInTheDocument()

      const meta = screen.getByTestId('productivity-trend-meta')
      expect(meta).toHaveTextContent(`${counts.length} weeks`)

      expect(screen.queryByTestId('productivity-trend-empty')).not.toBeInTheDocument()
      expect(container.querySelector('[data-state="empty"]')).toBeNull()
    })
  })

  it('plots completed-only — failed counts do not influence the polyline points', async () => {
    const buckets: CompletionBucketPoint[] = [
      { boundary: '2026-04-06', completed: 2, failed: 9 },
      { boundary: '2026-04-13', completed: 4, failed: 1 },
      { boundary: '2026-04-20', completed: 1, failed: 7 },
    ]
    mockTrendResponse(makeTrendResponse(buckets))

    renderTrend()

    await waitFor(() => {
      const markers = screen.getByTestId('line-series').querySelectorAll('circle')
      const ys = [...markers].map((marker) => Number(marker.getAttribute('cy')))

      expect(ys).toHaveLength(3)

      expect(ys[1]).toBeLessThan(ys[0])
      expect(ys[1]).toBeLessThan(ys[2])

      expect(ys[0]).toBeLessThan(ys[2])
    })
  })

  it('renders the empty state when the endpoint returns no buckets', async () => {
    mockTrendResponse(makeTrendResponse([]))

    const { container } = renderTrend()

    await waitFor(() => {
      const section = screen.getByTestId('productivity-trend')
      expect(section).toBeInTheDocument()
      expect(section).toHaveAttribute('data-state', 'empty')

      const empty = screen.getByTestId('chart-container-empty')
      expect(empty).toBeInTheDocument()
      expect(empty.textContent ?? '').toMatch(/no completion data/i)

      expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
      expect(container.querySelector('svg')).toBeNull()
    })
  })

  it('renders a flat sparkline when every returned bucket has completed=0', async () => {
    const counts = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]
    mockTrendResponse(makeTrendResponse(makeBuckets(counts)))

    const { container } = renderTrend()

    await waitFor(() => {
      const section = screen.getByTestId('productivity-trend')
      expect(section).not.toHaveAttribute('data-state', 'empty')

      expect(screen.getByTestId('chart-accessibility')).toBeInTheDocument()

      const markers = screen.getByTestId('line-series').querySelectorAll('circle')
      expect(markers).toHaveLength(counts.length)

      const ys = [...markers].map((marker) => Number(marker.getAttribute('cy')))
      expect(new Set(ys).size).toBe(1)

      expect(screen.getByTestId('productivity-trend-meta')).toHaveTextContent(`${counts.length} weeks`)
      expect(screen.queryByTestId('productivity-trend-empty')).not.toBeInTheDocument()
      expect(container.querySelector('[data-state="empty"]')).toBeNull()
    })
  })

  it('does not expose any user-facing bucket-size or time-range control', async () => {
    mockTrendResponse(makeTrendResponse(makeBuckets([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12])))

    const { container } = renderTrend()

    await waitFor(() => expect(screen.getByTestId('chart-accessibility')).toBeInTheDocument())

    expect(container.querySelector('select')).toBeNull()
    expect(container.querySelector('input')).toBeNull()
    expect(container.querySelector('button')).toBeNull()

    const section = screen.getByTestId('productivity-trend')
    expect(section.querySelector('[role="combobox"]')).toBeNull()
  })

  it('renders the empty state when the endpoint returns no data (empty buckets)', async () => {
    mockTrendResponse(makeTrendResponse([]))

    const { container } = renderTrend()

    await waitFor(() => {
      const section = screen.getByTestId('productivity-trend')
      expect(section).toHaveAttribute('data-state', 'empty')

      expect(screen.getByTestId('chart-container-empty')).toBeInTheDocument()
      expect(container.querySelector('svg')).toBeNull()
    })
  })

  it('routes loading through the shared chart container without rendering chart content', () => {
    mockTrendPending()

    const { container } = renderTrend()

    expect(screen.getByTestId('chart-container-loading')).toBeInTheDocument()
    expect(screen.queryByTestId('chart-container-error')).not.toBeInTheDocument()
    expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
    expect(container.querySelector('svg')).toBeNull()
  })

  it('routes fetch errors through the shared chart container without rendering chart content', async () => {
    mockTrendError()

    const { container } = renderTrend()

    await waitFor(() => {
      expect(screen.getByTestId('chart-container-error')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('chart-container-loading')).not.toBeInTheDocument()
    expect(screen.queryByTestId('chart-accessibility')).not.toBeInTheDocument()
    expect(container.querySelector('svg')).toBeNull()
  })

  it('uses chart accessibility summary and theme-token line colors', async () => {
    mockTrendResponse(makeTrendResponse(makeBuckets([1, 2, 3])))

    renderTrend()

    await waitFor(() => {
      expect(screen.getByTestId('chart-sr-summary').textContent).toContain('Weekly completion trend')
      const path = screen.getByTestId('line-series').querySelector('path')
      expect(path?.getAttribute('class')).toContain('stroke-chart-1')
      expect(path?.getAttribute('stroke')).toBeNull()
    })
  })
})
