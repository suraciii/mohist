import '@testing-library/jest-dom'
import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { TurnList } from './TurnList'
import type {
  DisplayTurn,
  DisplayPrompt,
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
  startedAt?: string
  changedFiles?: DisplayChangedFile[]
}): DisplayTurn {
  const startedAt = overrides.startedAt ?? '2024-05-15T10:00:00.000Z'
  return {
    id: overrides.id ?? 'turn-1',
    startedAt,
    completedAt: null,
    prompt: makePrompt({ sentAt: startedAt }),
    assistantParts: [],
    changedFiles: overrides.changedFiles ?? [],
    state: 'idle',
  }
}

describe('TurnDiffs accessibility', () => {
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
