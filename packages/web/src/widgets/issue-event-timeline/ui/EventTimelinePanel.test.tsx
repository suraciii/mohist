import { beforeEach, describe, expect, it, vi } from 'vitest'
import { render, screen, fireEvent, within } from '@testing-library/react'
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

  it('forwards enabled=false to useEventTimeline when the panel is mounted in lazy/dialog mode', () => {
    vi.mocked(useEventTimeline).mockClear()
    vi.mocked(useEventTimeline).mockReturnValue({ entries: [], isLoading: false })

    render(<EventTimelinePanel issueNumber={42} issueId="issue-42" enabled={false} />)

    expect(useEventTimeline).toHaveBeenLastCalledWith(42, 'issue-42', false)
  })

  it('forwards enabled=true to useEventTimeline by default', () => {
    vi.mocked(useEventTimeline).mockClear()
    vi.mocked(useEventTimeline).mockReturnValue({ entries: [], isLoading: false })

    render(<EventTimelinePanel issueNumber={42} issueId="issue-42" />)

    expect(useEventTimeline).toHaveBeenLastCalledWith(42, 'issue-42', true)
  })

  describe('neutral visual treatment (issue-180 T-002)', () => {
    const SATURATED_BG_CLASSES = [
      'bg-blue-50',
      'bg-amber-50',
      'bg-purple-50',
      'bg-green-50',
      'bg-red-50',
      'bg-gray-100',
    ]
    const SATURATED_TEXT_CLASSES = [
      'text-blue-700',
      'text-amber-700',
      'text-purple-700',
      'text-green-700',
      'text-red-700',
    ]

    it('renders regular categories in neutral monochrome with no category badge and no full-row tint', () => {
      vi.mocked(useEventTimeline).mockReturnValue({
        entries: [
          makeEntry({ id: 'wf', category: 'workflow', description: 'Run started' }),
          makeEntry({ id: 'ap', category: 'approval', description: 'Approved' }),
          makeEntry({ id: 'in', category: 'integration', description: 'Synced' }),
          makeEntry({ id: 'ok', category: 'success', description: 'Published' }),
          makeEntry({ id: 'md', category: 'metadata', description: 'Edited' }),
        ],
        isLoading: false,
      })

      render(<EventTimelinePanel issueNumber={42} issueId="issue-42" />)

      const rows = screen.getAllByTestId('event-timeline-row')
      expect(rows).toHaveLength(5)

      const regularLabels = ['Workflow', 'Approval', 'Integration', 'Success', 'Metadata']
      for (const row of rows) {
        for (const cls of SATURATED_BG_CLASSES) {
          expect(row.className).not.toContain(cls)
        }
        for (const cls of SATURATED_TEXT_CLASSES) {
          expect(row.className).not.toContain(cls)
        }
        for (const label of regularLabels) {
          expect(within(row).queryByText(label)).not.toBeInTheDocument()
        }
      }
    })

    it('renders failure events with a colored marker accent but no full-row tinted background', () => {
      vi.mocked(useEventTimeline).mockReturnValue({
        entries: [
          makeEntry({
            id: 'fail',
            category: 'failure',
            attention: true,
            description: 'Run failed',
          }),
        ],
        isLoading: false,
      })

      render(<EventTimelinePanel issueNumber={42} issueId="issue-42" />)

      const row = screen.getByTestId('event-timeline-row')
      expect(row.className).not.toContain('bg-red-50')
      expect(row.className).not.toContain('bg-red-50/80')

      const marker = row.querySelector('span.bg-red-500')
      expect(marker).not.toBeNull()
    })

    it('renders attention-required events with a colored marker accent but no full-row tinted background', () => {
      vi.mocked(useEventTimeline).mockReturnValue({
        entries: [
          makeEntry({
            id: 'approval-attention',
            category: 'approval',
            attention: true,
            description: 'Approval requested',
          }),
        ],
        isLoading: false,
      })

      render(<EventTimelinePanel issueNumber={42} issueId="issue-42" />)

      const row = screen.getByTestId('event-timeline-row')
      expect(row.getAttribute('data-attention')).toBe('true')
      expect(row.className).not.toContain('bg-amber-50')
      expect(row.className).not.toContain('bg-amber-50/60')

      const marker = row.querySelector('span.bg-amber-500')
      expect(marker).not.toBeNull()
    })

    it('uses a neutral light background for expanded failure detail (no bg-gray-900)', () => {
      vi.mocked(useEventTimeline).mockReturnValue({
        entries: [
          makeEntry({
            id: 'fail-detail',
            category: 'failure',
            attention: true,
            description: 'Run failed',
            detail: 'compile error: foo.ts',
          }),
        ],
        isLoading: false,
      })

      render(<EventTimelinePanel issueNumber={42} issueId="issue-42" />)

      fireEvent.click(screen.getByTestId('event-detail-toggle'))

      const detail = screen.getByTestId('event-detail')
      expect(detail.className).not.toContain('bg-gray-900')
      expect(detail.className).toContain('bg-gray-50')
      expect(detail.textContent).toContain('compile error: foo.ts')
    })

    it('keeps expandable event detail controls large enough for mobile touch targets', () => {
      vi.mocked(useEventTimeline).mockReturnValue({
        entries: [
          makeEntry({
            id: 'fail-touch-target',
            category: 'failure',
            attention: true,
            description: 'Run failed',
            detail: 'compile error: foo.ts',
          }),
        ],
        isLoading: false,
      })

      Object.defineProperty(window, 'innerWidth', { configurable: true, value: 375 })
      window.dispatchEvent(new Event('resize'))

      render(<EventTimelinePanel issueNumber={42} issueId="issue-42" />)

      const toggle = screen.getByTestId('event-detail-toggle')
      expect(toggle).toHaveClass('min-h-11')
      expect(toggle).toHaveClass('min-w-11')
    })

    it('does not apply slide-in-from-top entrance animation to live events', () => {
      vi.mocked(useEventTimeline).mockReturnValue({
        entries: [
          makeEntry({
            id: 'live',
            description: 'Live tick',
            isLive: true,
          }),
        ],
        isLoading: false,
      })

      render(<EventTimelinePanel issueNumber={42} issueId="issue-42" />)

      const liveRow = screen.getByTestId('event-timeline-row')
      expect(liveRow.getAttribute('data-live')).toBe('true')
      expect(liveRow.className).not.toContain('slide-in-from-top')
      expect(liveRow.className).not.toContain('animate-in')
    })

    it('keeps category filter chips usable to narrow the list by selected category', () => {
      vi.mocked(useEventTimeline).mockReturnValue({
        entries: [
          makeEntry({ id: 'wf', category: 'workflow', description: 'Run started' }),
          makeEntry({ id: 'ap', category: 'approval', description: 'Approved' }),
          makeEntry({
            id: 'fail',
            category: 'failure',
            attention: true,
            description: 'Run failed',
          }),
        ],
        isLoading: false,
      })

      render(<EventTimelinePanel issueNumber={42} issueId="issue-42" />)

      expect(screen.getAllByTestId('event-timeline-row')).toHaveLength(3)

      fireEvent.click(screen.getByTestId('category-filter-workflow'))
      fireEvent.click(screen.getByTestId('category-filter-approval'))

      const narrowedRows = screen.getAllByTestId('event-timeline-row')
      expect(narrowedRows).toHaveLength(1)
      expect(narrowedRows[0].getAttribute('data-category')).toBe('failure')

      fireEvent.click(screen.getByTestId('timeline-clear-filters'))

      expect(screen.getAllByTestId('event-timeline-row')).toHaveLength(3)
    })
  })
})

void useEventTimeline
