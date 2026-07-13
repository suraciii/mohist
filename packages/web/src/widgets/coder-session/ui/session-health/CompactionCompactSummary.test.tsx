import '@testing-library/jest-dom'
import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { CompactionCompactSummary } from './CompactionCompactSummary'
import type { CompactCompaction } from './CompactionCompactSummary'

function makeEntry(overrides: Partial<CompactCompaction> = {}): CompactCompaction {
  return {
    id: 'compaction-1',
    strategy: 'summary',
    contextWindowUsedBefore: 950_000,
    contextWindowUsedAfter: 400_000,
    recordedAt: '2024-01-01T00:00:00Z',
    ...overrides,
  }
}

describe('CompactionCompactSummary', () => {
  it('renders nothing when there are zero compaction events', () => {
    const { container } = render(<CompactionCompactSummary entries={[]} />)
    expect(container.firstChild).toBeNull()
    expect(screen.queryByTestId('compaction-compact-summary')).toBeNull()
  })

  it('renders nothing when the entries prop is undefined or null', () => {
    const { container: c1 } = render(<CompactionCompactSummary entries={undefined as unknown as CompactCompaction[]} />)
    expect(c1.firstChild).toBeNull()
    const { container: c2 } = render(<CompactionCompactSummary entries={null as unknown as CompactCompaction[]} />)
    expect(c2.firstChild).toBeNull()
  })

  it('surfaces the compaction count as a one-line headline', () => {
    const entries = [
      makeEntry({ id: 'c1', recordedAt: '2024-01-01T00:00:00Z' }),
      makeEntry({ id: 'c2', recordedAt: '2024-01-02T00:00:00Z' }),
      makeEntry({ id: 'c3', recordedAt: '2024-01-03T00:00:00Z' }),
    ]
    render(<CompactionCompactSummary entries={entries} />)
    const count = screen.getByTestId('compaction-compact-summary-count')
    expect(count).toHaveTextContent('3 compactions')
    const summary = screen.getByTestId('compaction-compact-summary')
    expect(summary).toHaveAttribute('data-compaction-count', '3')
  })

  it('uses the singular "compaction" label for a single event', () => {
    render(<CompactionCompactSummary entries={[makeEntry()]} />)
    const count = screen.getByTestId('compaction-compact-summary-count')
    expect(count).toHaveTextContent('1 compaction')
    expect(screen.getByTestId('compaction-compact-summary')).toHaveAttribute('data-compaction-count', '1')
  })

  it('lists the distinct strategies used during the session', () => {
    const entries = [
      makeEntry({ id: 'c1', strategy: 'summary' }),
      makeEntry({ id: 'c2', strategy: 'summary' }),
      makeEntry({ id: 'c3', strategy: 'sliding-window' }),
    ]
    render(<CompactionCompactSummary entries={entries} />)
    const strategies = screen.getByTestId('compaction-compact-summary-strategies')
    expect(strategies.textContent).toContain('summary')
    expect(strategies.textContent).toContain('sliding-window')
  })

  it('falls back to "strategy unknown" when no events carry a strategy', () => {
    const entries = [
      makeEntry({ id: 'c1', strategy: undefined }),
      makeEntry({ id: 'c2', strategy: null }),
    ]
    render(<CompactionCompactSummary entries={entries} />)
    const strategies = screen.getByTestId('compaction-compact-summary-strategies')
    expect(strategies).toHaveTextContent('strategy unknown')
  })

  it('renders the time range when events carry timestamps', () => {
    const entries = [
      makeEntry({ id: 'c1', recordedAt: '2024-01-01T10:00:00Z' }),
      makeEntry({ id: 'c2', recordedAt: '2024-01-02T11:30:00Z' }),
      makeEntry({ id: 'c3', recordedAt: '2024-01-03T09:15:00Z' }),
    ]
    render(<CompactionCompactSummary entries={entries} />)
    const times = screen.getByTestId('compaction-compact-summary-times')
    // The stringification is locale-dependent via toLocaleString, so
    // assert structural presence: the time range must show a separator
    // arrow and contain *some* date-bearing substring (non-empty).
    expect(times.textContent).not.toBe('')
    expect(times.textContent).toMatch(/→/)
  })

  it('omits the time range when no events carry timestamps', () => {
    const entries = [
      makeEntry({ id: 'c1', recordedAt: undefined }),
      makeEntry({ id: 'c2', recordedAt: null }),
    ]
    render(<CompactionCompactSummary entries={entries} />)
    expect(screen.queryByTestId('compaction-compact-summary-times')).toBeNull()
  })

  it('aggregates the token reduction across all events', () => {
    const entries = [
      makeEntry({ id: 'c1', contextWindowUsedBefore: 800_000, contextWindowUsedAfter: 200_000 }),
      makeEntry({ id: 'c2', contextWindowUsedBefore: 700_000, contextWindowUsedAfter: 200_000 }),
      makeEntry({ id: 'c3', contextWindowUsedBefore: 700_000, contextWindowUsedAfter: 300_000 }),
    ]
    render(<CompactionCompactSummary entries={entries} />)
    const reduction = screen.getByTestId('compaction-compact-summary-reduction')
    // Total reduction: (800_000-200_000) + (700_000-200_000) + (700_000-300_000)
    //                 = 600_000 + 500_000 + 400_000 = 1.5M
    // Total before  : 2_200_000 => 1.5M / 2.2M ≈ 68% reduction
    expect(reduction.textContent).toMatch(/reduced by/)
    expect(reduction.textContent).toContain('1.5M')
    expect(reduction.textContent).toMatch(/\(68%\)/)
  })

  it('ignores events with no real reduction (after >= before) in the aggregate', () => {
    const entries = [
      makeEntry({ id: 'c1', contextWindowUsedBefore: 950_000, contextWindowUsedAfter: 400_000 }),
      makeEntry({ id: 'c2', contextWindowUsedBefore: 100_000, contextWindowUsedAfter: 100_000 }),
      makeEntry({ id: 'c3', contextWindowUsedBefore: 200_000, contextWindowUsedAfter: 300_000 }),
    ]
    render(<CompactionCompactSummary entries={entries} />)
    const reduction = screen.getByTestId('compaction-compact-summary-reduction')
    // Only c1 contributes: 550_000 reduction, 950_000 before => ~58%
    expect(reduction.textContent).toContain('550.0k')
    expect(reduction.textContent).toMatch(/\(58%\)/)
  })

  it('shows "reduction unknown" when no events carry countable reduction', () => {
    const entries = [
      makeEntry({ id: 'c1', contextWindowUsedBefore: null, contextWindowUsedAfter: null }),
      makeEntry({ id: 'c2', contextWindowUsedBefore: undefined, contextWindowUsedAfter: undefined }),
    ]
    render(<CompactionCompactSummary entries={entries} />)
    const reduction = screen.getByTestId('compaction-compact-summary-reduction')
    expect(reduction).toHaveTextContent('reduction unknown')
  })

  it('builds a descriptive tooltip combining count, times, strategies, and reduction', () => {
    const entries = [
      makeEntry({ id: 'c1', strategy: 'summary', recordedAt: '2024-01-01T10:00:00Z' }),
      makeEntry({ id: 'c2', strategy: 'sliding-window', recordedAt: '2024-01-02T11:30:00Z' }),
    ]
    render(<CompactionCompactSummary entries={entries} />)
    const summary = screen.getByTestId('compaction-compact-summary')
    const title = summary.getAttribute('title') ?? summary.getAttribute('aria-label') ?? ''
    expect(title).toMatch(/2 compactions/)
    expect(title).toContain('summary')
    expect(title).toContain('sliding-window')
    expect(title).toMatch(/→/)
    expect(title).toMatch(/reduced by/)
  })

  it('exposes compaction-count, time range, strategies and reduction via accessible data-testids', () => {
    const entries = [
      makeEntry({ id: 'c1', strategy: 'summary', recordedAt: '2024-01-01T10:00:00Z' }),
      makeEntry({ id: 'c2', strategy: 'summary', recordedAt: '2024-01-02T11:30:00Z' }),
    ]
    render(<CompactionCompactSummary entries={entries} />)
    expect(screen.getByTestId('compaction-compact-summary')).toBeInTheDocument()
    expect(screen.getByTestId('compaction-compact-summary-count')).toBeInTheDocument()
    expect(screen.getByTestId('compaction-compact-summary-times')).toBeInTheDocument()
    expect(screen.getByTestId('compaction-compact-summary-strategies')).toBeInTheDocument()
    expect(screen.getByTestId('compaction-compact-summary-reduction')).toBeInTheDocument()
  })
})
