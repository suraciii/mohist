import { describe, expect, it } from 'vitest'
import { viewSessionEvents, type SessionChatView, type SessionEvent, type SessionTimelineView } from './view'

function makeEvent(sequence: number, type: SessionEvent['type'], payload: unknown, createdAt: string): SessionEvent {
  return { id: sequence, sequence, type, payload, createdAt }
}

const PI_STREAM: SessionEvent[] = [
  makeEvent(0, 'session.input', { text: 'Inspect the release', kind: 'task' }, '2024-02-01T10:00:00.000Z'),
  makeEvent(1, 'reasoning.delta', { text: 'I will inspect the repository first.' }, '2024-02-01T10:00:01.000Z'),
  makeEvent(2, 'message.delta', { text: 'I found the release manifest.' }, '2024-02-01T10:00:02.000Z'),
  makeEvent(3, 'tool_call.started', { toolCallId: 'pi-tool-1', toolName: 'read', status: 'running', rawInput: '{"path":"package.json"}' }, '2024-02-01T10:00:03.000Z'),
  makeEvent(4, 'tool_call.completed', { toolCallId: 'pi-tool-1', toolName: 'read', status: 'completed', rawOutput: 'version: 1.0.0' }, '2024-02-01T10:00:04.000Z'),
  makeEvent(5, 'provider.retry', { phase: 'provider', attempt: 2, maxAttempts: 5, delayMs: 1000, message: 'temporary upstream response' }, '2024-02-01T10:00:05.000Z'),
  makeEvent(6, 'compaction_event', { strategy: 'automatic', contextWindowUsedBefore: 30000, contextWindowUsedAfter: 8000, contextWindowSize: 32000, summary: 'Earlier context compacted' }, '2024-02-01T10:00:06.000Z'),
  makeEvent(7, 'session.activity', { activity: 'idle' }, '2024-02-01T10:00:07.000Z'),
]

describe('Pi facts through runtime-neutral Session views', () => {
  it('renders transcript, reasoning, tool lifecycle, and provider retry in chat', () => {
    const view = viewSessionEvents(PI_STREAM, 'chat') as SessionChatView
    expect(view.turns[0].parts).toEqual(expect.arrayContaining([
      expect.objectContaining({ partType: 'reasoning', text: 'I will inspect the repository first.' }),
      expect.objectContaining({ partType: 'text', text: 'I found the release manifest.' }),
      expect.objectContaining({ partType: 'tool', toolCallId: 'pi-tool-1', status: 'completed' }),
      expect.objectContaining({ partType: 'error', kind: 'recovery', message: 'Provider retry: provider (2/5) - temporary upstream response' }),
    ]))
    expect(view.turns[0].completedAt).toBe('2024-02-01T10:00:07.000Z')
  })

  it('renders tool lifecycle, automatic compaction, and provider retry in timeline', () => {
    const round = (viewSessionEvents(PI_STREAM, 'timeline') as SessionTimelineView).rounds[0]
    expect(round.agentText).toBe('I found the release manifest.')
    expect(round.thoughtText).toBe('I will inspect the repository first.')
    expect(round.toolCalls[0]).toMatchObject({ toolCallId: 'pi-tool-1', state: 'completed' })
    expect(round.compactions[0]).toMatchObject({ strategy: 'automatic', contextWindowUsedBefore: 30000, contextWindowUsedAfter: 8000 })
    expect(round.recovery[0]).toMatchObject({ status: 'recovering', attempt: 2, reason: 'temporary upstream response' })
  })
})
