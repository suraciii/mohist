import { describe, expect, it } from 'vitest'
import { __testing__ } from './LiveTaskProvider'
import { dispatchTimelineEvent, onTimelineEvent, type TimelineLiveEvent } from '../../entities/issue/model/timeline-events'

describe('LiveTaskProvider transcript routing', () => {
  it('unwraps transcript envelopes with runtime metadata and payload', () => {
    const envelope = {
      type: 'message.delta',
      sessionId: 'session-1',
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
    })
  })

  it('normalizes server transcript metadata into session-scoped detail fields', () => {
    const unwrapped = __testing__.unwrapTranscriptEnvelope({
      type: 'reasoning.delta',
      sessionId: 'session-84',
      agentSessionId: 'acp-84',
      payload: { text: 'thinking' },
    })

    expect(unwrapped?.detail).toMatchObject({
      acpSessionId: 'acp-84',
      coderSessionId: 'session-84',
      text: 'thinking',
    })
  })
})

describe('LiveTaskProvider timeline forwarding', () => {
  it('builds a TimelineLiveEvent from the CloudEvents envelope (issueNumber, time, eventId from rawData)', () => {
    const envelope = {
      id: 'evt-abc-123',
      type: 'com.mohist.workflow.run.started',
      source: '/mohist/test',
      specVersion: '1.0',
      time: '2026-06-18T00:00:00.000Z',
      payload: { issueNumber: 42, issueId: 'iss-1' },
    }

    const event = __testing__.buildTimelineLiveEvent(
      'com.mohist.workflow.run.started',
      envelope,
      { issueNumber: 42, issueId: 'iss-1' },
    )

    expect(event.issueNumber).toBe(42)
    expect(event.issueId).toBe('iss-1')
    expect(event.type).toBe('com.mohist.workflow.run.started')
    expect(event.time).toBe('2026-06-18T00:00:00.000Z')
    expect(event.eventId).toBe('evt-abc-123')
    expect(event.payload).toEqual({ issueNumber: 42, issueId: 'iss-1' })
  })

  it('does not use CloudEvents id as issueId when payload omits issueId', () => {
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
    expect(event.issueId).toBeNull()
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
})
