import { describe, expect, it } from 'vitest'
import type { SessionEvent } from '../view'
import { buildChatView } from './chat'

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

describe('buildChatView', () => {
  it('projects prompt-led turns with ordered reasoning, text, and tool parts', () => {
    const view = buildChatView([
      makeEvent({
        sequence: 0,
        type: 'input',
        payload: { text: 'Fix the login bug', kind: 'initial' },
        createdAt: '2024-01-01T10:00:00.000Z',
      }),
      makeEvent({
        sequence: 1,
        type: 'assistant_reasoning',
        payload: { text: 'Inspecting the auth handler' },
        createdAt: '2024-01-01T10:00:01.000Z',
      }),
      makeEvent({
        sequence: 2,
        type: 'assistant_text',
        payload: { text: 'Reading the source ' },
        createdAt: '2024-01-01T10:00:02.000Z',
      }),
      makeEvent({
        sequence: 3,
        type: 'assistant_text',
        payload: { text: 'to find the cause.' },
        createdAt: '2024-01-01T10:00:03.000Z',
      }),
      makeEvent({
        sequence: 4,
        type: 'tool_call',
        payload: {
          toolCallId: 'tc-1',
          kind: 'read',
          title: 'Read src/auth.ts',
          rawInput: '{"file_path":"src/auth.ts"}',
        },
        createdAt: '2024-01-01T10:00:04.000Z',
      }),
      makeEvent({
        sequence: 5,
        type: 'tool_call',
        payload: {
          toolCallId: 'tc-1',
          status: 'completed',
          rawOutput: 'export const auth = ...',
        },
        createdAt: '2024-01-01T10:00:05.000Z',
      }),
      makeEvent({
        sequence: 6,
        type: 'session.activity',
        payload: { activity: 'idle' },
        createdAt: '2024-01-01T10:00:06.000Z',
      }),
    ])

    expect(view.kind).toBe('chat')
    expect(view.turns).toHaveLength(1)
    expect(view.turns[0].prompt).toMatchObject({ text: 'Fix the login bug', kind: 'initial' })
    expect(view.turns[0].parts.map((part) => part.partType)).toEqual(['reasoning', 'text', 'tool'])
    expect(view.turns[0].parts[1]).toMatchObject({ text: 'Reading the source to find the cause.' })
    expect(view.turns[0].parts[2]).toMatchObject({
      toolCallId: 'tc-1',
      normalizedName: 'read',
      status: 'completed',
      input: '{"file_path":"src/auth.ts"}',
      output: 'export const auth = ...',
      completedAt: '2024-01-01T10:00:05.000Z',
    })
    expect(view.turns[0].completedAt).toBe('2024-01-01T10:00:06.000Z')
  })
})
