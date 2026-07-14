import { beforeEach, describe, expect, it } from 'vitest'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { useEffect, type ComponentProps } from 'react'
import { render } from '../../../../tests/test-utils'
import { EventTimelinePanel, EventTimelinePanelView } from './EventTimelinePanel'
import type { TimelineEntry } from '../model/types'
import type { EventTimelineHistoryHook } from '../useEventTimeline'
import { setScopedValue } from '../../../../tests/support/scoped-property'

let timeline = { entries: [] as TimelineEntry[], isLoading: false }
let requestedIssueNumbers: string[] = []

const historyHook: EventTimelineHistoryHook = (issueNumber, enabled) => {
  useEffect(() => {
    if (enabled) requestedIssueNumbers.push(String(issueNumber))
  }, [issueNumber, enabled])
  return { data: [], isLoading: false }
}

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

function renderTimelineView(
  props: Partial<ComponentProps<typeof EventTimelinePanelView>> = {},
) {
  return render(
    <EventTimelinePanelView
      entries={timeline.entries}
      isLoading={timeline.isLoading}
      {...props}
    />,
  )
}

beforeEach(() => {
  timeline = { entries: [], isLoading: false }
  requestedIssueNumbers = []
})

describe('EventTimelinePanel', () => {
  it('renders the panel and rows', () => {
    timeline = {
      entries: [
        makeEntry({ id: '1', description: 'Run started', category: 'workflow' }),
        makeEntry({ id: '2', description: 'Stage moved from Plan to Build', category: 'workflow' }),
      ],
      isLoading: false,
    }

    renderTimelineView({ workflowStatus: 'running' })

    expect(screen.getByTestId('event-timeline-panel')).toBeInTheDocument()
    expect(screen.getAllByTestId('event-timeline-row')).toHaveLength(2)
    expect(screen.getByText('Run started')).toBeInTheDocument()
    expect(screen.getByText('Stage moved from Plan to Build')).toBeInTheDocument()
  })

  it('shows empty state when no events', () => {
    renderTimelineView()

    expect(screen.getByTestId('timeline-empty-state')).toBeInTheDocument()
    expect(screen.getByText('No activity yet.')).toBeInTheDocument()
  })

  it('shows pulsing live badge when workflow is running', () => {
    timeline = {
      entries: [makeEntry()],
      isLoading: false,
    }

    renderTimelineView({ workflowStatus: 'running' })

    expect(screen.getByTestId('timeline-live-badge')).toBeInTheDocument()
  })

  it('shows de-emphasized live badge when workflow is inactive', () => {
    timeline = {
      entries: [makeEntry()],
      isLoading: false,
    }

    renderTimelineView({ workflowStatus: 'completed' })

    expect(screen.getByTestId('timeline-inactive-badge')).toBeInTheDocument()
  })

  it('filters events by category', () => {
    timeline = {
      entries: [
        makeEntry({ id: '1', category: 'workflow', description: 'Run started' }),
        makeEntry({ id: '2', category: 'failure', description: 'Run failed', attention: true }),
      ],
      isLoading: false,
    }

    renderTimelineView()

    expect(screen.getAllByTestId('event-timeline-row')).toHaveLength(2)

    fireEvent.click(screen.getByTestId('category-filter-workflow'))

    expect(screen.getAllByTestId('event-timeline-row')).toHaveLength(1)
    expect(screen.getByText('Run failed')).toBeInTheDocument()
    expect(screen.queryByText('Run started')).not.toBeInTheDocument()

    fireEvent.click(screen.getByTestId('timeline-clear-filters'))

    expect(screen.getAllByTestId('event-timeline-row')).toHaveLength(2)
  })

  it('shows category counts on chips', () => {
    timeline = {
      entries: [
        makeEntry({ id: '1', category: 'workflow' }),
        makeEntry({ id: '2', category: 'workflow' }),
        makeEntry({ id: '3', category: 'failure', attention: true }),
      ],
      isLoading: false,
    }

    renderTimelineView()

    expect(screen.getByTestId('category-filter-workflow')).toHaveTextContent('2')
    expect(screen.getByTestId('category-filter-failure')).toHaveTextContent('1')
  })

  it('toggles order between newest-first and chronological', () => {
    timeline = {
      entries: [
        makeEntry({ id: '1', time: '2026-06-18T10:00:00.000Z', description: 'First' }),
        makeEntry({ id: '2', time: '2026-06-18T11:00:00.000Z', description: 'Second' }),
      ],
      isLoading: false,
    }

    renderTimelineView()

    const rows = screen.getAllByTestId('event-timeline-row')
    expect(rows[0]).toHaveTextContent('Second')
    expect(rows[1]).toHaveTextContent('First')

    fireEvent.click(screen.getByTestId('timeline-order-toggle'))

    const chronologicalRows = screen.getAllByTestId('event-timeline-row')
    expect(chronologicalRows[0]).toHaveTextContent('First')
    expect(chronologicalRows[1]).toHaveTextContent('Second')
  })

  it('renders source tags on rows', () => {
    timeline = {
      entries: [
        makeEntry({ id: '1', source: 'ISSUE', description: 'Issue labeled bug' }),
        makeEntry({ id: '2', source: 'WORKFLOW', description: 'Run started' }),
      ],
      isLoading: false,
    }

    renderTimelineView()

    expect(screen.getByText('ISSUE')).toBeInTheDocument()
    expect(screen.getByText('WORKFLOW')).toBeInTheDocument()
  })

  it('renders sticky day separators when feed spans days', () => {
    timeline = {
      entries: [
        makeEntry({ id: '1', time: '2026-06-17T10:00:00.000Z', description: 'Earlier day' }),
        makeEntry({ id: '2', time: '2026-06-18T10:00:00.000Z', description: 'Later day' }),
      ],
      isLoading: false,
    }

    renderTimelineView()

    const separators = screen.getAllByText(/Jun (17|18), 2026/)
    expect(separators.length).toBeGreaterThanOrEqual(2)
  })

  it('expands failure detail inline', () => {
    timeline = {
      entries: [
        makeEntry({ id: '1', category: 'failure', attention: true, description: 'Run failed', detail: 'compile error' }),
      ],
      isLoading: false,
    }

    renderTimelineView()

    expect(screen.queryByTestId('event-detail')).not.toBeInTheDocument()
    fireEvent.click(screen.getByTestId('event-detail-toggle'))
    expect(screen.getByTestId('event-detail')).toHaveTextContent('compile error')
  })

  it('expands attention-required detail inline', () => {
    timeline = {
      entries: [
        makeEntry({ id: '1', category: 'approval', attention: true, description: 'Approval requested', detail: 'needs review' }),
      ],
      isLoading: false,
    }

    renderTimelineView()

    expect(screen.queryByTestId('event-detail')).not.toBeInTheDocument()
    fireEvent.click(screen.getByTestId('event-detail-toggle'))
    expect(screen.getByTestId('event-detail')).toHaveTextContent('needs review')
  })

  it('does not load history when the panel is mounted disabled in lazy/dialog mode', async () => {
    render(
      <EventTimelinePanel
        issueNumber={42}
        issueId="issue-42"
        enabled={false}
        historyHook={historyHook}
      />,
    )

    await waitFor(() => {
      expect(screen.getByTestId('timeline-empty-state')).toBeInTheDocument()
    })
    expect(requestedIssueNumbers).toEqual([])
  })

  it('loads history for the issue by default', async () => {
    render(
      <EventTimelinePanel issueNumber={42} issueId="issue-42" historyHook={historyHook} />,
    )

    await waitFor(() => {
      expect(requestedIssueNumbers).toEqual(['42'])
    })
  })

  describe('neutral visual treatment', () => {
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
      timeline = {
        entries: [
          makeEntry({ id: 'wf', category: 'workflow', description: 'Run started' }),
          makeEntry({ id: 'ap', category: 'approval', description: 'Approved' }),
          makeEntry({ id: 'in', category: 'integration', description: 'Synced' }),
          makeEntry({ id: 'ok', category: 'success', description: 'Published' }),
          makeEntry({ id: 'md', category: 'metadata', description: 'Edited' }),
        ],
        isLoading: false,
      }

      renderTimelineView()

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
      timeline = {
        entries: [
          makeEntry({
            id: 'fail',
            category: 'failure',
            attention: true,
            description: 'Run failed',
          }),
        ],
        isLoading: false,
      }

      renderTimelineView()

      const row = screen.getByTestId('event-timeline-row')
      expect(row.className).not.toContain('bg-danger-subtle')
      expect(row.className).not.toContain('bg-red-50')
      expect(row.className).not.toContain('bg-red-50/80')
      const marker = row.querySelector('span.bg-danger')
      expect(marker).not.toBeNull()
    })

    it('renders attention-required events with a colored marker accent but no full-row tinted background', () => {
      timeline = {
        entries: [
          makeEntry({
            id: 'approval-attention',
            category: 'approval',
            attention: true,
            description: 'Approval requested',
          }),
        ],
        isLoading: false,
      }

      renderTimelineView()

      const row = screen.getByTestId('event-timeline-row')
      expect(row.getAttribute('data-attention')).toBe('true')
      expect(row.className).not.toContain('bg-warning-subtle')
      expect(row.className).not.toContain('bg-amber-50')
      expect(row.className).not.toContain('bg-amber-50/60')
      const marker = row.querySelector('span.bg-warning')
      expect(marker).not.toBeNull()
    })

    it('uses a neutral light background for expanded failure detail (no bg-gray-900)', () => {
      timeline = {
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
      }

      renderTimelineView()

      fireEvent.click(screen.getByTestId('event-detail-toggle'))

      const detail = screen.getByTestId('event-detail')
      expect(detail.className).not.toContain('bg-gray-900')
      expect(detail.className).toContain('bg-muted')
      expect(detail.textContent).toContain('compile error: foo.ts')
    })

    it('keeps expandable event detail controls large enough for mobile touch targets', () => {
      timeline = {
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
      }

      setScopedValue(window, 'innerWidth', 375)
      window.dispatchEvent(new Event('resize'))

      renderTimelineView()

      const toggle = screen.getByTestId('event-detail-toggle')
      expect(toggle).toHaveClass('min-h-11')
      expect(toggle).toHaveClass('min-w-11')
    })

    it('does not apply slide-in-from-top entrance animation to live events', () => {
      timeline = {
        entries: [
          makeEntry({
            id: 'live',
            description: 'Live tick',
            isLive: true,
          }),
        ],
        isLoading: false,
      }

      renderTimelineView()

      const liveRow = screen.getByTestId('event-timeline-row')
      expect(liveRow.getAttribute('data-live')).toBe('true')
      expect(liveRow.className).not.toContain('slide-in-from-top')
      expect(liveRow.className).not.toContain('animate-in')
    })

    it('keeps category filter chips usable to narrow the list by selected category', () => {
      timeline = {
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
      }

      renderTimelineView()

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
