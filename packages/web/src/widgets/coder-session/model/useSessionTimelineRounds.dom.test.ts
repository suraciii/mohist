import { describe, it, expect, vi } from 'vitest'
import { reconstructRoundsFromEvents } from './useSessionTimeline'

function makeSessionEvent(
  overrides: Partial<{
    id: number
    sequence: number
    type: string
    payload: unknown
    createdAt: string
  }> = {},
) {
  const sequence = overrides.sequence ?? 0
  return {
    id: overrides.id ?? sequence,
    sequence,
    type: overrides.type ?? 'input',
    payload: overrides.payload,
    createdAt: overrides.createdAt ?? '2024-01-01T00:00:00.000Z',
  }
}

describe('reconstructRoundsFromEvents', () => {
  it('returns empty array for empty events', () => {
    expect(reconstructRoundsFromEvents([])).toEqual([])
  })

  it('routes events through viewSessionEvents timeline projection', async () => {
    const viewModule = await import('../../../entities/session/model/view')
    const spy = vi.spyOn(viewModule, 'viewSessionEvents')
    try {
      const events = [makeSessionEvent({ type: 'input', payload: { text: 'hello' } })]
      reconstructRoundsFromEvents(events)
      expect(spy).toHaveBeenCalledWith(events, 'timeline')
    } finally {
      spy.mockRestore()
    }
  })

  it('creates one round per input with assistant and thought content grouped', () => {
    const events = [
      makeSessionEvent({
        sequence: 0,
        type: 'input',
        payload: { text: 'first prompt', kind: 'initial' },
        createdAt: '2024-01-01T00:00:00Z',
      }),
      makeSessionEvent({
        sequence: 1,
        type: 'assistant_text',
        payload: { text: 'Hello' },
        createdAt: '2024-01-01T00:00:01Z',
      }),
      makeSessionEvent({
        sequence: 2,
        type: 'assistant_text',
        payload: { text: ' world' },
        createdAt: '2024-01-01T00:00:02Z',
      }),
      makeSessionEvent({
        sequence: 3,
        type: 'assistant_reasoning',
        payload: { text: 'thinking' },
        createdAt: '2024-01-01T00:00:03Z',
      }),
      makeSessionEvent({
        sequence: 4,
        type: 'input',
        payload: { text: 'second prompt', kind: 'task' },
        createdAt: '2024-01-01T00:00:04Z',
      }),
      makeSessionEvent({
        sequence: 5,
        type: 'assistant_text',
        payload: { text: 'second' },
        createdAt: '2024-01-01T00:00:05Z',
      }),
    ]

    const rounds = reconstructRoundsFromEvents(events)

    expect(rounds).toHaveLength(2)
    expect(rounds[0].roundIndex).toBe(0)
    expect(rounds[0].userText).toBe('first prompt')
    expect(rounds[0].agentText).toBe('Hello world')
    expect(rounds[0].thoughtText).toBe('thinking')
    expect(rounds[0].startedAt).toBe('2024-01-01T00:00:00Z')
    expect(rounds[1].roundIndex).toBe(1)
    expect(rounds[1].userText).toBe('second prompt')
    expect(rounds[1].agentText).toBe('second')
    expect(rounds[1].thoughtText).toBe('')
  })

  it('groups tool_call and tool_call by toolCallId with updated status', () => {
    const events = [
      makeSessionEvent({
        sequence: 0,
        type: 'input',
        payload: { text: 'use tools' },
        createdAt: '2024-01-01T00:00:00Z',
      }),
      makeSessionEvent({
        sequence: 1,
        type: 'tool_call',
        payload: { toolCallId: 'call-1', kind: 'bash', title: 'bash', rawInput: '{"command":"ls"}' },
        createdAt: '2024-01-01T00:00:01Z',
      }),
      makeSessionEvent({
        sequence: 2,
        type: 'tool_call',
        payload: { toolCallId: 'call-1', status: 'completed', rawOutput: 'file.txt' },
        createdAt: '2024-01-01T00:00:02Z',
      }),
    ]

    const rounds = reconstructRoundsFromEvents(events)

    expect(rounds).toHaveLength(1)
    expect(rounds[0].toolCalls).toHaveLength(1)
    expect(rounds[0].toolCalls[0].toolCallId).toBe('call-1')
    expect(rounds[0].toolCalls[0].toolName).toBe('bash')
    expect(rounds[0].toolCalls[0].state).toBe('completed')
    expect(rounds[0].toolCalls[0].rawOutput).toBe('file.txt')
    expect(rounds[0].toolCalls[0].rawInput).toBe('{"command":"ls"}')
  })

  it('maps session.liveness events to recovery events on the active round', () => {
    const events = [
      makeSessionEvent({ sequence: 0, type: 'input', payload: { text: 'p' }, createdAt: '2024-01-01T00:00:00Z' }),
      makeSessionEvent({
        sequence: 1,
        type: 'session.liveness',
        payload: { status: 'probing', activeProbeVersion: 2 },
        createdAt: '2024-01-01T00:00:01Z',
      }),
      makeSessionEvent({
        sequence: 2,
        type: 'session.liveness',
        payload: { status: 'failed', failureReason: 'timeout' },
        createdAt: '2024-01-01T00:00:02Z',
      }),
    ]

    const rounds = reconstructRoundsFromEvents(events)

    expect(rounds).toHaveLength(1)
    expect(rounds[0].recoveryEvents).toHaveLength(2)
    expect(rounds[0].recoveryEvents[0].status).toBe('recovering')
    expect(rounds[0].recoveryEvents[0].attempt).toBe(2)
    expect(rounds[0].recoveryEvents[1].status).toBe('failed')
    expect(rounds[0].recoveryEvents[1].reason).toBe('timeout')
  })

  it('infers round labels from total count', () => {
    const events = [
      makeSessionEvent({ sequence: 0, type: 'input', payload: { text: 'p1' }, createdAt: '2024-01-01T00:00:00Z' }),
      makeSessionEvent({ sequence: 1, type: 'input', payload: { text: 'p2' }, createdAt: '2024-01-01T00:00:01Z' }),
    ]

    const rounds = reconstructRoundsFromEvents(events)

    expect(rounds[0].label).toBe('proposal.md')
    expect(rounds[1].label).toBe('specs/')
  })

  it('projects a compaction event into the active round with before/after counts', () => {
    const events = [
      makeSessionEvent({ sequence: 0, type: 'input', payload: { text: 'p' }, createdAt: '2024-01-01T00:00:00Z' }),
      makeSessionEvent({
        sequence: 1,
        type: 'compaction',
        payload: {
          strategy: 'summary',
          contextWindowUsedBefore: 950_000,
          contextWindowUsedAfter: 400_000,
          contextWindowSize: 1_000_000,
          summary: 'Kept the original task instructions.',
        },
        createdAt: '2024-01-01T00:00:05Z',
      }),
    ]

    const rounds = reconstructRoundsFromEvents(events)

    expect(rounds).toHaveLength(1)
    expect(rounds[0].compactions).toHaveLength(1)
    const compaction = rounds[0].compactions[0]
    expect(compaction.strategy).toBe('summary')
    expect(compaction.contextWindowUsedBefore).toBe(950_000)
    expect(compaction.contextWindowUsedAfter).toBe(400_000)
    expect(compaction.contextWindowSize).toBe(1_000_000)
    expect(compaction.summary).toBe('Kept the original task instructions.')
    expect(compaction.recordedAt).toBe('2024-01-01T00:00:05Z')
  })

  it('also recognises compaction_event type as a compaction source', () => {
    const events = [
      makeSessionEvent({ sequence: 0, type: 'input', payload: { text: 'p' }, createdAt: '2024-01-01T00:00:00Z' }),
      makeSessionEvent({
        sequence: 1,
        type: 'compaction_event',
        payload: {
          strategy: 'summary',
          contextWindowUsedBefore: 500_000,
          contextWindowUsedAfter: 100_000,
        },
        createdAt: '2024-01-01T00:00:01Z',
      }),
    ]

    const rounds = reconstructRoundsFromEvents(events)
    expect(rounds).toHaveLength(1)
    expect(rounds[0].compactions).toHaveLength(1)
    expect(rounds[0].compactions[0].contextWindowUsedBefore).toBe(500_000)
    expect(rounds[0].compactions[0].contextWindowUsedAfter).toBe(100_000)
  })

  it('attaches orphan compaction events (no preceding input) to a synthesised round', () => {
    const events = [
      makeSessionEvent({
        sequence: 0,
        type: 'compaction',
        payload: {
          contextWindowUsedBefore: 800_000,
          contextWindowUsedAfter: 200_000,
        },
        createdAt: '2024-01-01T00:00:00Z',
      }),
    ]

    const rounds = reconstructRoundsFromEvents(events)
    expect(rounds).toHaveLength(1)
    expect(rounds[0].compactions).toHaveLength(1)
    expect(rounds[0].agentText).toBe('')
    expect(rounds[0].userText).toBe('')
  })

  it('records multiple compaction events in the same round in stream order', () => {
    const events = [
      makeSessionEvent({ sequence: 0, type: 'input', payload: { text: 'p' }, createdAt: '2024-01-01T00:00:00Z' }),
      makeSessionEvent({
        sequence: 1,
        type: 'compaction',
        payload: { contextWindowUsedBefore: 900_000, contextWindowUsedAfter: 400_000 },
        createdAt: '2024-01-01T00:00:05Z',
      }),
      makeSessionEvent({
        sequence: 2,
        type: 'compaction',
        payload: { contextWindowUsedBefore: 800_000, contextWindowUsedAfter: 350_000 },
        createdAt: '2024-01-01T00:00:30Z',
      }),
    ]

    const rounds = reconstructRoundsFromEvents(events)
    expect(rounds).toHaveLength(1)
    expect(rounds[0].compactions).toHaveLength(2)
    expect(rounds[0].compactions[0].contextWindowUsedAfter).toBe(400_000)
    expect(rounds[0].compactions[1].contextWindowUsedAfter).toBe(350_000)
  })
})
