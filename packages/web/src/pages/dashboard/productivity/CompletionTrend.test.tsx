// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { CompletionBucketPoint, CompletionTrendResponse } from '../../../entities/issue'

const useCompletionTrendMock = vi.fn()
vi.mock('../../../entities/issue/api/completion-trend', () => ({
  useCompletionTrend: (...args: unknown[]) => useCompletionTrendMock(...args),
}))

import { CompletionTrend } from './CompletionTrend'

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

function renderTrend() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider>
        <MemoryRouter initialEntries={['/']}>
          <CompletionTrend />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('CompletionTrend', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  afterEach(() => {
    cleanup()
  })

  it('renders an SVG sparkline with one polyline point per returned weekly bucket in order', () => {
    const counts = [1, 2, 3, 5, 4, 6, 7, 9, 8, 11, 10, 12]
    useCompletionTrendMock.mockReturnValue({ data: makeTrendResponse(makeBuckets(counts)) })

    const { container } = renderTrend()

    const section = screen.getByTestId('productivity-trend')
    expect(section).toBeInTheDocument()
    expect(section).not.toHaveAttribute('data-state', 'empty')

    const sparkline = screen.getByTestId('productivity-trend-sparkline')
    expect(sparkline).toBeInTheDocument()
    expect(sparkline.tagName.toLowerCase()).toBe('svg')

    const polyline = screen.getByTestId('productivity-trend-polyline')
    expect(polyline).toBeInTheDocument()
    expect(polyline.tagName.toLowerCase()).toBe('polyline')

    const pointsAttr = polyline.getAttribute('points') ?? ''
    const pointPairs = pointsAttr.trim().split(/\s+/).filter(Boolean)
    expect(pointPairs).toHaveLength(counts.length)

    expect(screen.getByTestId('productivity-trend-baseline')).toBeInTheDocument()

    const meta = screen.getByTestId('productivity-trend-meta')
    expect(meta).toHaveTextContent(`${counts.length} weeks`)

    expect(screen.queryByTestId('productivity-trend-empty')).not.toBeInTheDocument()
    expect(container.querySelector('[data-state="empty"]')).toBeNull()
  })

  it('plots completed-only — failed counts do not influence the polyline points', () => {
    const buckets: CompletionBucketPoint[] = [
      { boundary: '2026-04-06', completed: 2, failed: 9 },
      { boundary: '2026-04-13', completed: 4, failed: 1 },
      { boundary: '2026-04-20', completed: 1, failed: 7 },
    ]
    useCompletionTrendMock.mockReturnValue({ data: makeTrendResponse(buckets) })

    renderTrend()

    const polyline = screen.getByTestId('productivity-trend-polyline')
    const pointsAttr = polyline.getAttribute('points') ?? ''
    const ys = pointsAttr
      .trim()
      .split(/\s+/)
      .filter(Boolean)
      .map((pair) => parseFloat(pair.split(',')[1]))

    expect(ys).toHaveLength(3)

    expect(ys[1]).toBeLessThan(ys[0])
    expect(ys[1]).toBeLessThan(ys[2])

    expect(ys[0]).toBeLessThan(ys[2])
  })

  it('renders the empty state when the endpoint returns no buckets', () => {
    useCompletionTrendMock.mockReturnValue({ data: makeTrendResponse([]) })

    const { container } = renderTrend()

    const section = screen.getByTestId('productivity-trend')
    expect(section).toBeInTheDocument()
    expect(section).toHaveAttribute('data-state', 'empty')

    const empty = screen.getByTestId('productivity-trend-empty')
    expect(empty).toBeInTheDocument()
    expect(empty.textContent ?? '').toMatch(/no completion data/i)

    expect(screen.queryByTestId('productivity-trend-sparkline')).not.toBeInTheDocument()
    expect(screen.queryByTestId('productivity-trend-polyline')).not.toBeInTheDocument()
    expect(container.querySelector('svg')).toBeNull()
  })

  it('renders a flat sparkline when every returned bucket has completed=0', () => {
    const counts = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]
    useCompletionTrendMock.mockReturnValue({
      data: makeTrendResponse(makeBuckets(counts)),
    })

    const { container } = renderTrend()

    const section = screen.getByTestId('productivity-trend')
    expect(section).not.toHaveAttribute('data-state', 'empty')

    const sparkline = screen.getByTestId('productivity-trend-sparkline')
    expect(sparkline).toBeInTheDocument()

    const polyline = screen.getByTestId('productivity-trend-polyline')
    const pointsAttr = polyline.getAttribute('points') ?? ''
    const pointPairs = pointsAttr.trim().split(/\s+/).filter(Boolean)
    expect(pointPairs).toHaveLength(counts.length)

    const ys = pointPairs.map((pair) => parseFloat(pair.split(',')[1]))
    expect(new Set(ys).size).toBe(1)

    expect(screen.getByTestId('productivity-trend-meta')).toHaveTextContent(`${counts.length} weeks`)
    expect(screen.queryByTestId('productivity-trend-empty')).not.toBeInTheDocument()
    expect(container.querySelector('[data-state="empty"]')).toBeNull()
  })

  it('does not expose any user-facing bucket-size or time-range control', () => {
    useCompletionTrendMock.mockReturnValue({
      data: makeTrendResponse(makeBuckets([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12])),
    })

    const { container } = renderTrend()

    expect(container.querySelector('select')).toBeNull()
    expect(container.querySelector('input')).toBeNull()
    expect(container.querySelector('button')).toBeNull()

    const section = screen.getByTestId('productivity-trend')
    expect(section.querySelector('[role="combobox"]')).toBeNull()
  })

  it('renders the empty state when the hook returns no data (undefined)', () => {
    useCompletionTrendMock.mockReturnValue({ data: undefined })

    const { container } = renderTrend()

    const section = screen.getByTestId('productivity-trend')
    expect(section).toHaveAttribute('data-state', 'empty')

    expect(screen.getByTestId('productivity-trend-empty')).toBeInTheDocument()
    expect(container.querySelector('svg')).toBeNull()
  })
})
