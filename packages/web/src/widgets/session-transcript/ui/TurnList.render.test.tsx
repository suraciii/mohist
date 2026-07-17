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
import { promptKindLabel } from '../model/prompt-kind-labels'

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

describe('TurnList divider bar — turn ordinal, prompt kind label, start time, duration', () => {
  it('renders a divider bar for every turn in a multi-turn fixture with timestamp text matching each startedAt', () => {
    const turns: DisplayTurn[] = [
      makeTurn({ id: 't1', startedAt: '2024-05-15T10:00:00.000Z' }),
      makeTurn({ id: 't2', startedAt: '2024-05-15T10:30:00.000Z' }),
      makeTurn({ id: 't3', startedAt: '2024-05-15T11:00:00.000Z' }),
    ]

    const { container } = render(<TurnList turns={turns} />)

    const dividers = container.querySelectorAll('[data-turn-divider]')
    expect(dividers).toHaveLength(3)

    const timestamps = container.querySelectorAll<HTMLTimeElement>('[data-turn-timestamp]')
    expect(timestamps).toHaveLength(3)
    expect(timestamps[0].textContent).toBe(new Date(turns[0].startedAt).toLocaleTimeString())
    expect(timestamps[1].textContent).toBe(new Date(turns[1].startedAt).toLocaleTimeString())
    expect(timestamps[2].textContent).toBe(new Date(turns[2].startedAt).toLocaleTimeString())

    expect(timestamps[0].getAttribute('datetime')).toBe(turns[0].startedAt)
    expect(timestamps[1].getAttribute('datetime')).toBe(turns[1].startedAt)
    expect(timestamps[2].getAttribute('datetime')).toBe(turns[2].startedAt)
  })

  it('numbers turn dividers 1-based in document order', () => {
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

  it('renders the prompt-kind label on each divider bar', () => {
    const turns: DisplayTurn[] = [
      makeTurn({ id: 't1', startedAt: '2024-05-15T10:00:00.000Z', prompt: { kind: 'initial' } }),
      makeTurn({ id: 't2', startedAt: '2024-05-15T10:30:00.000Z', prompt: { kind: 'followup' } }),
      makeTurn({ id: 't3', startedAt: '2024-05-15T11:00:00.000Z', prompt: { kind: 'task' } }),
    ]

    const { container } = render(<TurnList turns={turns} />)
    const kindLabels = container.querySelectorAll('[data-turn-kind-label]')

    expect(kindLabels).toHaveLength(3)
    expect(kindLabels[0].textContent).toBe(promptKindLabel('initial'))
    expect(kindLabels[1].textContent).toBe(promptKindLabel('followup'))
    expect(kindLabels[2].textContent).toBe(promptKindLabel('task'))
  })

  it('positions the divider bar above the prompt block inside TurnItem', () => {
    const turn = makeTurn({ id: 't1', startedAt: '2024-05-15T10:00:00.000Z' })

    const { container } = render(<TurnList turns={[turn]} />)

    const divider = container.querySelector('[data-turn-divider]')
    expect(divider).toBeTruthy()

    const promptBlock = container.querySelector('[data-prompt-block]')
    expect(promptBlock).toBeTruthy()

    const position = divider!.compareDocumentPosition(promptBlock!)
    expect(position & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  })

  it('uses toLocaleTimeString formatting for the divider-bar timestamp', () => {
    const turn = makeTurn({ id: 't1', startedAt: '2024-05-15T10:00:00.000Z' })

    const { container } = render(<TurnList turns={[turn]} />)
    const timestamp = container.querySelector('[data-turn-timestamp]')

    expect(timestamp?.textContent).toBe(new Date(turn.startedAt).toLocaleTimeString())
    expect(timestamp?.textContent).not.toBe(new Date(turn.startedAt).toLocaleString())
  })

  it('renders a divider bar for every prompt kind, not just legacy-missing', () => {
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

  it('renders no divider bar for an empty turn list', () => {
    const { container } = render(<TurnList turns={[]} />)
    expect(container.querySelectorAll('[data-turn-divider]')).toHaveLength(0)
    expect(container.querySelectorAll('[data-turn-timestamp]')).toHaveLength(0)
  })
})

describe('TurnList divider duration derivation', () => {
  it('shows a duration on the divider bar of a completed turn (turn has completedAt)', () => {
    const turn = makeTurn({
      id: 't1',
      startedAt: '2024-05-15T10:00:00.000Z',
      completedAt: '2024-05-15T10:00:42.000Z',
    })

    const { container } = render(<TurnList turns={[turn]} />)
    const duration = container.querySelector('[data-turn-duration]')
    expect(duration).toBeTruthy()
    expect(duration?.textContent).toBe('42.0s')
  })

  it('shows minutes-level duration when the turn spans more than a minute', () => {
    const turn = makeTurn({
      id: 't1',
      startedAt: '2024-05-15T10:00:00.000Z',
      completedAt: '2024-05-15T10:02:30.000Z',
    })

    const { container } = render(<TurnList turns={[turn]} />)
    const duration = container.querySelector('[data-turn-duration]')
    expect(duration?.textContent).toBe('2m 30s')
  })

  it('does not render a finalized duration on a running turn (completedAt is null)', () => {
    const turn = makeTurn({
      id: 't1',
      startedAt: '2024-05-15T10:00:00.000Z',
      completedAt: null,
    })

    const { container } = render(<TurnList turns={[turn]} />)
    expect(container.querySelector('[data-turn-duration]')).toBeNull()
  })

  it('does not render a finalized duration when completedAt is missing', () => {
    const turn = makeTurn({
      id: 't1',
      startedAt: '2024-05-15T10:00:00.000Z',
    })

    const { container } = render(<TurnList turns={[turn]} />)
    expect(container.querySelector('[data-turn-duration]')).toBeNull()
  })

  it('mixes completed and running turns: completed show duration, running do not', () => {
    const turns: DisplayTurn[] = [
      makeTurn({ id: 't1', startedAt: '2024-05-15T10:00:00.000Z', completedAt: '2024-05-15T10:00:15.000Z' }),
      makeTurn({ id: 't2', startedAt: '2024-05-15T10:01:00.000Z', completedAt: null }),
    ]

    const { container } = render(<TurnList turns={turns} />)

    const durations = container.querySelectorAll('[data-turn-duration]')
    expect(durations).toHaveLength(1)
    expect(durations[0].textContent).toBe('15.0s')
  })
})

describe('TurnList full-width timeline invariant', () => {
  it('does not apply a max-width cap to the turn list container', () => {
    const turn = makeTurn({ id: 't1', startedAt: '2024-05-15T10:00:00.000Z' })
    const { container } = render(<TurnList turns={[turn]} />)

    const log = container.querySelector('[role="log"]')
    expect(log).toBeTruthy()
    expect(log!.className).not.toContain('max-w-2xl')
    expect(log!.className).not.toContain('mx-auto')
  })

  it('does not render the legacy rounded bubble classes on the prompt block', () => {
    const turn = makeTurn({ id: 't1', startedAt: '2024-05-15T10:00:00.000Z' })
    const { container } = render(<TurnList turns={[turn]} />)

    const promptBlock = container.querySelector('[data-prompt-block]')
    expect(promptBlock).toBeTruthy()
    expect(promptBlock!.className).not.toContain('rounded-2xl')
    expect(promptBlock!.className).not.toContain('justify-end')
  })

  it('keeps turn-level data-turn-ref attributes on each turn for navigation anchoring', () => {
    const turns: DisplayTurn[] = [
      makeTurn({ id: 'a', startedAt: '2024-05-15T10:00:00.000Z' }),
      makeTurn({ id: 'b', startedAt: '2024-05-15T10:30:00.000Z' }),
    ]

    const { container } = render(<TurnList turns={turns} />)
    const refs = container.querySelectorAll('[data-turn-ref]')
    expect(refs).toHaveLength(2)
    expect(refs[0].getAttribute('data-turn-id')).toBe('a')
    expect(refs[1].getAttribute('data-turn-id')).toBe('b')
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