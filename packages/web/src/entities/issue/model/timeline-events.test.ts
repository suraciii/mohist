import { describe, expect, it } from 'vitest'
import { dispatchTimelineEvent, onTimelineEvent, type TimelineLiveEvent } from './timeline-events'

function makeEvent(overrides: Partial<TimelineLiveEvent> = {}): TimelineLiveEvent {
  return {
    issueNumber: 42,
    type: 'com.mohist.workflow.run.started',
    time: '2026-06-18T00:00:00.000Z',
    eventId: 'evt-1',
    payload: { issueNumber: 42, },
    ...overrides,
  }
}

describe('timeline-events pub/sub', () => {
  it('round-trips a TimelineLiveEvent through dispatch and on listener', () => {
    const received: TimelineLiveEvent[] = []
    const off = onTimelineEvent((event) => received.push(event))

    const sent = makeEvent()
    dispatchTimelineEvent(sent)

    expect(received).toEqual([sent])

    off()
  })

  it('forwards events to multiple listeners', () => {
    const a: TimelineLiveEvent[] = []
    const b: TimelineLiveEvent[] = []
    const offA = onTimelineEvent((event) => a.push(event))
    const offB = onTimelineEvent((event) => b.push(event))

    const sent = makeEvent({ type: 'com.mohist.issue.labels-changed' })
    dispatchTimelineEvent(sent)

    expect(a).toEqual([sent])
    expect(b).toEqual([sent])

    offA()
    offB()
  })

  it('stops delivering events after the unsubscribe is called', () => {
    const received: TimelineLiveEvent[] = []
    const off = onTimelineEvent((event) => received.push(event))

    dispatchTimelineEvent(makeEvent({ eventId: 'evt-1' }))
    off()
    dispatchTimelineEvent(makeEvent({ eventId: 'evt-2' }))

    expect(received).toHaveLength(1)
    expect(received[0].eventId).toBe('evt-1')
  })

  it('preserves null issueNumber and null time fields (no required-field coercion)', () => {
    const received: TimelineLiveEvent[] = []
    const off = onTimelineEvent((event) => received.push(event))

    const sparse: TimelineLiveEvent = {
      issueNumber: null,
      type: 'unknown.type',
      time: null,
      eventId: null,
      payload: {},
    }
    dispatchTimelineEvent(sparse)

    expect(received).toEqual([sparse])

    off()
  })

  it('keeps listeners isolated — handlers do not bleed into later registrations', () => {
    const early: TimelineLiveEvent[] = []
    const late: TimelineLiveEvent[] = []
    const offEarly = onTimelineEvent((event) => early.push(event))

    dispatchTimelineEvent(makeEvent({ eventId: 'evt-1' }))

    const offLate = onTimelineEvent((event) => late.push(event))

    dispatchTimelineEvent(makeEvent({ eventId: 'evt-2' }))

    expect(early.map((e) => e.eventId)).toEqual(['evt-1', 'evt-2'])
    expect(late.map((e) => e.eventId)).toEqual(['evt-2'])

    offEarly()
    offLate()
  })
})
