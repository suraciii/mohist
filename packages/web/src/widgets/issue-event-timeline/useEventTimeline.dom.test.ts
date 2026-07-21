import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, renderHook } from '@testing-library/react'
import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query'
import { createElement, type ReactNode } from 'react'
import { ProjectProvider } from '../../entities/project'
import { dispatchTimelineEvent, type TimelineLiveEvent } from '../../entities/issue/model/timeline-events'
import type { StoredCloudEventDto, WorkflowTimeline } from '../../entities/issue/model/types'
import { WorkflowStage } from '../../entities/issue'
import {
  useEventTimeline,
  type EventTimelineHistoryHook,
  type EventTimelineWorkflowHook,
} from './useEventTimeline'

// react-query resolves via notifyManager's scheduled timers; advance the clock
// ourselves under fake timers instead of polling wall-clock time (waitFor's
// default 1000ms is too tight on slow CI — design/testing.md: advance fake
// time, don't poll harder).
async function flush() {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(1000)
  })
}

let historyResponse: StoredCloudEventDto[] | 'never' = []
let requestedIssueNumbers: string[] = []

const historyHook: EventTimelineHistoryHook = (issueNumber, enabled) => useQuery({
  queryKey: ['event-timeline-test-history', issueNumber],
  queryFn: async () => {
    requestedIssueNumbers.push(String(issueNumber))
    if (historyResponse === 'never') return new Promise<StoredCloudEventDto[]>(() => {})
    return historyResponse
  },
  enabled,
})

let workflowResponse: WorkflowTimeline | undefined
const workflowHook: EventTimelineWorkflowHook = () => ({ data: workflowResponse })

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
    type: 'com.mohist.workflow.stage.started',
    time: '2026-06-18T10:01:00.000Z',
    eventId: 'evt-live-1',
    payload: { issueNumber: 42, from: 'plan', to: 'build' },
    ...overrides,
  }
}

function makeWrapper() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  })
  const project = {
    id: 'proj-1',
    name: 'Project 1',
    createdAt: '2026-01-01T00:00:00.000Z',
    updatedAt: '2026-01-01T00:00:00.000Z',
    repositories: [],
  }

  return ({ children }: { children: ReactNode }) => createElement(
    QueryClientProvider,
    { client: queryClient },
    createElement(
      ProjectProvider,
      { initialProjectId: project.id, initialProjects: [project], children },
    ),
  )
}

function renderTimelineHook(enabled: boolean = true) {
  return renderHook(
    ({ number, isEnabled }) => useEventTimeline(number, isEnabled, historyHook, workflowHook),
    {
      initialProps: { number: 42, isEnabled: enabled },
      wrapper: makeWrapper(),
    },
  )
}

beforeEach(() => {
  historyResponse = []
  workflowResponse = undefined
  requestedIssueNumbers = []
  vi.useFakeTimers()
})

afterEach(() => {
  vi.useRealTimers()
})

describe('useEventTimeline', () => {
  it('returns history entries on mount', async () => {
    historyResponse = [
      makeHistoryEvent({ eventId: 'h1', type: 'com.mohist.issue.created', time: '2026-06-18T09:00:00.000Z' }),
    ]

    const { result } = renderTimelineHook()

    await flush()
    expect(result.current.entries).toHaveLength(1)
    expect(result.current.entries[0].id).toBe('h1')
    expect(result.current.entries[0].isLive).toBe(false)
  })

  it('appends live events for the current issue', () => {
    const { result } = renderTimelineHook()

    act(() => {
      dispatchTimelineEvent(makeLiveEvent({ issueNumber: 42, eventId: 'l1' }))
    })

    expect(result.current.entries).toHaveLength(1)
    expect(result.current.entries[0].id).toBe('l1')
    expect(result.current.entries[0].isLive).toBe(true)
  })

  it('ignores live events for a different issue', () => {
    const { result } = renderTimelineHook()

    act(() => {
      dispatchTimelineEvent(makeLiveEvent({
        issueNumber: 99,
        eventId: 'l-other',
        payload: { issueNumber: 99, },
      }))
    })

    expect(result.current.entries).toHaveLength(0)
  })

  it('ignores a live event without canonical issue context', () => {
    const { result } = renderTimelineHook()

    act(() => {
      dispatchTimelineEvent(makeLiveEvent({ issueNumber: null, eventId: 'l-without-context' }))
    })

    expect(result.current.entries).toHaveLength(0)
  })

  it('deduplicates live events against history by eventId', async () => {
    historyResponse = [makeHistoryEvent({ eventId: 'shared-1', type: 'com.mohist.workflow.run.started' })]
    const { result } = renderTimelineHook()

    await flush()
    expect(result.current.entries).toHaveLength(1)

    act(() => {
      dispatchTimelineEvent(makeLiveEvent({ eventId: 'shared-1', issueNumber: 42, }))
    })

    expect(result.current.entries).toHaveLength(1)
  })

  it('orders entries chronologically by time', async () => {
    historyResponse = [
      makeHistoryEvent({ eventId: 'h1', time: '2026-06-18T12:00:00.000Z' }),
      makeHistoryEvent({ eventId: 'h2', time: '2026-06-18T10:00:00.000Z' }),
    ]

    const { result } = renderTimelineHook()

    await flush()
    expect(result.current.entries.map((event) => event.id)).toEqual(['h2', 'h1'])
  })

  it('caps live events at 500 entries', () => {
    const { result } = renderTimelineHook()

    act(() => {
      for (let i = 0; i < 550; i++) {
        dispatchTimelineEvent(makeLiveEvent({
          eventId: `l-${i}`,
          issueNumber: 42,
          time: new Date(Date.UTC(2026, 5, 18, 10, 0, 0, i)).toISOString(),
        }))
      }
    })

    expect(result.current.entries).toHaveLength(500)
  })

  it('resets live events when issueNumber changes', () => {
    const { result, rerender } = renderTimelineHook()

    act(() => {
      dispatchTimelineEvent(makeLiveEvent({ issueNumber: 42, eventId: 'l1' }))
    })

    expect(result.current.entries).toHaveLength(1)

    rerender({ number: 43, isEnabled: true })

    expect(result.current.entries).toHaveLength(0)
  })

  it('passes loading state through', () => {
    historyResponse = 'never'

    const { result } = renderTimelineHook()

    expect(result.current.isLoading).toBe(true)
  })

  it('loads history only when enabled', async () => {
    const { rerender } = renderTimelineHook(false)

    expect(requestedIssueNumbers).toEqual([])

    rerender({ number: 42, isEnabled: true })

    await flush()
    expect(requestedIssueNumbers).toEqual(['42'])
  })

  it('does not subscribe to live events when enabled is false', () => {
    const { result } = renderTimelineHook(false)

    act(() => {
      dispatchTimelineEvent(makeLiveEvent({ issueNumber: 42, eventId: 'lazy-l1' }))
    })

    expect(result.current.entries).toEqual([])
  })

  it('subscribes to live events only after enabled flips to true', () => {
    const { result, rerender } = renderTimelineHook(false)

    act(() => {
      dispatchTimelineEvent(makeLiveEvent({ issueNumber: 42, eventId: 'before-open' }))
    })
    expect(result.current.entries).toEqual([])

    rerender({ number: 42, isEnabled: true })

    act(() => {
      dispatchTimelineEvent(makeLiveEvent({ issueNumber: 42, eventId: 'after-open' }))
    })
    expect(result.current.entries.map((entry) => entry.id)).toEqual(['after-open'])
  })

  it('formats historical and live task events with the same stage-aware title resolver', async () => {
    historyResponse = [makeHistoryEvent({
      eventId: 'history-task',
      type: 'com.mohist.workflow.task.completed',
      data: { stage: 'build', taskId: 'T-004' },
    })]
    const titleWorkflowHook: EventTimelineWorkflowHook = () => ({
      data: {
        workflowRunId: 'wr-1',
        status: 'running',
        currentStage: 'build',
        pendingWork: null,
        availableActions: [],
        stages: [
          { stage: WorkflowStage.Plan, status: 'completed', order: 0, startedAt: null, completedAt: null, durationMs: null, checks: [], approval: null, tasks: [{ id: 'T-004', title: 'Plan title', uses: null, status: 'completed', startedAt: null, completedAt: null, durationMs: null, attempts: 1, message: null }] },
          { stage: WorkflowStage.Build, status: 'running', order: 1, startedAt: null, completedAt: null, durationMs: null, checks: [], approval: null, tasks: [{ id: 'T-004', title: 'Build title', uses: null, status: 'running', startedAt: null, completedAt: null, durationMs: null, attempts: 1, message: null }] },
        ],
      },
    })
    const { result } = renderHook(
      () => useEventTimeline(42, true, historyHook, titleWorkflowHook),
      { wrapper: makeWrapper() },
    )

    await flush()
    act(() => {
      dispatchTimelineEvent(makeLiveEvent({
        eventId: 'live-task',
        type: 'com.mohist.workflow.task.completed',
        payload: { stage: 'build', taskId: 'T-004' },
      }))
    })

    expect(result.current.entries.map((entry) => entry.description))
      .toEqual(['Build title completed', 'Build title completed'])
  })

  it('resolves a retained live task after workflow titles load', () => {
    const { result, rerender } = renderTimelineHook()
    act(() => {
      dispatchTimelineEvent(makeLiveEvent({
        type: 'com.mohist.workflow.task.started',
        payload: { stage: 'build', taskId: 'T-004' },
      }))
    })
    expect(result.current.entries[0].description).toBe('T-004 started')

    workflowResponse = {
      workflowRunId: 'wr-1',
      status: 'running',
      currentStage: 'build',
      pendingWork: null,
      availableActions: [],
      stages: [{
        stage: WorkflowStage.Build,
        status: 'running',
        order: 1,
        startedAt: null,
        completedAt: null,
        durationMs: null,
        checks: [],
        approval: null,
        tasks: [{ id: 'T-004', title: 'Loaded title', uses: null, status: 'running', startedAt: null, completedAt: null, durationMs: null, attempts: 1, message: null }],
      }],
    }
    rerender({ number: 42, isEnabled: true })

    expect(result.current.entries[0].description).toBe('Loaded title started')
  })
})
