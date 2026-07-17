import { describe, expect, it } from 'vitest'
import type { SessionTurn, ToolPart } from '../../../entities/coder-session'
import {
  appendInputTurn,
  appendReasoningToTurn,
  appendTextToTurn,
  asPayloadRecord,
  asRecord,
  buildLiveToolDetails,
  closeActiveTextPart,
  closeLatestTurn,
  createErrorPart,
  createInputTurn,
  createReasoningPart,
  createTemporaryTurn,
  createTextPart,
  createToolPart,
  deriveToolTarget,
  ensureLiveTurn,
  findToolByCorrelation,
  getDisplayFields,
  getNormalizedName,
  getNumber,
  getString,
  isTerminalState,
  mapStatusToDisplay,
  normalizePromptKind,
  truncatePreview,
  updateToolInTurn,
  type LiveToolCall,
} from './transcript-state'

function baseTurn(overrides: Partial<SessionTurn> = {}): SessionTurn {
  return {
    id: 'turn-1',
    startedAt: '2026-06-12T00:00:00.000Z',
    completedAt: null,
    incomplete: true,
    user: {
      role: 'mohist',
      text: '',
      kind: 'task',
      sentAt: '2026-06-12T00:00:00.000Z',
    },
    assistant: [],
    ...overrides,
  }
}

describe('appendInputTurn', () => {
  it('appends a new live turn when turns is empty', () => {
    const next = appendInputTurn([], { text: 'hello', kind: 'task', sentAt: '2026-06-12T00:00:00.000Z' })

    expect(next).toHaveLength(1)
    expect(next[0].user.text).toBe('hello')
    expect(next[0].user.kind).toBe('task')
    expect(next[0].user.sentAt).toBe('2026-06-12T00:00:00.000Z')
    expect(next[0].assistant).toEqual([])
    expect(next[0].completedAt).toBeNull()
    expect(next[0].incomplete).toBe(true)
    expect(next[0].id).toMatch(/^live-/)
  })

  it('appends a fresh turn after a completed turn', () => {
    const completed = baseTurn({ completedAt: '2026-06-12T00:01:00.000Z', incomplete: false })
    const next = appendInputTurn([completed], { text: 'next', kind: 'followup', sentAt: '2026-06-12T00:02:00.000Z' })

    expect(next).toHaveLength(2)
    expect(next[0]).toBe(completed)
    expect(next[1].user.text).toBe('next')
    expect(next[1].user.kind).toBe('followup')
  })

  it('updates the existing live turn in place when text matches and no assistant output yet', () => {
    const live = baseTurn({ id: 'live-existing', user: { role: 'mohist', text: 'hello', kind: 'task', sentAt: '2026-06-12T00:00:00.000Z' } })
    const next = appendInputTurn([live], { text: 'hello', kind: 'retry', sentAt: '2026-06-12T00:00:05.000Z' })

    expect(next).toHaveLength(1)
    expect(next[0].id).toBe('live-existing')
    expect(next[0].user.kind).toBe('retry')
    expect(next[0].user.sentAt).toBe('2026-06-12T00:00:05.000Z')
  })

  it('closes the prior live turn before appending a new one when prior turn has assistant output', () => {
    const liveWithAssistant = baseTurn({
      id: 'live-with-output',
      user: { role: 'mohist', text: 'first', kind: 'task', sentAt: '2026-06-12T00:00:00.000Z' },
      assistant: [createTextPart('partial', '2026-06-12T00:00:01.000Z')],
    })

    const next = appendInputTurn([liveWithAssistant], { text: 'second', kind: 'followup', sentAt: '2026-06-12T00:01:00.000Z' })

    expect(next).toHaveLength(2)
    expect(next[0].completedAt).toBe('2026-06-12T00:01:00.000Z')
    expect(next[0].incomplete).toBe(false)
    expect(next[1].user.text).toBe('second')
  })
})

describe('appendTextToTurn', () => {
  it('creates a new text part when there is no active text part', () => {
    const turn = baseTurn()
    const next = appendTextToTurn(turn, 'hello')

    expect(next.assistant).toHaveLength(1)
    expect(next.assistant[0].type).toBe('text')
    expect(next.assistant[0].type === 'text' ? next.assistant[0].text : '').toBe('hello')
    expect(next.assistant[0].type === 'text' ? next.assistant[0].completedAt : null).toBeNull()
  })

  it('appends to an existing open text part', () => {
    const existing = createTextPart('hello ', '2026-06-12T00:00:00.000Z')
    const turn = baseTurn({ assistant: [existing] })

    const next = appendTextToTurn(turn, 'world')

    expect(next.assistant).toHaveLength(1)
    expect(next.assistant[0].type === 'text' ? next.assistant[0].text : '').toBe('hello world')
  })

  it('does not touch already-closed text parts', () => {
    const closed = { ...createTextPart('closed', '2026-06-12T00:00:00.000Z'), completedAt: '2026-06-12T00:00:01.000Z' }
    const turn = baseTurn({ assistant: [closed] })

    const next = appendTextToTurn(turn, 'fresh')

    expect(next.assistant).toHaveLength(2)
    expect(next.assistant[0]).toEqual(closed)
    expect(next.assistant[1].type === 'text' ? next.assistant[1].text : '').toBe('fresh')
  })

  it('concatenates a multi-paragraph delta sequence losslessly with paragraph boundary spanning two deltas', () => {
    const turn = baseTurn()
    const deltas = [
      'First paragraph line 1.\n',
      '\n',
      'Second paragraph about usage:\n',
      '\n',
      'Let me read the file.',
    ]

    const next = deltas.reduce<SessionTurn>((acc, delta) => appendTextToTurn(acc, delta), turn)

    expect(next.assistant).toHaveLength(1)
    const part = next.assistant[0]
    if (part.type !== 'text') throw new Error('expected text part')

    expect(part.text).toBe('First paragraph line 1.\n\nSecond paragraph about usage:\n\nLet me read the file.')
    expect(part.text).toContain('\n\n')
    expect(part.text).not.toContain('usage:Let me')
    expect(part.text).not.toContain('usage:Let')
  })

  it('preserves whitespace and ordering across many small token-level deltas', () => {
    const turn = baseTurn()
    const source = 'usage:\n\nLet me check the docs.\n\nThe relevant section is below.'
    const tokens = source.split('')

    const next = tokens.reduce<SessionTurn>((acc, delta) => appendTextToTurn(acc, delta), turn)

    expect(next.assistant).toHaveLength(1)
    const part = next.assistant[0]
    if (part.type !== 'text') throw new Error('expected text part')

    expect(part.text).toBe(source)
    expect(part.text).not.toContain('usage:Let me')
  })

  it('opens a fresh text part when the previous one is closed (preserving paragraph boundary across the gap)', () => {
    const open = createTextPart('first paragraph\n', '2026-06-12T00:00:00.000Z')
    const turn = baseTurn({
      assistant: [{ ...open, completedAt: '2026-06-12T00:00:01.000Z' }],
    })

    const next = appendTextToTurn(turn, '\nNew paragraph after close.')

    expect(next.assistant).toHaveLength(2)
    expect(next.assistant[0].type === 'text' ? next.assistant[0].text : '').toBe('first paragraph\n')
    expect(next.assistant[1].type === 'text' ? next.assistant[1].text : '').toBe('\nNew paragraph after close.')
    expect(next.assistant[0].type === 'text' ? next.assistant[0].completedAt : null).toBe('2026-06-12T00:00:01.000Z')
  })
})

describe('closeActiveTextPart', () => {
  it('closes the open text part with the given timestamp', () => {
    const part = createTextPart('streaming', '2026-06-12T00:00:00.000Z')
    const turn = baseTurn({ assistant: [part] })

    const next = closeActiveTextPart(turn, '2026-06-12T00:00:05.000Z')

    expect(next.assistant[0].type === 'text' ? next.assistant[0].completedAt : 'nope').toBe('2026-06-12T00:00:05.000Z')
  })

  it('returns the turn unchanged when there is no active text part', () => {
    const closed = { ...createTextPart('closed', '2026-06-12T00:00:00.000Z'), completedAt: '2026-06-12T00:00:01.000Z' }
    const turn = baseTurn({ assistant: [closed] })

    const next = closeActiveTextPart(turn, '2026-06-12T00:00:05.000Z')

    expect(next).toBe(turn)
  })
})

describe('appendReasoningToTurn', () => {
  it('creates a new reasoning part when none is active', () => {
    const turn = baseTurn()
    const next = appendReasoningToTurn(turn, 'thinking...')

    expect(next.assistant).toHaveLength(1)
    expect(next.assistant[0].type).toBe('reasoning')
    expect(next.assistant[0].type === 'reasoning' ? next.assistant[0].text : '').toBe('thinking...')
  })

  it('appends to an existing open reasoning part', () => {
    const existing = createReasoningPart('thinking ', '2026-06-12T00:00:00.000Z')
    const turn = baseTurn({ assistant: [existing] })

    const next = appendReasoningToTurn(turn, 'more')

    expect(next.assistant[0].type === 'reasoning' ? next.assistant[0].text : '').toBe('thinking more')
  })

  it('reasoning interrupt closes the open text part and a subsequent text append opens a fresh part', () => {
    const openText = createTextPart('usage:\n', '2026-06-12T00:00:00.000Z')
    const turn = baseTurn({ assistant: [openText] })

    const interrupted = appendReasoningToTurn(closeActiveTextPart(turn, '2026-06-12T00:00:01.000Z'), 'thinking about tools')
    const resumed = appendTextToTurn(interrupted, '\nLet me read the file.')

    expect(resumed.assistant).toHaveLength(3)
    const [first, reasoning, later] = resumed.assistant
    if (first.type !== 'text') throw new Error('expected first text part')
    if (reasoning.type !== 'reasoning') throw new Error('expected reasoning part')
    if (later.type !== 'text') throw new Error('expected later text part')

    expect(first.text).toBe('usage:\n')
    expect(first.completedAt).toBe('2026-06-12T00:00:01.000Z')
    expect(reasoning.text).toBe('thinking about tools')
    expect(reasoning.completedAt).toBeNull()

    expect(later.id).not.toBe(first.id)
    expect(later.text).toBe('\nLet me read the file.')
    expect(later.completedAt).toBeNull()
    expect(resumed.assistant.filter((p) => p.type === 'text')).toHaveLength(2)
  })

  it('reasoning interrupt then resume preserves the paragraph boundary across the gap', () => {
    const openText = createTextPart('usage:', '2026-06-12T00:00:00.000Z')
    const turn = baseTurn({ assistant: [openText] })

    const interrupted = appendReasoningToTurn(closeActiveTextPart(turn, '2026-06-12T00:00:01.000Z'), 'hmm')
    const resumed = appendTextToTurn(interrupted, '\n\nLet me check.')

    const texts = resumed.assistant.filter((p) => p.type === 'text')
    if (texts.length !== 2) throw new Error('expected 2 text parts')
    const [first, second] = texts
    if (first.type !== 'text' || second.type !== 'text') throw new Error('expected text parts')

    expect(first.text).toBe('usage:')
    expect(second.text).toBe('\n\nLet me check.')
    expect(`${first.text}${second.text}`).not.toContain('usage:Let me')
  })
})

describe('updateToolInTurn', () => {
  function toolPartFixture(): { turn: SessionTurn; part: ToolPart } {
    const part: ToolPart = {
      id: 'part-1',
      type: 'tool',
      tool: {
        toolCallId: 'call-1',
        normalizedName: 'bash',
        toolName: 'bash',
        status: 'running',
        title: 'echo',
        target: 'echo',
        input: '{"command":"ls"}',
        output: '',
        startedAt: '2026-06-12T00:00:00.000Z',
        completedAt: null,
        rawInput: '{"command":"ls"}',
        rawOutput: '',
      },
    }
    return { turn: baseTurn({ assistant: [part] }), part }
  }

  it('updates an existing tool part by toolCallId', () => {
    const { turn, part } = toolPartFixture()

    const next = updateToolInTurn(turn, 'call-1', {
      status: 'completed',
      output: 'file1\nfile2',
    })

    const toolPart = next.assistant[0]
    if (toolPart.type !== 'tool') throw new Error('expected tool part')

    expect(toolPart.tool.toolCallId).toBe('call-1')
    expect(toolPart.tool.status).toBe('completed')
    expect(toolPart.tool.output).toBe('file1\nfile2')
    expect(toolPart.tool.completedAt).not.toBeNull()
    expect(toolPart.tool.startedAt).toBe(part.tool.startedAt)
  })

  it('appends a new tool part when toolCallId does not match and no correlation key', () => {
    const { turn } = toolPartFixture()

    const next = updateToolInTurn(turn, 'call-2', {
      toolName: 'read',
      status: 'started',
      rawInput: { filePath: '/etc/hosts' },
    })

    expect(next.assistant).toHaveLength(2)
    const toolPart = next.assistant[1]
    if (toolPart.type !== 'tool') throw new Error('expected tool part')

    expect(toolPart.tool.toolCallId).toBe('call-2')
    expect(toolPart.tool.toolName).toBe('read')
    expect(toolPart.tool.status).toBe('running')
  })

  it('correlates by normalized name and target when correlation key is provided', () => {
    const { turn } = toolPartFixture()

    const next = updateToolInTurn(turn, 'call-new', {
      toolName: 'bash',
      status: 'completed',
      output: 'done',
    }, 'bash|echo')

    expect(next.assistant).toHaveLength(1)
    const toolPart = next.assistant[0]
    if (toolPart.type !== 'tool') throw new Error('expected tool part')

    expect(toolPart.tool.toolCallId).toBe('call-new')
    expect(toolPart.tool.status).toBe('completed')
    expect(toolPart.tool.output).toBe('done')
    expect(toolPart.tool.completedAt).not.toBeNull()
  })

  it('does not correlate onto a tool part that has reached a terminal state', () => {
    const part: ToolPart = {
      id: 'part-1',
      type: 'tool',
      tool: {
        toolCallId: 'call-1',
        normalizedName: 'bash',
        toolName: 'bash',
        status: 'completed',
        title: 'echo',
        input: '{"command":"ls"}',
        output: 'old',
        startedAt: '2026-06-12T00:00:00.000Z',
        completedAt: '2026-06-12T00:00:01.000Z',
        rawInput: '{"command":"ls"}',
        rawOutput: 'old',
      },
    }
    const turn = baseTurn({ assistant: [part] })

    const next = updateToolInTurn(turn, 'call-new', {
      toolName: 'bash',
      status: 'completed',
      output: 'fresh',
    }, 'bash|echo')

    expect(next.assistant).toHaveLength(2)
  })
})

describe('ensureLiveTurn', () => {
  it('returns a copy of turns when at least one exists', () => {
    const turn = baseTurn()
    const next = ensureLiveTurn([turn], '2026-06-12T00:00:00.000Z')

    expect(next).toEqual([turn])
    expect(next).not.toBe([turn])
  })

  it('seeds a temporary turn when turns is empty', () => {
    const next = ensureLiveTurn([], '2026-06-12T00:00:00.000Z')

    expect(next).toHaveLength(1)
    expect(next[0].incomplete).toBe(true)
    expect(next[0].user.kind).toBe('legacy-missing')
    expect(next[0].user.text).toMatch(/loading/i)
  })
})

describe('closeLatestTurn', () => {
  it('closes any open text or reasoning parts and the turn itself', () => {
    const text = createTextPart('streaming', '2026-06-12T00:00:00.000Z')
    const turn = baseTurn({ assistant: [text] })

    const next = closeLatestTurn([turn], '2026-06-12T00:00:05.000Z')

    expect(next[0].completedAt).toBe('2026-06-12T00:00:05.000Z')
    expect(next[0].incomplete).toBe(false)
    expect(next[0].assistant[0].type === 'text' ? next[0].assistant[0].completedAt : 'nope').toBe('2026-06-12T00:00:05.000Z')
  })

  it('seeds a temporary turn when turns is empty', () => {
    const next = closeLatestTurn([], '2026-06-12T00:00:05.000Z')

    expect(next).toHaveLength(1)
    expect(next[0].completedAt).toBe('2026-06-12T00:00:05.000Z')
    expect(next[0].incomplete).toBe(false)
  })
})

describe('createInputTurn / createTemporaryTurn / createTextPart / createReasoningPart / createErrorPart', () => {
  it('createInputTurn defaults to legacy-missing kind', () => {
    const turn = createInputTurn({ text: 'hi' })
    expect(turn.user.kind).toBe('legacy-missing')
    expect(turn.user.text).toBe('hi')
  })

  it('createTemporaryTurn always uses legacy-missing kind', () => {
    const turn = createTemporaryTurn('2026-06-12T00:00:00.000Z')
    expect(turn.user.kind).toBe('legacy-missing')
    expect(turn.incomplete).toBe(true)
  })

  it('createTextPart / createReasoningPart produce open parts', () => {
    const text = createTextPart('a', '2026-06-12T00:00:00.000Z')
    expect(text.completedAt).toBeNull()
    expect(text.text).toBe('a')

    const reason = createReasoningPart('b', '2026-06-12T00:00:00.000Z')
    expect(reason.completedAt).toBeNull()
    expect(reason.text).toBe('b')
  })

  it('createErrorPart carries message, kind, and timestamp', () => {
    const part = createErrorPart('boom', 'failed', '2026-06-12T00:00:00.000Z')
    expect(part.message).toBe('boom')
    expect(part.kind).toBe('failed')
    expect(part.at).toBe('2026-06-12T00:00:00.000Z')
  })
})

describe('createToolPart', () => {
  it('maps status and attaches display fields', () => {
    const tool: LiveToolCall = {
      toolCallId: 'call-1',
      toolName: 'bash',
      status: 'started',
      title: 'ls',
      startedAt: '2026-06-12T00:00:00.000Z',
      completedAt: null,
      rawInput: { command: 'ls' },
      rawOutput: undefined,
    }

    const part = createToolPart(tool)

    expect(part.type).toBe('tool')
    if (part.type !== 'tool') throw new Error('expected tool part')

    expect(part.tool.status).toBe('running')
    expect(part.tool.toolCallId).toBe('call-1')
    expect(part.tool.displayTitle).toBe('ls')
    expect(part.tool.normalizedName).toBe('bash')
    expect(part.tool.completedAt).toBeNull()
  })

  it('maps terminal statuses correctly', () => {
    const completed = createToolPart({
      toolCallId: 'call-1',
      toolName: 'bash',
      status: 'completed',
      startedAt: '2026-06-12T00:00:00.000Z',
      completedAt: '2026-06-12T00:00:05.000Z',
    })
    expect(completed.type === 'tool' ? completed.tool.status : 'nope').toBe('completed')

    const failed = createToolPart({
      toolCallId: 'call-2',
      toolName: 'bash',
      status: 'failed',
      startedAt: '2026-06-12T00:00:00.000Z',
      completedAt: '2026-06-12T00:00:05.000Z',
    })
    expect(failed.type === 'tool' ? failed.tool.status : 'nope').toBe('failed')

    const cancelled = createToolPart({
      toolCallId: 'call-3',
      toolName: 'bash',
      status: 'cancelled',
      startedAt: '2026-06-12T00:00:00.000Z',
      completedAt: '2026-06-12T00:00:05.000Z',
    })
    expect(cancelled.type === 'tool' ? cancelled.tool.status : 'nope').toBe('cancelled')

    const timeout = createToolPart({
      toolCallId: 'call-4',
      toolName: 'bash',
      status: 'timeout',
      startedAt: '2026-06-12T00:00:00.000Z',
      completedAt: '2026-06-12T00:00:05.000Z',
    })
    expect(timeout.type === 'tool' ? timeout.tool.status : 'nope').toBe('failed')
  })

  it('parses edit input into changedFiles when present', () => {
    const tool: LiveToolCall = {
      toolCallId: 'call-edit',
      toolName: 'edit',
      status: 'completed',
      startedAt: '2026-06-12T00:00:00.000Z',
      completedAt: '2026-06-12T00:00:05.000Z',
      input: JSON.stringify({
        filePath: 'src/foo.ts',
        oldString: 'a',
        newString: 'b',
        patchText: '*** Update File: src/foo.ts\n-a\n+b',
      }),
    }

    const part = createToolPart(tool)
    if (part.type !== 'tool') throw new Error('expected tool part')

    expect(part.tool.changedFiles).toBeDefined()
    expect(part.tool.changedFiles?.[0]?.path).toBe('src/foo.ts')
  })
})

describe('mapStatusToDisplay / isTerminalState', () => {
  it('maps live statuses to display statuses', () => {
    expect(mapStatusToDisplay('started')).toBe('running')
    expect(mapStatusToDisplay('completed')).toBe('completed')
    expect(mapStatusToDisplay('failed')).toBe('failed')
    expect(mapStatusToDisplay('timeout')).toBe('failed')
    expect(mapStatusToDisplay('cancelled')).toBe('cancelled')
    expect(mapStatusToDisplay('unknown')).toBe('pending')
  })

  it('flags terminal states', () => {
    expect(isTerminalState('completed')).toBe(true)
    expect(isTerminalState('failed')).toBe(true)
    expect(isTerminalState('timeout')).toBe(true)
    expect(isTerminalState('cancelled')).toBe(true)
    expect(isTerminalState('started')).toBe(false)
    expect(isTerminalState('running')).toBe(false)
  })
})

describe('normalizePromptKind', () => {
  it('passes through known kinds', () => {
    for (const kind of ['initial', 'task', 'retry', 'followup', 'recovery'] as const) {
      expect(normalizePromptKind(kind)).toBe(kind)
    }
  })

  it('falls back to legacy-missing for unknown kinds', () => {
    expect(normalizePromptKind(undefined)).toBe('legacy-missing')
    expect(normalizePromptKind('something-else')).toBe('legacy-missing')
  })
})

describe('findToolByCorrelation', () => {
  function makeTurn(): SessionTurn {
    const tool: ToolPart = {
      id: 'part-1',
      type: 'tool',
      tool: {
        toolCallId: 'call-1',
        normalizedName: 'bash',
        toolName: 'bash',
        status: 'running',
        title: 'echo',
        target: 'echo',
        startedAt: '2026-06-12T00:00:00.000Z',
        completedAt: null,
      },
    }
    return baseTurn({ assistant: [tool] })
  }

  it('matches by normalized name', () => {
    expect(findToolByCorrelation(makeTurn(), 'bash')).toBe(0)
  })

  it('matches by normalized name + target', () => {
    expect(findToolByCorrelation(makeTurn(), 'bash', 'echo')).toBe(0)
    expect(findToolByCorrelation(makeTurn(), 'bash', 'other')).toBe(-1)
  })

  it('skips terminal tool parts', () => {
    const terminal: ToolPart = {
      id: 'part-1',
      type: 'tool',
      tool: {
        toolCallId: 'call-1',
        normalizedName: 'bash',
        toolName: 'bash',
        status: 'completed',
        title: 'echo',
        target: 'echo',
        startedAt: '2026-06-12T00:00:00.000Z',
        completedAt: '2026-06-12T00:00:05.000Z',
      },
    }
    const turn = baseTurn({ assistant: [terminal] })
    expect(findToolByCorrelation(turn, 'bash')).toBe(-1)
  })
})

describe('buildLiveToolDetails', () => {
  it('returns execution family details for bash', () => {
    const details = buildLiveToolDetails('bash', { command: 'ls' }, { exitCode: 0, stdout: 'file' })
    expect(details?.family).toBe('execution')
    expect(details?.command).toBe('ls')
    expect(details?.exitCode).toBe(0)
    expect(details?.outputPreview).toBe('file')
    expect(details?.completionStatus).toBe('completed')
  })

  it('returns delegation family details for task', () => {
    const details = buildLiveToolDetails('task', { description: 'do work', subagent_type: 'coder' }, undefined)
    expect(details?.family).toBe('delegation')
    expect(details?.description).toBe('do work')
    expect(details?.subagentType).toBe('coder')
  })

  it('returns planning family details for todowrite', () => {
    const details = buildLiveToolDetails('todowrite', { todos: [{ status: 'pending' }, { status: 'done' }] }, undefined)
    expect(details?.family).toBe('planning')
    expect(details?.totalCount).toBe(2)
    expect(details?.statusCounts).toEqual({ pending: 1, done: 1 })
  })

  it('returns interaction family details for webfetch', () => {
    const details = buildLiveToolDetails('webfetch', { url: 'https://example.com' }, { content: 'preview' })
    expect(details?.family).toBe('interaction')
    expect(details?.url).toBe('https://example.com')
    expect(details?.resultPreview).toBe('preview')
  })

  it('returns undefined for unrelated tool names', () => {
    expect(buildLiveToolDetails('unknown', { foo: 'bar' }, undefined)).toBeUndefined()
  })
})

describe('payload helpers', () => {
  it('asRecord accepts objects, rejects arrays and primitives', () => {
    expect(asRecord({ a: 1 })).toEqual({ a: 1 })
    expect(asRecord([])).toBeNull()
    expect(asRecord('hi')).toBeNull()
    expect(asRecord(null)).toBeNull()
  })

  it('asPayloadRecord parses JSON strings', () => {
    expect(asPayloadRecord('{"a":1}')).toEqual({ a: 1 })
    expect(asPayloadRecord({ a: 1 })).toEqual({ a: 1 })
    expect(asPayloadRecord('not json')).toBeNull()
  })

  it('getNumber accepts only finite numbers', () => {
    expect(getNumber(42)).toBe(42)
    expect(getNumber(0)).toBe(0)
    expect(getNumber(NaN)).toBeUndefined()
    expect(getNumber('1')).toBeUndefined()
  })

  it('getString accepts only non-empty strings', () => {
    expect(getString('hi')).toBe('hi')
    expect(getString('')).toBeUndefined()
    expect(getString(42)).toBeUndefined()
  })

  it('truncatePreview caps by length', () => {
    expect(truncatePreview('hello', 10)).toBe('hello')
    expect(truncatePreview('hello world', 5)).toBe('hello...')
    expect(truncatePreview('')).toBe('')
  })
})

describe('deriveToolTarget', () => {
  it('prefers file path from input when available', () => {
    expect(deriveToolTarget('read', { filePath: '/etc/hosts' }, 'Read')).toBe('/etc/hosts')
  })

  it('falls back to tool label or title', () => {
    expect(deriveToolTarget('bash', { command: 'ls -la' }, 'Bash')).toBe('ls -la')
    expect(deriveToolTarget('bash', undefined, 'Some title')).toBe('Some title')
  })
})

describe('getNormalizedName / getDisplayFields', () => {
  it('getNormalizedName reuses provided normalizedName', () => {
    expect(getNormalizedName({ normalizedName: 'read', toolName: 'should-not-overwrite' })).toBe('read')
  })

  it('getDisplayFields prefers explicit title', () => {
    const fields = getDisplayFields({ toolName: 'bash', title: 'ls', rawInput: {} })
    expect(fields.displayTitle).toBe('ls')
  })

  it('getDisplayFields falls back to displayTitle from detail', () => {
    const fields = getDisplayFields({
      toolName: 'bash',
      displayTitle: 'from-meta',
      displaySubtitle: 'subtitle',
      rawInput: {},
    })
    expect(fields.displayTitle).toBe('from-meta')
    expect(fields.displaySubtitle).toBe('subtitle')
  })
})