import { describe, expect, it } from 'vitest'
import type { SessionTurn } from '../../../entities/coder-session'
import {
  appendReasoningToTurn,
  appendTextToTurn,
  closeActiveTextPart,
  createTextPart,
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

describe('appendTextToTurn — streamed text paragraph fidelity', () => {
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

describe('appendReasoningToTurn — interrupted-and-resumed text segment boundaries', () => {
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
