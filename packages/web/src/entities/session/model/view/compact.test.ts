import { describe, expect, it } from 'vitest'
import type { SessionEvent } from '../view'
import { buildCompactView } from './compact'

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

describe('buildCompactView', () => {
  it('summarizes counts, first prompt details, preview, and terminal failure state', () => {
    const previewSource = 'x'.repeat(201)
    const view = buildCompactView([
      makeEvent({
        sequence: 0,
        type: 'input',
        payload: { text: 'Do the thing', kind: 'task' },
        createdAt: '2024-02-01T00:00:00.000Z',
      }),
      makeEvent({
        sequence: 1,
        type: 'assistant_text',
        payload: { text: previewSource },
        createdAt: '2024-02-01T00:00:01.000Z',
      }),
      makeEvent({
        sequence: 2,
        type: 'assistant_reasoning',
        payload: { text: 'Thinking' },
        createdAt: '2024-02-01T00:00:02.000Z',
      }),
      makeEvent({
        sequence: 3,
        type: 'tool_call',
        payload: { toolCallId: 'tc-1' },
        createdAt: '2024-02-01T00:00:03.000Z',
      }),
      makeEvent({
        sequence: 4,
        type: 'tool_call',
        payload: { toolCallId: 'tc-1', status: 'completed' },
        createdAt: '2024-02-01T00:00:04.000Z',
      }),
      makeEvent({
        sequence: 5,
        type: 'session.liveness',
        payload: { status: 'failed', failureReason: 'out of memory' },
        createdAt: '2024-02-01T00:00:05.000Z',
      }),
    ])

    expect(view).toEqual({
      kind: 'compact',
      eventCount: 6,
      toolCount: 1,
      messageChunkCount: 1,
      thoughtChunkCount: 1,
      promptCount: 1,
      terminalStatus: 'failed',
      failureReason: 'out of memory',
      startedAt: '2024-02-01T00:00:00.000Z',
      lastActivityAt: '2024-02-01T00:00:05.000Z',
      firstPromptText: 'Do the thing',
      firstPromptKind: 'task',
      preview: `${'x'.repeat(200)}\u2026`,
    })
  })
})
