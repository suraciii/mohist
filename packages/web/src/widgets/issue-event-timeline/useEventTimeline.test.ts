import { beforeEach, describe, expect, it, vi } from 'vitest'
import { renderHook, act } from '@testing-library/react'
import { useEventTimeline } from './useEventTimeline'
import type { TimelineLiveEvent } from '../../entities/issue/model/timeline-events'
import type { StoredCloudEventDto } from '../../entities/issue/model/types'

const handlers = new Set<(event: TimelineLiveEvent) => void>()

vi.mock('../../entities/issue', () => ({
  useIssueEvents: vi.fn(),
}))

vi.mock('../../entities/issue/model/timeline-events', () => ({
  dispatchTimelineEvent: vi.fn(),
  onTimelineEvent: vi.fn((handler: (event: TimelineLiveEvent) => void) => {
    handlers.add(handler)
    return () => handlers.delete(handler)
  }),
}))

import { useIssueEvents } from '../../entities/issue'
import { onTimelineEvent } from '../../entities/issue/model/timeline-events'

function dispatch(event: TimelineLiveEvent) {
  handlers.forEach((handler) => handler(event))
}

function makeHistoryEvent(overrides: Partial<StoredCloudEventDto> = {}): StoredCloudEventDto {
  return {
    id: 1,
    eventId: 'evt-history-1',
    source: 'workflow',
    type: 'com.mohist.workflow.run.started',
    specVersion: '1.0',
    subject: null,
    time: '2026-06-18T10:00:00.000Z',
    dataContentType: 'application/json',
    data: {},
    extensions: {},
    ...overrides,
  }
}

function makeLiveEvent(overrides: Partial<TimelineLiveEvent> = {}): TimelineLiveEvent {
  return {
    issueNumber: 42,
    issueId: 'issue-42',
    type: 'com.mohist.workflow.stage.started',
    time: '2026-06-18T10:01:00.000Z',
    eventId: 'evt-live-1',
    payload: { issueNumber: 42, from: 'plan', to: 'build' },
    ...overrides,
  }
}

beforeEach(() => {
  handlers.clear()
  vi.mocked(useIssueEvents).mockReturnValue({ data: undefined, isLoading: false } as ReturnType<typeof useIssueEvents>)
})

describe('useEventTimeline', () => {
  it('returns history entries on mount', () => {
    vi.mocked(useIssueEvents).mockReturnValue({
      data: [makeHistoryEvent({ eventId: 'h1', type: 'com.mohist.issue.created', time: '2026-06-18T09:00:00.000Z' })],
      isLoading: false,
    } as ReturnType<typeof useIssueEvents>)

    const { result } = renderHook(() => useEventTimeline(42, 'issue-42'))

    expect(result.current.entries).toHaveLength(1)
    expect(result.current.entries[0].id).toBe('h1')
    expect(result.current.entries[0].isLive).toBe(false)
  })

  it('appends live events for the current issue', () => {
    const { result } = renderHook(() => useEventTimeline(42, 'issue-42'))

    act(() => {
      dispatch(makeLiveEvent({ issueNumber: 42, eventId: 'l1' }))
    })

    expect(result.current.entries).toHaveLength(1)
    expect(result.current.entries[0].id).toBe('l1')
    expect(result.current.entries[0].isLive).toBe(true)
  })

  it('ignores live events for a different issue', () => {
    const { result } = renderHook(() => useEventTimeline(42, 'issue-42'))

    act(() => {
      dispatch(makeLiveEvent({ issueNumber: 99, issueId: 'issue-99', eventId: 'l-other', payload: { issueNumber: 99 } }))
    })

    expect(result.current.entries).toHaveLength(0)
  })

  it('matches live events by issueId when issueNumber differs', () => {
    const { result } = renderHook(() => useEventTimeline(42, 'issue-42'))

    act(() => {
      dispatch(makeLiveEvent({ issueNumber: null, issueId: 'issue-42', eventId: 'l-by-id' }))
    })

    expect(result.current.entries).toHaveLength(1)
    expect(result.current.entries[0].id).toBe('l-by-id')
  })

  it('deduplicates live events against history by eventId', () => {
    vi.mocked(useIssueEvents).mockReturnValue({
      data: [makeHistoryEvent({ eventId: 'shared-1', type: 'com.mohist.workflow.run.started' })],
      isLoading: false,
    } as ReturnType<typeof useIssueEvents>)

    const { result } = renderHook(() => useEventTimeline(42, 'issue-42'))

    act(() => {
      dispatch(makeLiveEvent({ eventId: 'shared-1', issueNumber: 42 }))
    })

    expect(result.current.entries).toHaveLength(1)
  })

  it('orders entries chronologically by time', () => {
    vi.mocked(useIssueEvents).mockReturnValue({
      data: [
        makeHistoryEvent({ eventId: 'h1', time: '2026-06-18T12:00:00.000Z' }),
        makeHistoryEvent({ eventId: 'h2', time: '2026-06-18T10:00:00.000Z' }),
      ],
      isLoading: false,
    } as ReturnType<typeof useIssueEvents>)

    const { result } = renderHook(() => useEventTimeline(42, 'issue-42'))

    expect(result.current.entries.map((e) => e.id)).toEqual(['h2', 'h1'])
  })

  it('caps live events at 500 entries', () => {
    const { result } = renderHook(() => useEventTimeline(42, 'issue-42'))

    act(() => {
      for (let i = 0; i < 550; i++) {
        dispatch(makeLiveEvent({ eventId: `l-${i}`, issueNumber: 42, time: `2026-06-18T10:00:00.00${i}Z` }))
      }
    })

    expect(result.current.entries).toHaveLength(500)
  })

  it('resets live events when issueNumber changes', () => {
    const { result, rerender } = renderHook(
      ({ number }: { number: number }) => useEventTimeline(number, 'issue-42'),
      { initialProps: { number: 42 } },
    )

    act(() => {
      dispatch(makeLiveEvent({ issueNumber: 42, eventId: 'l1' }))
    })

    expect(result.current.entries).toHaveLength(1)

    rerender({ number: 43 })

    expect(result.current.entries).toHaveLength(0)
  })

  it('passes loading state through', () => {
    vi.mocked(useIssueEvents).mockReturnValue({
      data: undefined,
      isLoading: true,
    } as ReturnType<typeof useIssueEvents>)

    const { result } = renderHook(() => useEventTimeline(42, 'issue-42'))

    expect(result.current.isLoading).toBe(true)
  })
})

void onTimelineEvent
