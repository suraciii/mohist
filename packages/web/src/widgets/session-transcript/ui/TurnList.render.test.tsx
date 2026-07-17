import '@testing-library/jest-dom'
import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { TurnList } from './TurnList'
import type {
  DisplayTurn,
  DisplayPrompt,
  DisplayAssistantPart,
  DisplayChangedFile,
} from '../model/session-transcript-display'

function makePrompt(overrides: Partial<DisplayPrompt> = {}): DisplayPrompt {
  return {
    role: 'mohist',
    text: 'prompt body',
    kind: 'followup',
    sentAt: overrides.sentAt ?? '2024-05-15T10:00:00.000Z',
    ...overrides,
  }
}

function makeTurn(overrides: {
  id?: string
  startedAt: string
  completedAt?: string | null
  prompt?: Partial<DisplayPrompt>
  assistantParts?: DisplayAssistantPart[]
  changedFiles?: DisplayChangedFile[]
  state?: DisplayTurn['state']
}): DisplayTurn {
  return {
    id: overrides.id ?? 'turn-1',
    startedAt: overrides.startedAt,
    completedAt: overrides.completedAt ?? null,
    prompt: makePrompt({ sentAt: overrides.startedAt, ...overrides.prompt }),
    assistantParts: overrides.assistantParts ?? [],
    changedFiles: overrides.changedFiles ?? [],
    state: overrides.state ?? 'idle',
  }
}

describe('TurnList turn header timestamps', () => {
  it('renders a timestamp element for every turn in a multi-turn fixture with text matching each startedAt', () => {
    const turns: DisplayTurn[] = [
      makeTurn({ id: 't1', startedAt: '2024-05-15T10:00:00.000Z' }),
      makeTurn({ id: 't2', startedAt: '2024-05-15T10:30:00.000Z' }),
      makeTurn({ id: 't3', startedAt: '2024-05-15T11:00:00.000Z' }),
    ]

    const { container } = render(<TurnList turns={turns} />)

    const timestamps = container.querySelectorAll<HTMLTimeElement>('[data-turn-timestamp]')
    expect(timestamps).toHaveLength(3)

    expect(timestamps[0].textContent).toBe(new Date(turns[0].startedAt).toLocaleTimeString())
    expect(timestamps[1].textContent).toBe(new Date(turns[1].startedAt).toLocaleTimeString())
    expect(timestamps[2].textContent).toBe(new Date(turns[2].startedAt).toLocaleTimeString())

    expect(timestamps[0].getAttribute('datetime')).toBe(turns[0].startedAt)
    expect(timestamps[1].getAttribute('datetime')).toBe(turns[1].startedAt)
    expect(timestamps[2].getAttribute('datetime')).toBe(turns[2].startedAt)
  })

  it('numbers turn headers 1-based in document order', () => {
    const turns: DisplayTurn[] = [
      makeTurn({ id: 'a', startedAt: '2024-05-15T10:00:00.000Z' }),
      makeTurn({ id: 'b', startedAt: '2024-05-15T10:30:00.000Z' }),
      makeTurn({ id: 'c', startedAt: '2024-05-15T11:00:00.000Z' }),
    ]

    const { container } = render(<TurnList turns={turns} />)
    const indexLabels = container.querySelectorAll('[data-turn-index-label]')

    expect(indexLabels).toHaveLength(3)
    expect(indexLabels[0].textContent).toBe('Turn 1')
    expect(indexLabels[1].textContent).toBe('Turn 2')
    expect(indexLabels[2].textContent).toBe('Turn 3')
  })

  it('positions the turn header above the prompt block inside TurnItem', () => {
    const turn = makeTurn({ id: 't1', startedAt: '2024-05-15T10:00:00.000Z' })

    const { container } = render(<TurnList turns={[turn]} />)

    const turnHeader = container.querySelector('[data-turn-index="1"]')
    expect(turnHeader).toBeTruthy()

    const promptBubble = container.querySelector('.rounded-2xl')
    expect(promptBubble).toBeTruthy()

    // The turn header appears before the prompt bubble in document order,
    // so it lands in the normal reading flow above the prompt block.
    const position = turnHeader!.compareDocumentPosition(promptBubble!)
    expect(position & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  })

  it('uses toLocaleTimeString formatting consistent with AssistantParts formatTime helper', () => {
    const turn = makeTurn({ id: 't1', startedAt: '2024-05-15T10:00:00.000Z' })

    const { container } = render(<TurnList turns={[turn]} />)
    const timestamp = container.querySelector('[data-turn-timestamp]')

    expect(timestamp?.textContent).toBe(new Date(turn.startedAt).toLocaleTimeString())
    expect(timestamp?.textContent).not.toBe(new Date(turn.startedAt).toLocaleString())
  })

  it('does not duplicate the turn-level timestamp for legacy-missing prompt kinds', () => {
    const turn = makeTurn({
      id: 'legacy',
      startedAt: '2024-05-15T10:00:00.000Z',
      prompt: {
        role: 'mohist',
        text: '',
        kind: 'legacy-missing',
        sentAt: '2024-05-15T10:00:00.000Z',
      },
    })

    const { container } = render(<TurnList turns={[turn]} />)

    // Exactly one data-turn-timestamp per turn: the TurnHeader above the prompt block.
    // PromptBlock still renders its internal prompt-level sentAt for legacy-missing,
    // but the turn-level time is sourced from a single place: TurnHeader.
    const turnTimestamps = container.querySelectorAll('[data-turn-timestamp]')
    expect(turnTimestamps).toHaveLength(1)
    expect(turnTimestamps[0].getAttribute('datetime')).toBe(turn.startedAt)
  })

  it('renders a TurnHeader for every prompt kind, not just legacy-missing', () => {
    const turns: DisplayTurn[] = [
      makeTurn({ id: 'initial', startedAt: '2024-05-15T09:00:00.000Z', prompt: { kind: 'initial', sentAt: '2024-05-15T09:00:00.000Z' } }),
      makeTurn({ id: 'task', startedAt: '2024-05-15T09:30:00.000Z', prompt: { kind: 'task', sentAt: '2024-05-15T09:30:00.000Z' } }),
      makeTurn({ id: 'followup', startedAt: '2024-05-15T10:00:00.000Z', prompt: { kind: 'followup', sentAt: '2024-05-15T10:00:00.000Z' } }),
      makeTurn({ id: 'retry', startedAt: '2024-05-15T10:30:00.000Z', prompt: { kind: 'retry', sentAt: '2024-05-15T10:30:00.000Z' } }),
      makeTurn({ id: 'recovery', startedAt: '2024-05-15T11:00:00.000Z', prompt: { kind: 'recovery', sentAt: '2024-05-15T11:00:00.000Z' } }),
      makeTurn({ id: 'legacy', startedAt: '2024-05-15T11:30:00.000Z', prompt: { kind: 'legacy-missing', sentAt: '2024-05-15T11:30:00.000Z' } }),
    ]

    const { container } = render(<TurnList turns={turns} />)
    const timestamps = container.querySelectorAll('[data-turn-timestamp]')

    expect(timestamps).toHaveLength(turns.length)
    Array.from(timestamps).forEach((node, i) => {
      expect(node.getAttribute('datetime')).toBe(turns[i].startedAt)
    })
  })

  it('renders no timestamp elements for an empty turn list', () => {
    const { container } = render(<TurnList turns={[]} />)
    expect(container.querySelectorAll('[data-turn-timestamp]')).toHaveLength(0)
  })
})

describe('TurnList — TurnDiffs accessibility', () => {
  const changedFiles: DisplayChangedFile[] = [
    { path: '/repo/src/foo.ts', operation: 'modified' as const, additions: 2, deletions: 1 },
    { path: '/repo/src/bar.ts', operation: 'modified' as const, additions: 1, deletions: 0 },
  ]

  it('exposes aria-expanded=false on the TurnDiffs disclosure button initially', () => {
    const turns: DisplayTurn[] = [
      makeTurn({ id: 't1', startedAt: '2024-05-15T10:00:00.000Z', changedFiles }),
    ]

    render(<TurnList turns={turns} />)

    const button = screen.getByRole('button', { name: /2 files changed/ })
    expect(button.getAttribute('aria-expanded')).toBe('false')
  })

  it('flips aria-expanded to true after the user expands TurnDiffs', () => {
    const turns: DisplayTurn[] = [
      makeTurn({ id: 't1', startedAt: '2024-05-15T10:00:00.000Z', changedFiles }),
    ]

    render(<TurnList turns={turns} />)

    const button = screen.getByRole('button', { name: /2 files changed/ })
    fireEvent.click(button)

    expect(button.getAttribute('aria-expanded')).toBe('true')
  })

  it('marks the file-icon and chevron svgs aria-hidden', () => {
    const turns: DisplayTurn[] = [
      makeTurn({ id: 't1', startedAt: '2024-05-15T10:00:00.000Z', changedFiles }),
    ]

    const { container } = render(<TurnList turns={turns} />)

    const svgs = container.querySelectorAll('svg')
    expect(svgs.length).toBeGreaterThan(0)
    for (const svg of Array.from(svgs)) {
      expect(svg.getAttribute('aria-hidden')).toBe('true')
    }
  })

  it('exposes a readable accessible name from the "N files changed" text', () => {
    const turns: DisplayTurn[] = [
      makeTurn({ id: 't1', startedAt: '2024-05-15T10:00:00.000Z', changedFiles }),
    ]

    render(<TurnList turns={turns} />)

    const button = screen.getByRole('button', { name: /2 files changed/ })
    const name = button.textContent?.trim() ?? ''
    expect(name.length).toBeGreaterThan(0)
    expect(name).not.toBe('unknown')
    expect(name).toContain('2 files changed')
  })

  it('exposes a readable accessible name on a single-file changed group', () => {
    const turns: DisplayTurn[] = [
      makeTurn({
        id: 't1',
        startedAt: '2024-05-15T10:00:00.000Z',
        changedFiles: [
          { path: '/repo/src/foo.ts', operation: 'modified' as const, additions: 2, deletions: 1 },
        ],
      }),
    ]

    render(<TurnList turns={turns} />)

    const button = screen.getByRole('button', { name: /1 file changed/ })
    const name = button.textContent?.trim() ?? ''
    expect(name.length).toBeGreaterThan(0)
    expect(name).not.toBe('unknown')
    expect(name).toContain('1 file changed')
  })
})