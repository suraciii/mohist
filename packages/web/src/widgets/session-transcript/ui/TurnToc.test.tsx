// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { TurnTocList, TurnTocRail, buildTurnTocEntries } from './TurnToc'
import type { DisplayTurn } from '../model/session-transcript-display'

function makeTurn(overrides: {
  id?: string
  prompt?: Partial<DisplayTurn['prompt']>
}): DisplayTurn {
  return {
    id: overrides.id ?? 'turn-1',
    startedAt: '2024-05-15T10:00:00.000Z',
    completedAt: null,
    prompt: {
      role: 'mohist',
      text: 'prompt body',
      kind: 'followup',
      sentAt: '2024-05-15T10:00:00.000Z',
      ...overrides.prompt,
    },
    assistantParts: [],
    changedFiles: [],
    state: 'idle',
  }
}

function fakeElement(): HTMLDivElement {
  return document.createElement('div')
}

describe('TurnToc', () => {
  describe('buildTurnTocEntries', () => {
    it('lists one entry per turn in document order with a 1-based index', () => {
      const turns: DisplayTurn[] = [
        makeTurn({ id: 't1', prompt: { kind: 'initial', title: 'First' } }),
        makeTurn({ id: 't2', prompt: { kind: 'followup', title: 'Second' } }),
        makeTurn({ id: 't3', prompt: { kind: 'task', title: 'Third' } }),
      ]
      const refs = new Map<number, HTMLDivElement>()
      refs.set(1, fakeElement())
      refs.set(2, fakeElement())
      refs.set(3, fakeElement())

      const entries = buildTurnTocEntries(turns, refs)

      expect(entries).toHaveLength(3)
      expect(entries.map(e => e.index)).toEqual([1, 2, 3])
      expect(entries.map(e => e.turnId)).toEqual(['t1', 't2', 't3'])
      expect(entries.map(e => e.label)).toEqual([
        'Initial Task · First',
        'Follow-up · Second',
        'Task · Third',
      ])
    })

    it('falls back to the kind label when the title is missing', () => {
      const turns: DisplayTurn[] = [
        makeTurn({ id: 't1', prompt: { kind: 'initial' } }),
        makeTurn({ id: 't2', prompt: { kind: 'task' } }),
      ]
      const refs = new Map<number, HTMLDivElement>()
      refs.set(1, fakeElement())
      refs.set(2, fakeElement())

      const entries = buildTurnTocEntries(turns, refs)

      expect(entries.map(e => e.label)).toEqual(['Initial Task', 'Task'])
    })

    it('reads each turn ref by its 1-based index from the map', () => {
      const turns: DisplayTurn[] = [
        makeTurn({ id: 'a', prompt: { kind: 'task', title: 'A' } }),
        makeTurn({ id: 'b', prompt: { kind: 'task', title: 'B' } }),
      ]
      const firstEl = fakeElement()
      const secondEl = fakeElement()
      const refs = new Map<number, HTMLDivElement>([[1, firstEl], [2, secondEl]])

      const entries = buildTurnTocEntries(turns, refs)

      expect(entries[0].target).toBe(firstEl)
      expect(entries[1].target).toBe(secondEl)
    })

    it('returns entries with null target when the ref map has no element for that index', () => {
      const turns: DisplayTurn[] = [
        makeTurn({ id: 'a', prompt: { kind: 'task', title: 'A' } }),
        makeTurn({ id: 'b', prompt: { kind: 'task', title: 'B' } }),
      ]
      const refs = new Map<number, HTMLDivElement>([[1, fakeElement()]])

      const entries = buildTurnTocEntries(turns, refs)

      expect(entries[0].target).not.toBeNull()
      expect(entries[1].target).toBeNull()
    })

    it('truncates the prompt title to 60 characters', () => {
      const longTitle = 'x'.repeat(80)
      const turns: DisplayTurn[] = [
        makeTurn({ id: 't1', prompt: { kind: 'followup', title: longTitle } }),
      ]
      const refs = new Map<number, HTMLDivElement>([[1, fakeElement()]])

      const entries = buildTurnTocEntries(turns, refs)

      const titlePortion = entries[0].label.replace(/^Follow-up · /, '')
      expect(titlePortion.length).toBe(60)
    })
  })

  describe('TurnTocList', () => {
    let scrollIntoViewSpy: ReturnType<typeof vi.spyOn>

    beforeEach(() => {
      scrollIntoViewSpy = vi.spyOn(Element.prototype, 'scrollIntoView').mockImplementation(() => {})
    })

    afterEach(() => {
      scrollIntoViewSpy.mockRestore()
    })

    it('renders one entry per entry in the list and in the same order', () => {
      const entries = [
        { index: 1, label: 'Initial Task', turnId: 't1', target: fakeElement() },
        { index: 2, label: 'Follow-up', turnId: 't2', target: fakeElement() },
      ]

      render(<TurnTocList entries={entries} />)

      const list = document.querySelector('[data-turn-toc-list]')
      const orderedButtons = list?.querySelectorAll('[data-turn-toc-entry]') ?? []

      expect(orderedButtons).toHaveLength(2)
      const firstButton = orderedButtons[0] as HTMLElement
      expect(firstButton).toBeTruthy()
      expect(firstButton.getAttribute('data-turn-toc-entry-index')).toBe('1')
      expect(firstButton.textContent).toContain('Initial Task')

      const secondButton = orderedButtons[1] as HTMLElement
      expect(secondButton.getAttribute('data-turn-toc-entry-index')).toBe('2')
      expect(secondButton.textContent).toContain('Follow-up')
    })

    it('clicking an entry calls scrollIntoView on its target ref with smooth behavior', () => {
      const target = fakeElement()
      const entries = [
        { index: 1, label: 'Initial Task', turnId: 't1', target },
      ]

      render(<TurnTocList entries={entries} />)

      const button = document.querySelector('[data-turn-toc-entry]') as HTMLButtonElement
      fireEvent.click(button)

      expect(scrollIntoViewSpy).toHaveBeenCalledTimes(1)
      expect(scrollIntoViewSpy.mock.instances[0]).toBe(target)
      const arg = scrollIntoViewSpy.mock.calls[0]?.[0]
      expect(arg).toMatchObject({ behavior: 'smooth', block: 'start' })
    })

    it('boundary indices (first/last) are honored: clicking the first turn scrolls that ref', () => {
      const firstEl = fakeElement()
      const secondEl = fakeElement()
      const entries = [
        { index: 1, label: 'First', turnId: 't1', target: firstEl },
        { index: 2, label: 'Second', turnId: 't2', target: secondEl },
      ]

      render(<TurnTocList entries={entries} />)

      const firstButton = document.querySelector('[data-turn-toc-entry-index="1"]') as HTMLButtonElement
      fireEvent.click(firstButton)

      expect(scrollIntoViewSpy).toHaveBeenCalledTimes(1)
      expect(scrollIntoViewSpy.mock.instances[0]).toBe(firstEl)
    })

    it('boundary indices (first/last) are honored: clicking the last turn scrolls that ref', () => {
      const firstEl = fakeElement()
      const lastEl = fakeElement()
      const entries = [
        { index: 1, label: 'First', turnId: 't1', target: firstEl },
        { index: 2, label: 'Second', turnId: 't2', target: fakeElement() },
        { index: 3, label: 'Third', turnId: 't3', target: lastEl },
      ]

      render(<TurnTocList entries={entries} />)

      const lastButton = document.querySelector('[data-turn-toc-entry-index="3"]') as HTMLButtonElement
      fireEvent.click(lastButton)

      expect(scrollIntoViewSpy).toHaveBeenCalledTimes(1)
      expect(scrollIntoViewSpy.mock.instances[0]).toBe(lastEl)
    })

    it('does not throw and does not call scrollIntoView when the entry has no target ref', () => {
      const entries = [
        { index: 1, label: 'Initial Task', turnId: 't1', target: null },
      ]

      render(<TurnTocList entries={entries} />)

      const button = document.querySelector('[data-turn-toc-entry]') as HTMLButtonElement
      expect(() => fireEvent.click(button)).not.toThrow()
      expect(scrollIntoViewSpy).not.toHaveBeenCalled()
    })

    it('invokes the optional onActivate callback after scrolling', () => {
      const target = fakeElement()
      const entries = [
        { index: 1, label: 'Initial', turnId: 't1', target },
      ]
      const onActivate = vi.fn()

      render(<TurnTocList entries={entries} onActivate={onActivate} />)

      const button = document.querySelector('[data-turn-toc-entry]') as HTMLButtonElement
      fireEvent.click(button)

      expect(onActivate).toHaveBeenCalledWith(entries[0])
    })

    it('marks the active entry with aria-current when activeIndex matches', () => {
      const entries = [
        { index: 1, label: 'First', turnId: 't1', target: fakeElement() },
        { index: 2, label: 'Second', turnId: 't2', target: fakeElement() },
      ]

      render(<TurnTocList entries={entries} activeIndex={2} />)

      const secondButton = document.querySelector('[data-turn-toc-entry-index="2"]') as HTMLButtonElement
      expect(secondButton.getAttribute('aria-current')).toBe('true')

      const firstButton = document.querySelector('[data-turn-toc-entry-index="1"]') as HTMLButtonElement
      expect(firstButton.getAttribute('aria-current')).toBeNull()
    })

    it('renders an empty-state placeholder when entries is empty', () => {
      render(<TurnTocList entries={[]} emptyLabel="No turns yet" />)
      expect(screen.getByText('No turns yet')).toBeInTheDocument()
    })
  })

  describe('TurnTocRail', () => {
    it('renders an aside with the lg-only visibility class and a "Turns" header', () => {
      const entries = [
        { index: 1, label: 'Initial', turnId: 't1', target: fakeElement() },
      ]
      render(<TurnTocRail entries={entries} />)

      const rail = document.querySelector('[data-turn-toc-rail]')
      expect(rail).not.toBeNull()
      expect(rail?.className).toContain('hidden')
      expect(rail?.className).toContain('lg:block')
      expect(rail?.querySelector('nav')).not.toBeNull()
      expect(rail?.textContent).toContain('Turns')
    })

    it('embeds a TurnTocList inside a nav with the proper aria-label', () => {
      const entries = [
        { index: 1, label: 'Initial', turnId: 't1', target: fakeElement() },
      ]
      render(<TurnTocRail entries={entries} />)

      const nav = document.querySelector('nav[aria-label="Session transcript table of contents"]')
      expect(nav).not.toBeNull()
      expect(nav?.querySelector('[data-turn-toc-list]')).not.toBeNull()
    })
  })
})