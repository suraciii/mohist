import { describe, it, expect } from 'vitest'
import {
  viewSessionEvents,
  type SessionEvent,
  type SessionChatView,
  type SessionTimelineView,
  type SessionCompactView,
} from './view'

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

const STREAM: SessionEvent[] = [
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
    type: 'input',
    payload: { text: 'Apply the fix', kind: 'task' },
    createdAt: '2024-01-01T10:00:06.000Z',
  }),
  makeEvent({
    sequence: 7,
    type: 'assistant_reasoning',
    payload: { text: 'Drafting a minimal patch' },
    createdAt: '2024-01-01T10:00:07.000Z',
  }),
  makeEvent({
    sequence: 8,
    type: 'tool_call',
    payload: {
      toolCallId: 'tc-2',
      kind: 'apply_patch',
      title: 'Apply patch to auth.ts',
      rawInput: '{"file":"src/auth.ts","patch":"@@ -1 +1 @@"}',
    },
    createdAt: '2024-01-01T10:00:08.000Z',
  }),
  makeEvent({
    sequence: 9,
    type: 'tool_call',
    payload: {
      toolCallId: 'tc-2',
      status: 'completed',
      rawOutput: 'patch applied',
    },
    createdAt: '2024-01-01T10:00:09.000Z',
  }),
  makeEvent({
    sequence: 10,
    type: 'assistant_text',
    payload: { text: 'Fix is in place.' },
    createdAt: '2024-01-01T10:00:10.000Z',
  }),
  makeEvent({
    sequence: 11,
    type: 'session.activity',
    payload: { activity: 'idle' },
    createdAt: '2024-01-01T10:00:11.000Z',
  }),
]

describe('viewSessionEvents chat projection', () => {
  it('returns a chat view with prompt-led turns in stream order', () => {
    const view = viewSessionEvents(STREAM, 'chat')
    expect(view.kind).toBe('chat')
    expect(view.turns).toHaveLength(2)
    expect(view.turns[0].prompt.text).toBe('Fix the login bug')
    expect(view.turns[0].prompt.kind).toBe('initial')
    expect(view.turns[0].startedAt).toBe('2024-01-01T10:00:00.000Z')
    expect(view.turns[1].prompt.text).toBe('Apply the fix')
    expect(view.turns[1].prompt.kind).toBe('task')
    expect(view.turns[1].startedAt).toBe('2024-01-01T10:00:06.000Z')
  })

  it('accumulates adjacent assistant text chunks into a single text part per turn', () => {
    const view = viewSessionEvents(STREAM, 'chat') as SessionChatView
    const firstTurn = view.turns[0]
    const textParts = firstTurn.parts.filter((p) => p.partType === 'text')
    expect(textParts).toHaveLength(1)
    const textPart = textParts[0] as Extract<typeof textParts[number], { partType: 'text' }>
    expect(textPart.text).toBe('Reading the source to find the cause.')
    expect(textPart.startedAt).toBe('2024-01-01T10:00:02.000Z')
    expect(textPart.completedAt).not.toBeNull()
  })

  it('renders persisted transcript text segments', () => {
    const view = viewSessionEvents([
      makeEvent({
        sequence: 1,
        type: 'input',
        payload: { text: 'Implement transcript aggregation', kind: 'task' },
      }),
      makeEvent({
        sequence: 2,
        type: 'assistant_reasoning',
        payload: { text: 'Planning the storage change' },
      }),
      makeEvent({
        sequence: 3,
        type: 'assistant_text',
        payload: { text: 'Added compact transcript segments.' },
      }),
    ], 'chat') as SessionChatView

    const parts = view.turns[0].parts
    expect(parts.find((p) => p.partType === 'reasoning')).toMatchObject({ text: 'Planning the storage change' })
    expect(parts.find((p) => p.partType === 'text')).toMatchObject({ text: 'Added compact transcript segments.' })
  })

  it('captures reasoning chunks in dedicated reasoning parts', () => {
    const view = viewSessionEvents(STREAM, 'chat') as SessionChatView
    const firstTurn = view.turns[0]
    const reasoningParts = firstTurn.parts.filter((p) => p.partType === 'reasoning')
    expect(reasoningParts).toHaveLength(1)
    const reasoning = reasoningParts[0] as Extract<typeof reasoningParts[number], { partType: 'reasoning' }>
    expect(reasoning.text).toBe('Inspecting the auth handler')
    expect(reasoning.startedAt).toBe('2024-01-01T10:00:01.000Z')
  })

  it('merges tool_call and tool_call into a single tool part with terminal status', () => {
    const view = viewSessionEvents(STREAM, 'chat') as SessionChatView
    const firstTurn = view.turns[0]
    const toolParts = firstTurn.parts.filter((p) => p.partType === 'tool')
    expect(toolParts).toHaveLength(1)
    const tool = toolParts[0] as Extract<typeof toolParts[number], { partType: 'tool' }>
    expect(tool.toolCallId).toBe('tc-1')
    expect(tool.toolName).toBe('read')
    expect(tool.normalizedName).toBe('read')
    expect(tool.status).toBe('completed')
    expect(tool.input).toBe('{"file_path":"src/auth.ts"}')
    expect(tool.output).toBe('export const auth = ...')
    expect(tool.startedAt).toBe('2024-01-01T10:00:04.000Z')
    expect(tool.completedAt).toBe('2024-01-01T10:00:05.000Z')
  })

  it('keeps text, reasoning, and tool parts ordered by stream sequence', () => {
    const view = viewSessionEvents(STREAM, 'chat') as SessionChatView
    const firstTurn = view.turns[0]
    const order = firstTurn.parts.map((p) => p.partType)
    expect(order).toEqual(['reasoning', 'text', 'tool'])
  })

  it('places terminal completion timestamp on the final turn', () => {
    const view = viewSessionEvents(STREAM, 'chat') as SessionChatView
    const lastTurn = view.turns[view.turns.length - 1]
    expect(lastTurn.completedAt).toBe('2024-01-01T10:00:11.000Z')
    expect(lastTurn.incomplete).toBe(false)
  })

  it('isolates parts across separate prompts', () => {
    const view = viewSessionEvents(STREAM, 'chat') as SessionChatView
    const secondTurn = view.turns[1]
    const toolParts = secondTurn.parts.filter((p) => p.partType === 'tool')
    const textParts = secondTurn.parts.filter((p) => p.partType === 'text')
    expect(toolParts).toHaveLength(1)
    expect(textParts).toHaveLength(1)
    const tool = toolParts[0] as Extract<typeof toolParts[number], { partType: 'tool' }>
    expect(tool.toolCallId).toBe('tc-2')
    expect(tool.normalizedName).toBe('apply_patch')
    const text = textParts[0] as Extract<typeof textParts[number], { partType: 'text' }>
    expect(text.text).toBe('Fix is in place.')
  })

  it('returns empty turns for an empty event stream', () => {
    const view = viewSessionEvents([], 'chat')
    expect(view.kind).toBe('chat')
    expect(view.turns).toEqual([])
  })
})

describe('viewSessionEvents timeline projection', () => {
  it('returns timeline rounds grouped per input', () => {
    const view = viewSessionEvents(STREAM, 'timeline')
    expect(view.kind).toBe('timeline')
    expect(view.rounds).toHaveLength(2)
    expect(view.rounds[0].roundIndex).toBe(0)
    expect(view.rounds[0].userText).toBe('Fix the login bug')
    expect(view.rounds[0].startedAt).toBe('2024-01-01T10:00:00.000Z')
    expect(view.rounds[1].roundIndex).toBe(1)
    expect(view.rounds[1].userText).toBe('Apply the fix')
    expect(view.rounds[1].startedAt).toBe('2024-01-01T10:00:06.000Z')
  })

  it('aggregates agent and thought text within each round', () => {
    const view = viewSessionEvents(STREAM, 'timeline') as SessionTimelineView
    expect(view.rounds[0].agentText).toBe('Reading the source to find the cause.')
    expect(view.rounds[0].thoughtText).toBe('Inspecting the auth handler')
    expect(view.rounds[1].agentText).toBe('Fix is in place.')
    expect(view.rounds[1].thoughtText).toBe('Drafting a minimal patch')
  })

  it('merges tool_call and tool_call by toolCallId with terminal state', () => {
    const view = viewSessionEvents(STREAM, 'timeline') as SessionTimelineView
    expect(view.rounds[0].toolCalls).toHaveLength(1)
    const tool = view.rounds[0].toolCalls[0]
    expect(tool.toolCallId).toBe('tc-1')
    expect(tool.toolName).toBe('read')
    expect(tool.state).toBe('completed')
    expect(tool.rawInput).toBe('{"file_path":"src/auth.ts"}')
    expect(tool.rawOutput).toBe('export const auth = ...')
    expect(tool.startedAt).toBe('2024-01-01T10:00:04.000Z')
    expect(tool.completedAt).toBe('2024-01-01T10:00:05.000Z')

    expect(view.rounds[1].toolCalls).toHaveLength(1)
    const secondTool = view.rounds[1].toolCalls[0]
    expect(secondTool.toolCallId).toBe('tc-2')
    expect(secondTool.toolName).toBe('apply_patch')
    expect(secondTool.state).toBe('completed')
  })

  it('sets the final round completedAt to the last event timestamp', () => {
    const view = viewSessionEvents(STREAM, 'timeline') as SessionTimelineView
    expect(view.rounds[1].completedAt).toBe('2024-01-01T10:00:11.000Z')
  })

  it('returns empty rounds for an empty event stream', () => {
    const view = viewSessionEvents([], 'timeline')
    expect(view.kind).toBe('timeline')
    expect(view.rounds).toEqual([])
  })

  it('attaches a compaction event to the active round with before/after counts', () => {
    const stream: SessionEvent[] = [
      makeEvent({
        sequence: 0,
        type: 'input',
        payload: { text: 'Initial task' },
        createdAt: '2024-05-01T10:00:00.000Z',
      }),
      makeEvent({
        sequence: 1,
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
    ]
    const view = viewSessionEvents(stream, 'timeline') as SessionTimelineView
    expect(view.rounds).toHaveLength(1)
    expect(view.rounds[0].compactions).toHaveLength(1)
    const compaction = view.rounds[0].compactions[0]
    expect(compaction.strategy).toBe('summary')
    expect(compaction.contextWindowUsedBefore).toBe(950_000)
    expect(compaction.contextWindowUsedAfter).toBe(400_000)
    expect(compaction.contextWindowSize).toBe(1_000_000)
    expect(compaction.summary).toBe('Kept task instructions.')
    expect(compaction.at).toBe('2024-05-01T10:05:00.000Z')
  })

  it('attaches a compaction_event to a synthesised round when no input exists', () => {
    const stream: SessionEvent[] = [
      makeEvent({
        sequence: 0,
        type: 'compaction_event',
        payload: {
          contextWindowUsedBefore: 500_000,
          contextWindowUsedAfter: 200_000,
        },
        createdAt: '2024-05-02T10:00:00.000Z',
      }),
    ]
    const view = viewSessionEvents(stream, 'timeline') as SessionTimelineView
    expect(view.rounds).toHaveLength(1)
    expect(view.rounds[0].userText).toBe('')
    expect(view.rounds[0].agentText).toBe('')
    expect(view.rounds[0].compactions).toHaveLength(1)
    expect(view.rounds[0].compactions[0].contextWindowUsedBefore).toBe(500_000)
  })
})

describe('viewSessionEvents compact projection', () => {
  it('counts events, prompts, tool calls, and chunks consistently with the same stream', () => {
    const view = viewSessionEvents(STREAM, 'compact')
    expect(view.kind).toBe('compact')
    expect(view.eventCount).toBe(12)
    expect(view.promptCount).toBe(2)
    expect(view.messageChunkCount).toBe(3)
    expect(view.thoughtChunkCount).toBe(2)
  })

  it('counts unique tool calls across tool_call and tool_call events', () => {
    const view = viewSessionEvents(STREAM, 'compact') as SessionCompactView
    expect(view.toolCount).toBe(2)
  })

  it('records terminal status and failure reason from session.activity=idle', () => {
    const view = viewSessionEvents(STREAM, 'compact') as SessionCompactView
    expect(view.terminalStatus).toBe('completed')
    expect(view.failureReason).toBeUndefined()
  })

  it('captures first prompt text and kind, plus a preview of the first assistant chunk', () => {
    const view = viewSessionEvents(STREAM, 'compact') as SessionCompactView
    expect(view.firstPromptText).toBe('Fix the login bug')
    expect(view.firstPromptKind).toBe('initial')
    expect(view.preview).toBe('Reading the source ')
  })

  it('records started and last activity timestamps from the stream', () => {
    const view = viewSessionEvents(STREAM, 'compact') as SessionCompactView
    expect(view.startedAt).toBe('2024-01-01T10:00:00.000Z')
    expect(view.lastActivityAt).toBe('2024-01-01T10:00:11.000Z')
  })

  it('reports failure reason when session.liveness reports failure', () => {
    const failedStream: SessionEvent[] = [
      makeEvent({
        sequence: 0,
        type: 'input',
        payload: { text: 'Do the thing', kind: 'task' },
        createdAt: '2024-02-01T00:00:00.000Z',
      }),
      makeEvent({
        sequence: 1,
        type: 'session.liveness',
        payload: { status: 'failed', failureReason: 'out of memory' },
        createdAt: '2024-02-01T00:00:01.000Z',
      }),
    ]
    const view = viewSessionEvents(failedStream, 'compact') as SessionCompactView
    expect(view.terminalStatus).toBe('failed')
    expect(view.failureReason).toBe('out of memory')
  })

  it('returns default compact shape for an empty event stream', () => {
    const view = viewSessionEvents([], 'compact') as SessionCompactView
    expect(view.eventCount).toBe(0)
    expect(view.promptCount).toBe(0)
    expect(view.messageChunkCount).toBe(0)
    expect(view.thoughtChunkCount).toBe(0)
    expect(view.toolCount).toBe(0)
    expect(view.terminalStatus).toBe('running')
    expect(view.startedAt).toBeNull()
    expect(view.lastActivityAt).toBeNull()
    expect(view.firstPromptText).toBeNull()
    expect(view.firstPromptKind).toBeNull()
    expect(view.preview).toBeNull()
  })
})

describe('viewSessionEvents centralization', () => {
  it('ignores server-projected turn fields on raw events when projecting', () => {
    const projectedPayload = {
      text: 'Pre-projected',
      turns: [
        {
          id: 'server-turn-1',
          assistant: [{ partType: 'text', text: 'Server-side projection' }],
        },
      ],
      workflowLogs: [{ type: 'assistant_text', text: 'Server-side log' }],
    }
    const streamWithProjection: SessionEvent[] = [
      makeEvent({
        sequence: 0,
        type: 'input',
        payload: { text: 'Real prompt', kind: 'initial' },
        createdAt: '2024-03-01T00:00:00.000Z',
      }),
      makeEvent({
        sequence: 1,
        type: 'assistant_text',
        payload: projectedPayload,
        createdAt: '2024-03-01T00:00:01.000Z',
      }),
    ]
    const chat = viewSessionEvents(streamWithProjection, 'chat') as SessionChatView
    expect(chat.turns).toHaveLength(1)
    expect(chat.turns[0].prompt.text).toBe('Real prompt')
    const textPart = chat.turns[0].parts.find((p) => p.partType === 'text') as
      | Extract<typeof chat.turns[0]['parts'][number], { partType: 'text' }>
      | undefined
    expect(textPart?.text).toBe('Pre-projected')
    expect(chat.turns[0].parts.some((p) => p.partType === 'tool')).toBe(false)

    const timeline = viewSessionEvents(streamWithProjection, 'timeline') as SessionTimelineView
    expect(timeline.rounds).toHaveLength(1)
    expect(timeline.rounds[0].agentText).toBe('Pre-projected')
    expect(timeline.rounds[0].userText).toBe('Real prompt')

    const compact = viewSessionEvents(streamWithProjection, 'compact') as SessionCompactView
    expect(compact.firstPromptText).toBe('Real prompt')
    expect(compact.preview).toBe('Pre-projected')
    expect(compact.messageChunkCount).toBe(1)
  })

  it('does not surface server-side transcript field names from its inputs', () => {
    const projectedPayload = {
      text: 'Pre-projected',
      turns: [{ id: 'server-turn-1' }],
      assistant: [{ partType: 'text', text: 'Server-side projection' }],
      workflowLogs: [{ type: 'assistant_text' }],
    }
    const stream: SessionEvent[] = [
      makeEvent({
        sequence: 0,
        type: 'input',
        payload: projectedPayload,
        createdAt: '2024-04-01T00:00:00.000Z',
      }),
    ]
    const chat = viewSessionEvents(stream, 'chat') as SessionChatView
    const timeline = viewSessionEvents(stream, 'timeline') as SessionTimelineView
    const compact = viewSessionEvents(stream, 'compact') as SessionCompactView

    expect(chat.turns[0].prompt.text).toBe('Pre-projected')
    expect(timeline.rounds[0].userText).toBe('Pre-projected')
    expect(compact.firstPromptText).toBe('Pre-projected')
    expect(compact.promptCount).toBe(1)
  })

  it('narrates the same ordered event stream identically across chat, timeline, and compact kinds', () => {
    const chat = viewSessionEvents(STREAM, 'chat') as SessionChatView
    const timeline = viewSessionEvents(STREAM, 'timeline') as SessionTimelineView
    const compact = viewSessionEvents(STREAM, 'compact') as SessionCompactView

    expect(chat.turns.map((t) => t.prompt.text)).toEqual(
      timeline.rounds.map((r) => r.userText),
    )
    expect(chat.turns.map((t) => t.prompt.kind)).toEqual(['initial', 'task'])
    expect(compact.firstPromptText).toBe(chat.turns[0].prompt.text)
    expect(compact.toolCount).toBe(
      chat.turns.reduce((sum, turn) => sum + turn.parts.filter((p) => p.partType === 'tool').length, 0),
    )
  })
})
