import { beforeEach, describe, expect, it, vi } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'
import { EventTimelinePanel } from './EventTimelinePanel'
import type { TimelineEntry } from '../model/types'

vi.mock('../useEventTimeline', () => ({
  useEventTimeline: vi.fn(),
}))

import { useEventTimeline } from '../useEventTimeline'

function makeEntry(overrides: Partial<TimelineEntry> = {}): TimelineEntry {
  return {
    id: 'evt-1',
    type: 'com.mohist.workflow.run.started',
    time: '2026-06-18T10:00:00.000Z',
    source: 'WORKFLOW',
    category: 'workflow',
    attention: false,
    description: 'Run started',
    detail: null,
    payload: {},
    isLive: false,
    ...overrides,
  }
}

beforeEach(() => {
  vi.mocked(useEventTimeline).mockReturnValue({ entries: [], isLoading: false })
})

describe('EventTimelinePanel', () => {
  it('renders the panel and rows', () => {
    vi.mocked(useEventTimeline).mockReturnValue({
      entries: [
        makeEntry({ id: '1', description: 'Run started', category: 'workflow' }),
        makeEntry({ id: '2', description: 'Stage moved from Plan to Build', category: 'workflow' }),
      ],
      isLoading: false,
    })

    render(<EventTimelinePanel issueNumber={42} issueId="issue-42" workflowStatus="running" />)

    expect(screen.getByTestId('event-timeline-panel')).toBeInTheDocument()
    expect(screen.getAllByTestId('event-timeline-row')).toHaveLength(2)
    expect(screen.getByText('Run started')).toBeInTheDocument()
    expect(screen.getByText('Stage moved from Plan to Build')).toBeInTheDocument()
  })

  it('shows empty state when no events', () => {
    render(<EventTimelinePanel issueNumber={42} issueId="issue-42" />)

    expect(screen.getByTestId('timeline-empty-state')).toBeInTheDocument()
    expect(screen.getByText('No activity yet.')).toBeInTheDocument()
  })

  it('shows pulsing live badge when workflow is running', () => {
    vi.mocked(useEventTimeline).mockReturnValue({
      entries: [makeEntry()],
      isLoading: false,
    })

    render(<EventTimelinePanel issueNumber={42} issueId="issue-42" workflowStatus="running" />)

    expect(screen.getByTestId('timeline-live-badge')).toBeInTheDocument()
  })

  it('shows de-emphasized live badge when workflow is inactive', () => {
    vi.mocked(useEventTimeline).mockReturnValue({
      entries: [makeEntry()],
      isLoading: false,
    })

    render(<EventTimelinePanel issueNumber={42} issueId="issue-42" workflowStatus="completed" />)

    expect(screen.getByTestId('timeline-inactive-badge')).toBeInTheDocument()
  })

  it('filters events by category', () => {
    vi.mocked(useEventTimeline).mockReturnValue({
      entries: [
        makeEntry({ id: '1', category: 'workflow', description: 'Run started' }),
        makeEntry({ id: '2', category: 'failure', description: 'Run failed', attention: true }),
      ],
      isLoading: false,
    })

    render(<EventTimelinePanel issueNumber={42} issueId="issue-42" />)

    expect(screen.getAllByTestId('event-timeline-row')).toHaveLength(2)

    fireEvent.click(screen.getByTestId('category-filter-workflow'))

    expect(screen.getAllByTestId('event-timeline-row')).toHaveLength(1)
    expect(screen.getByText('Run failed')).toBeInTheDocument()
    expect(screen.queryByText('Run started')).not.toBeInTheDocument()

    fireEvent.click(screen.getByTestId('timeline-clear-filters'))

    expect(screen.getAllByTestId('event-timeline-row')).toHaveLength(2)
  })

  it('shows category counts on chips', () => {
    vi.mocked(useEventTimeline).mockReturnValue({
      entries: [
        makeEntry({ id: '1', category: 'workflow' }),
        makeEntry({ id: '2', category: 'workflow' }),
        makeEntry({ id: '3', category: 'failure', attention: true }),
      ],
      isLoading: false,
    })

    render(<EventTimelinePanel issueNumber={42} issueId="issue-42" />)

    expect(screen.getByTestId('category-filter-workflow')).toHaveTextContent('2')
    expect(screen.getByTestId('category-filter-failure')).toHaveTextContent('1')
  })

  it('toggles order between newest-first and chronological', () => {
    vi.mocked(useEventTimeline).mockReturnValue({
      entries: [
        makeEntry({ id: '1', time: '2026-06-18T10:00:00.000Z', description: 'First' }),
        makeEntry({ id: '2', time: '2026-06-18T11:00:00.000Z', description: 'Second' }),
      ],
      isLoading: false,
    })

    render(<EventTimelinePanel issueNumber={42} issueId="issue-42" />)

    const rows = screen.getAllByTestId('event-timeline-row')
    expect(rows[0]).toHaveTextContent('Second')
    expect(rows[1]).toHaveTextContent('First')

    fireEvent.click(screen.getByTestId('timeline-order-toggle'))

    const chronologicalRows = screen.getAllByTestId('event-timeline-row')
    expect(chronologicalRows[0]).toHaveTextContent('First')
    expect(chronologicalRows[1]).toHaveTextContent('Second')
  })

  it('renders source tags on rows', () => {
    vi.mocked(useEventTimeline).mockReturnValue({
      entries: [
        makeEntry({ id: '1', source: 'ISSUE', description: 'Issue labeled bug' }),
        makeEntry({ id: '2', source: 'WORKFLOW', description: 'Run started' }),
      ],
      isLoading: false,
    })

    render(<EventTimelinePanel issueNumber={42} issueId="issue-42" />)

    expect(screen.getByText('ISSUE')).toBeInTheDocument()
    expect(screen.getByText('WORKFLOW')).toBeInTheDocument()
  })

  it('renders sticky day separators when feed spans days', () => {
    vi.mocked(useEventTimeline).mockReturnValue({
      entries: [
        makeEntry({ id: '1', time: '2026-06-17T10:00:00.000Z', description: 'Earlier day' }),
        makeEntry({ id: '2', time: '2026-06-18T10:00:00.000Z', description: 'Later day' }),
      ],
      isLoading: false,
    })

    render(<EventTimelinePanel issueNumber={42} issueId="issue-42" />)

    const separators = screen.getAllByText(/Jun (17|18), 2026/)
    expect(separators.length).toBeGreaterThanOrEqual(2)
  })

  it('expands failure detail inline', () => {
    vi.mocked(useEventTimeline).mockReturnValue({
      entries: [
        makeEntry({ id: '1', category: 'failure', attention: true, description: 'Run failed', detail: 'compile error' }),
      ],
      isLoading: false,
    })

    render(<EventTimelinePanel issueNumber={42} issueId="issue-42" />)

    expect(screen.queryByTestId('event-detail')).not.toBeInTheDocument()
    fireEvent.click(screen.getByTestId('event-detail-toggle'))
    expect(screen.getByTestId('event-detail')).toHaveTextContent('compile error')
  })

  it('expands attention-required detail inline', () => {
    vi.mocked(useEventTimeline).mockReturnValue({
      entries: [
        makeEntry({ id: '1', category: 'approval', attention: true, description: 'Approval requested', detail: 'needs review' }),
      ],
      isLoading: false,
    })

    render(<EventTimelinePanel issueNumber={42} issueId="issue-42" />)

    expect(screen.queryByTestId('event-detail')).not.toBeInTheDocument()
    fireEvent.click(screen.getByTestId('event-detail-toggle'))
    expect(screen.getByTestId('event-detail')).toHaveTextContent('needs review')
  })
})

void useEventTimeline
