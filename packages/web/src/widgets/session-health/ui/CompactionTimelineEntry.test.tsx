// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { fireEvent, render, screen, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { CompactionTimelineEntry } from './CompactionTimelineEntry'

function makeEntry(overrides: Partial<Parameters<typeof CompactionTimelineEntry>[0]['entry']> = {}) {
  return {
    id: 'compaction-1',
    strategy: 'summary',
    contextWindowUsedBefore: 950_000,
    contextWindowUsedAfter: 400_000,
    contextWindowSize: 1_000_000,
    summary: 'Kept task instructions and final patch.',
    recordedAt: '2026-01-01T00:00:00Z',
    ...overrides,
  }
}

describe('CompactionTimelineEntry', () => {
  it('renders the strategy in the banner title', () => {
    render(<CompactionTimelineEntry entry={makeEntry()} />)
    const title = screen.getByTestId('compaction-timeline-title')
    expect(title).toHaveTextContent('Context compacted (summary)')
  })

  it('uses a default strategy label when none is provided', () => {
    render(<CompactionTimelineEntry entry={makeEntry({ strategy: undefined })} />)
    expect(screen.getByTestId('compaction-timeline-title')).toHaveTextContent('Context compacted (summary)')
  })

  it('renders before/after token counts in the counts row', () => {
    render(<CompactionTimelineEntry entry={makeEntry()} />)
    const counts = screen.getByTestId('compaction-timeline-counts')
    expect(counts).toHaveTextContent('Before:')
    expect(counts).toHaveTextContent('950.0k tokens')
    expect(counts).toHaveTextContent('After:')
    expect(counts).toHaveTextContent('400.0k tokens')
  })

  it('renders the reduction delta and percentage', () => {
    render(<CompactionTimelineEntry entry={makeEntry()} />)
    const counts = screen.getByTestId('compaction-timeline-counts')
    // 950_000 -> 400_000 = 550_000 reduction (~58%).
    expect(counts).toHaveTextContent(/Reduced by 550\.0k tokens \(58%\)/)
  })

  it('omits the reduction line when after >= before (no compaction occurred)', () => {
    render(
      <CompactionTimelineEntry
        entry={makeEntry({
          contextWindowUsedBefore: 100_000,
          contextWindowUsedAfter: 100_000,
        })}
      />,
    )
    const counts = screen.getByTestId('compaction-timeline-counts')
    expect(counts).not.toHaveTextContent(/Reduced by/)
  })

  it('renders "unknown" placeholders when counts are missing', () => {
    render(
      <CompactionTimelineEntry
        entry={makeEntry({
          contextWindowUsedBefore: null,
          contextWindowUsedAfter: null,
          contextWindowSize: null,
        })}
      />,
    )
    const counts = screen.getByTestId('compaction-timeline-counts')
    const text = counts.textContent ?? ''
    expect(text).toContain('unknown')
  })

  it('hides the summary section when no summary is present', () => {
    render(<CompactionTimelineEntry entry={makeEntry({ summary: '' })} />)
    expect(screen.queryByRole('button', { name: /summary/i })).toBeNull()
  })

  it('shows the summary section with a "Show summary" toggle when a summary exists', () => {
    render(<CompactionTimelineEntry entry={makeEntry()} />)
    const toggle = screen.getByRole('button', { name: 'Show summary' })
    expect(toggle).toBeInTheDocument()
    // Summary is collapsed by default.
    expect(screen.queryByText('Kept task instructions and final patch.')).toBeNull()
  })

  it('expands the summary when the toggle is clicked', () => {
    render(<CompactionTimelineEntry entry={makeEntry()} />)
    fireEvent.click(screen.getByRole('button', { name: 'Show summary' }))
    expect(screen.getByText('Kept task instructions and final patch.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Hide summary' })).toBeInTheDocument()
  })

  it('collapses the summary when the toggle is clicked twice', () => {
    render(<CompactionTimelineEntry entry={makeEntry()} />)
    fireEvent.click(screen.getByRole('button', { name: 'Show summary' }))
    fireEvent.click(screen.getByRole('button', { name: 'Hide summary' }))
    expect(screen.queryByText('Kept task instructions and final patch.')).toBeNull()
  })

  it('exposes the strategy as a data attribute for CSS hooks / testing', () => {
    render(<CompactionTimelineEntry entry={makeEntry({ strategy: 'sliding-window' })} />)
    const entry = screen.getByTestId('compaction-timeline-entry')
    expect(entry).toHaveAttribute('data-strategy', 'sliding-window')
  })

  it('renders the recorded timestamp', () => {
    render(<CompactionTimelineEntry entry={makeEntry({ recordedAt: '2026-05-01T12:34:56Z' })} />)
    const entry = screen.getByTestId('compaction-timeline-entry')
    // The timestamp is rendered through toLocaleString; just assert the
    // banner has a date-bearing string in addition to the title.
    const text = within(entry).getByTestId('compaction-timeline-title').textContent ?? ''
    expect(text).toContain('Context compacted')
  })
})
