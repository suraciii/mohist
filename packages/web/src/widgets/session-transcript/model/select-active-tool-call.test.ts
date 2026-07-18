import { describe, expect, it } from 'vitest'
import type { DisplayToolPart, DisplayTurn } from './session-transcript-display'
import { selectActiveToolCall } from './select-active-tool-call'

const STARTED_AT = '2026-01-01T00:00:00.000Z'

function makeToolPart(overrides: Partial<DisplayToolPart>): DisplayToolPart {
  return {
    id: 'tool-1',
    partType: 'tool',
    toolCallId: 'tc-1',
    normalizedName: 'bash',
    toolName: 'bash',
    status: 'completed',
    startedAt: STARTED_AT,
    completedAt: '2026-01-01T00:00:01.000Z',
    hasError: false,
    isContextTool: false,
    ...overrides,
  } as DisplayToolPart
}

function makeTurn(overrides: {
  id?: string
  assistantParts?: DisplayTurn['assistantParts']
}): DisplayTurn {
  return {
    id: overrides.id ?? 'turn-1',
    startedAt: STARTED_AT,
    completedAt: null,
    prompt: {
      role: 'mohist',
      text: 'prompt body',
      kind: 'followup',
      sentAt: STARTED_AT,
    },
    assistantParts: overrides.assistantParts ?? [],
    changedFiles: [],
    state: 'idle',
  }
}

describe('selectActiveToolCall', () => {
  it('returns null when there are no turns', () => {
    expect(selectActiveToolCall([])).toBeNull()
  })

  it('returns null when no turn contains a tool part', () => {
    const turns: DisplayTurn[] = [
      makeTurn({ id: 't1', assistantParts: [{ id: 'p1', partType: 'text', text: 'hello', startedAt: STARTED_AT, completedAt: STARTED_AT }] }),
    ]
    expect(selectActiveToolCall(turns)).toBeNull()
  })

  it('returns null when every tool part is in a terminal state', () => {
    const turns: DisplayTurn[] = [
      makeTurn({ id: 't1', assistantParts: [makeToolPart({ id: 'a', status: 'completed' })] }),
      makeTurn({ id: 't2', assistantParts: [makeToolPart({ id: 'b', status: 'failed' })] }),
      makeTurn({ id: 't3', assistantParts: [makeToolPart({ id: 'c', status: 'cancelled' })] }),
    ]
    expect(selectActiveToolCall(turns)).toBeNull()
  })

  it('returns the single in-progress tool part when there is exactly one', () => {
    const live = makeToolPart({ id: 'live', toolCallId: 'tc-live', status: 'running' })
    const turns: DisplayTurn[] = [
      makeTurn({ id: 't1', assistantParts: [makeToolPart({ id: 'done-1', status: 'completed' })] }),
      makeTurn({ id: 't2', assistantParts: [live] }),
      makeTurn({ id: 't3', assistantParts: [makeToolPart({ id: 'done-2', status: 'failed' })] }),
    ]
    expect(selectActiveToolCall(turns)).toBe(live)
  })

  it('treats pending as in-progress alongside running', () => {
    const pending = makeToolPart({ id: 'pending', toolCallId: 'tc-pending', status: 'pending' })
    const turns: DisplayTurn[] = [
      makeTurn({ id: 't1', assistantParts: [pending] }),
    ]
    expect(selectActiveToolCall(turns)).toBe(pending)
  })

  it('breaks ties deterministically by returning the last in-progress tool part in turn order', () => {
    const first = makeToolPart({ id: 'first', toolCallId: 'tc-1', status: 'running' })
    const second = makeToolPart({ id: 'second', toolCallId: 'tc-2', status: 'running' })
    const third = makeToolPart({ id: 'third', toolCallId: 'tc-3', status: 'pending' })
    const turns: DisplayTurn[] = [
      makeTurn({ id: 't1', assistantParts: [first, second] }),
      makeTurn({ id: 't2', assistantParts: [third] }),
    ]
    expect(selectActiveToolCall(turns)).toBe(third)
  })

  it('skips over terminal tool parts when finding the last in-progress match', () => {
    const live = makeToolPart({ id: 'live', toolCallId: 'tc-live', status: 'running' })
    const turns: DisplayTurn[] = [
      makeTurn({
        id: 't1',
        assistantParts: [
          makeToolPart({ id: 'done-a', status: 'completed' }),
          live,
          makeToolPart({ id: 'done-b', status: 'failed' }),
        ],
      }),
    ]
    expect(selectActiveToolCall(turns)).toBe(live)
  })

  it('also considers tool parts nested inside a context-group', () => {
    const nested = makeToolPart({ id: 'nested', toolCallId: 'tc-nested', status: 'running' })
    const turns: DisplayTurn[] = [
      makeTurn({
        id: 't1',
        assistantParts: [
          {
            id: 'ctx-1',
            partType: 'context-group',
            title: 'Explored',
            tools: [makeToolPart({ id: 'done-x', status: 'completed' }), nested],
            hasError: false,
          },
        ],
      }),
    ]
    expect(selectActiveToolCall(turns)).toBe(nested)
  })

  it('returns the last in-progress tool across a mix of top-level and nested tools', () => {
    const topLive = makeToolPart({ id: 'top', toolCallId: 'tc-top', status: 'running' })
    const nestedLive = makeToolPart({ id: 'nested', toolCallId: 'tc-nested', status: 'running' })
    const turns: DisplayTurn[] = [
      makeTurn({
        id: 't1',
        assistantParts: [
          {
            id: 'ctx-1',
            partType: 'context-group',
            title: 'Explored',
            tools: [nestedLive],
            hasError: false,
          },
          topLive,
        ],
      }),
    ]
    expect(selectActiveToolCall(turns)).toBe(topLive)
  })
})