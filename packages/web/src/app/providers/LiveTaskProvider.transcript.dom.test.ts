import { createElement } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, render } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ProjectProvider } from '../../entities/project'
import { REVERSE_DNS_EVENT_TYPES } from '../../shared/lib/canonical-event-types'
import { LiveTaskProvider, __testing__, type EventsConnectionHook } from './LiveTaskProvider'
import { dispatchTimelineEvent, onTimelineEvent, type TimelineLiveEvent } from '../../entities/issue/model/timeline-events'
import { TEST_PROJECT } from './_liveTaskProviderTestUtils'

let eventsConnectionCalls: Parameters<EventsConnectionHook>[] = []
const eventsConnectionHook: EventsConnectionHook = (...args) => {
  eventsConnectionCalls.push(args)
  return { status: 'disconnected', connection: null, reconnectVersion: 0 }
}
beforeEach(() => {
  vi.clearAllMocks()
  eventsConnectionCalls = []
})

describe('LiveTaskProvider transcript routing', () => {
  it('unwraps transcript envelopes with runtime metadata and payload', () => {
    const envelope = {
      type: 'message.delta',
      sessionId: 'session-1',
      agentSessionId: 'runtime-1',
      sequence: 12,
      createdAt: '2026-06-12T00:00:00.000Z',
      payload: { text: 'persisted segment' },
    }

    const unwrapped = __testing__.unwrapTranscriptEnvelope(envelope)

    expect(unwrapped?.eventName).toBe('message.delta')
    expect(unwrapped?.payload).toEqual({ text: 'persisted segment' })
    expect(unwrapped?.detail).toMatchObject({
      type: 'message.delta',
      text: 'persisted segment',
      payload: { text: 'persisted segment' },
      sequence: 12,
      runtimeSessionId: 'runtime-1',
    })
  })

  it('does not substitute a logical session id for a missing runtime binding', () => {
    const unwrapped = __testing__.unwrapTranscriptEnvelope({
      type: 'message.delta',
      sessionId: 'session-1',
      payload: { text: 'persisted segment' },
    })

    expect(unwrapped?.detail).not.toHaveProperty('runtimeSessionId')
  })

  it('normalizes server transcript metadata into session-scoped detail fields', () => {
    const unwrapped = __testing__.unwrapTranscriptEnvelope({
      type: 'reasoning.delta',
      sessionId: 'session-84',
      runtimeSessionId: 'runtime-84',
      runtime: 'opencode',
      payload: { text: 'thinking' },
    })

    expect(unwrapped?.detail).toMatchObject({
      runtimeSessionId: 'runtime-84',
      sessionId: 'session-84',
      runtime: 'opencode',
      text: 'thinking',
    })
  })

  it('prefers the canonical runtime session field when the envelope provides it', () => {
    const unwrapped = __testing__.unwrapTranscriptEnvelope({
      type: 'message.delta',
      sessionId: 'session-84',
      runtimeSessionId: 'runtime-84',
      runtime: 'opencode',
      payload: { text: 'working' },
    })

    expect(unwrapped?.detail).toMatchObject({
      runtimeSessionId: 'runtime-84',
      sessionId: 'session-84',
      runtime: 'opencode',
      text: 'working',
    })
    expect(unwrapped?.detail).not.toHaveProperty('acpSessionId')
    expect(unwrapped?.detail).not.toHaveProperty('coderSessionId')
  })
})

describe('LiveTaskProvider timeline forwarding', () => {
  it('routes a TimelineLiveEvent by the canonical envelope issue while preserving payload', () => {
    const envelope = {
      id: 'evt-abc-123',
      type: 'com.mohist.workflow.run.started',
      source: '/mohist/test',
      specVersion: '1.0',
      time: '2026-06-18T00:00:00.000Z',
      payload: { issueNumber: 42 },
      extensions: { issue: '99' },
    }
    const parsed = __testing__.unwrapEnvelope(envelope)

    const event = __testing__.buildTimelineLiveEvent(
      'com.mohist.workflow.run.started',
      envelope,
      parsed,
    )

    expect(event.issueNumber).toBe(99)
    expect(event.type).toBe('com.mohist.workflow.run.started')
    expect(event.time).toBe('2026-06-18T00:00:00.000Z')
    expect(event.eventId).toBe('evt-abc-123')
    expect(event.payload).toEqual({ issueNumber: 42 })
  })

  it('takes CloudEvents issue lineage without changing the timeline payload', () => {
    const envelope = {
      id: 'evt-issue-lineage',
      type: 'com.mohist.workflow.run.started',
      source: '/mohist/test',
      specVersion: '1.0',
      time: '2026-06-18T00:00:00.000Z',
      payload: { stage: 'build' },
      extensions: { issue: '42' },
    }
    const parsed = __testing__.unwrapEnvelope(envelope)

    const event = __testing__.buildTimelineLiveEvent(
      'com.mohist.workflow.run.started',
      envelope,
      parsed,
    )

    expect(event.issueNumber).toBe(42)
    expect(event.payload).toEqual({ stage: 'build' })
  })

  it('does not use the CloudEvents event id as issue context', () => {
    const envelope = {
      id: 'evt-abc-123',
      type: 'com.mohist.workflow.run.started',
      source: '/mohist/test',
      specVersion: '1.0',
      time: '2026-06-18T00:00:00.000Z',
      payload: { issueNumber: 42 },
    }

    const event = __testing__.buildTimelineLiveEvent(
      'com.mohist.workflow.run.started',
      envelope,
      { issueNumber: 42 },
    )

    expect(event.issueNumber).toBe(42)
    expect(event.eventId).toBe('evt-abc-123')
  })

  it('falls back to payload time when envelope omits the CloudEvents time', () => {
    const event = __testing__.buildTimelineLiveEvent(
      'merge_completed',
      { payload: { issueNumber: 7, time: '2026-06-18T01:00:00.000Z' } },
      { issueNumber: 7, time: '2026-06-18T01:00:00.000Z' },
    )

    expect(event.issueNumber).toBe(7)
    expect(event.time).toBe('2026-06-18T01:00:00.000Z')
  })

  it('returns null issueNumber and null time when both envelope and payload omit them', () => {
    const event = __testing__.buildTimelineLiveEvent(
      'unknown_event',
      { payload: {} },
      {},
    )

    expect(event.issueNumber).toBeNull()
    expect(event.time).toBeNull()
    expect(event.eventId).toBeNull()
    expect(event.payload).toEqual({})
  })

  it('dispatchTimelineEvent delivers the built event to onTimelineEvent subscribers', () => {
    const received: TimelineLiveEvent[] = []
    const off = onTimelineEvent((e) => received.push(e))

    const event = __testing__.buildTimelineLiveEvent(
      'rebase_conflict',
      { id: 'rc-1', payload: { issueNumber: 99 } },
      { issueNumber: 99 },
    )
    dispatchTimelineEvent(event)

    expect(received).toHaveLength(1)
    expect(received[0].type).toBe('rebase_conflict')
    expect(received[0].issueNumber).toBe(99)
    expect(received[0].eventId).toBe('rc-1')

    off()
  })

  it('does not suppress or replace existing invalidation/toast behavior on the forward path', () => {
    const observed: string[] = []
    const off = onTimelineEvent((e) => observed.push(`forward:${e.type}`))

    const envelope = {
      id: 'evt-1',
      type: 'merge_completed',
      source: '/mohist/test',
      specVersion: '1.0',
      time: '2026-06-18T00:00:00.000Z',
      payload: { issueNumber: 42, issueId: 'iss-1' },
    }

    const event = __testing__.buildTimelineLiveEvent(
      'merge_completed',
      envelope,
      envelope.payload as Record<string, unknown>,
    )
    dispatchTimelineEvent(event)

    expect(observed).toEqual(['forward:merge_completed'])

    off()
  })

  it('invalidates approval-wait metrics when a stage approval is resolved live', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')

    render(
      createElement(
        QueryClientProvider,
        { client: queryClient },
        createElement(
          ProjectProvider,
          {
            initialProjectId: TEST_PROJECT.id,
            initialProjects: [TEST_PROJECT],
            children: createElement(
              LiveTaskProvider,
              {
                children: createElement('div', null, 'child'),
                eventsConnectionHook,
              },
            ),
          },
        ),
      ),
    )

    const handleEvent = eventsConnectionCalls[0][1]
    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.StageApprovalResolved, {
        issueId: 'issue-1',
        issueNumber: 42,
      })
    })

    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['issues'] })
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['agent-activity'] })
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['issues', 'metrics', 'approval-wait'] })
  })
})
