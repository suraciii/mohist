import { describe, expect, it } from 'vitest'
import type { SessionEvent } from '../view'
import { buildTimelineView } from './timeline'

function makeEvent(overrides: Partial<SessionEvent> & { type: SessionEvent['type']; payload?: unknown }): SessionEvent {
  const sequence = overrides.sequence ?? 0
  return {
    id: overrides.id ?? sequence,
    sequence,
    type: overrides.type,
    payload: overrides.payload ?? {},
    createdAt: overrides.createdAt ?? '2024-01-01T00:00:00.000Z',
  }
}

describe('buildTimelineView', () => {
  it('groups rounds per input and keeps tool, recovery, and compaction details in the active round', () => {
    const view = buildTimelineView([
      makeEvent({
        sequence: 0,
        type: 'input',
        payload: { text: 'Initial task' },
        createdAt: '2024-05-01T10:00:00.000Z',
      }),
      makeEvent({
        sequence: 1,
        type: 'assistant_reasoning',
        payload: { text: 'Planning the storage change' },
        createdAt: '2024-05-01T10:01:00.000Z',
      }),
      makeEvent({
        sequence: 2,
        type: 'assistant_text',
        payload: { text: 'Added compact transcript segments.' },
        createdAt: '2024-05-01T10:02:00.000Z',
      }),
      makeEvent({
        sequence: 3,
        type: 'tool_call',
        payload: { toolCallId: 'tc-1', kind: 'read', rawInput: '{"file_path":"src/auth.ts"}' },
        createdAt: '2024-05-01T10:03:00.000Z',
      }),
      makeEvent({
        sequence: 4,
        type: 'tool_call',
        payload: { toolCallId: 'tc-1', status: 'completed', rawOutput: 'done' },
        createdAt: '2024-05-01T10:04:00.000Z',
      }),
      makeEvent({
        sequence: 5,
        type: 'compaction',
        payload: {
          strategy: 'summary',
          contextWindowUsedBefore: 950_000,
          contextWindowUsedAfter: 400_000,
          contextWindowSize: 1_000_000,
          summary: 'Kept task instructions.',
        },
        createdAt: '2024-05-01T10:05:00.000Z',
      }),
      makeEvent({
        sequence: 6,
        type: 'session.liveness',
        payload: { status: 'probing', activeProbeVersion: 2 },
        createdAt: '2024-05-01T10:06:00.000Z',
      }),
    ])

    expect(view.kind).toBe('timeline')
    expect(view.rounds).toHaveLength(1)
    expect(view.rounds[0]).toMatchObject({
      roundIndex: 0,
      userText: 'Initial task',
      agentText: 'Added compact transcript segments.',
      thoughtText: 'Planning the storage change',
      completedAt: '2024-05-01T10:06:00.000Z',
    })
    expect(view.rounds[0].toolCalls).toEqual([
      {
        toolCallId: 'tc-1',
        toolName: 'read',
        state: 'completed',
        rawInput: '{"file_path":"src/auth.ts"}',
        rawOutput: 'done',
        startedAt: '2024-05-01T10:03:00.000Z',
        completedAt: '2024-05-01T10:04:00.000Z',
      },
    ])
    expect(view.rounds[0].compactions[0]).toMatchObject({
      strategy: 'summary',
      contextWindowUsedBefore: 950_000,
      contextWindowUsedAfter: 400_000,
      contextWindowSize: 1_000_000,
      summary: 'Kept task instructions.',
      at: '2024-05-01T10:05:00.000Z',
    })
    expect(view.rounds[0].recovery).toEqual([
      { status: 'recovering', attempt: 2, at: '2024-05-01T10:06:00.000Z' },
    ])
  })
})
