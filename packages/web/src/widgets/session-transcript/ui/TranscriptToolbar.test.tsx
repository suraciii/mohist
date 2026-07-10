import '@testing-library/jest-dom'
import { fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { TranscriptToolbar } from './TranscriptToolbar'
import type { TurnTocEntry } from './TurnToc'

function makeEntries(count: number): TurnTocEntry[] {
  const targets = Array.from({ length: count }, () => document.createElement('div'))
  return targets.map((target, i) => ({
    index: i + 1,
    label: `Turn ${i + 1}`,
    turnId: `t${i + 1}`,
    target,
  }))
}

describe('TranscriptToolbar', () => {
  let scrollIntoViewSpy: ReturnType<typeof vi.spyOn>

  beforeEach(() => {
    scrollIntoViewSpy = vi.spyOn(Element.prototype, 'scrollIntoView').mockImplementation(() => {})
  })

  afterEach(() => {
    scrollIntoViewSpy.mockRestore()
  })

  it('mounts above the turn list on the main branch and renders the Turns disclosure trigger', () => {
    const entries = makeEntries(3)
    render(<TranscriptToolbar entries={entries} />)

    const toolbar = document.querySelector('[data-transcript-toolbar]')
    expect(toolbar).not.toBeNull()
    expect(toolbar?.className).toContain('lg:hidden')

    const trigger = document.querySelector('[data-transcript-toolbar-toc-trigger]')
    expect(trigger).not.toBeNull()
    expect(trigger?.textContent).toContain('Turns')
  })

  it('renders the entry count next to the trigger label', () => {
    const entries = makeEntries(4)
    render(<TranscriptToolbar entries={entries} />)

    const trigger = document.querySelector('[data-transcript-toolbar-toc-trigger]')
    expect(trigger?.textContent).toContain('(4)')
  })

  it('does not render an entry-count suffix when there are no entries', () => {
    render(<TranscriptToolbar entries={[]} />)
    const trigger = document.querySelector('[data-transcript-toolbar-toc-trigger]')
    expect(trigger?.textContent).not.toContain('(0)')
  })

  it('opens the overlay when the disclosure trigger is clicked', () => {
    const entries = makeEntries(2)
    render(<TranscriptToolbar entries={entries} />)

    const trigger = document.querySelector('[data-transcript-toolbar-toc-trigger]') as HTMLButtonElement
    expect(document.querySelector('[data-transcript-toolbar-toc-overlay]')).toBeNull()

    fireEvent.click(trigger)

    const overlay = document.querySelector('[data-transcript-toolbar-toc-overlay]')
    expect(overlay).not.toBeNull()
    expect(overlay?.querySelectorAll('[data-turn-toc-entry]')).toHaveLength(2)
  })

  it('toggles the overlay: a second click closes it', () => {
    const entries = makeEntries(2)
    render(<TranscriptToolbar entries={entries} />)
    const trigger = document.querySelector('[data-transcript-toolbar-toc-trigger]') as HTMLButtonElement

    fireEvent.click(trigger)
    expect(document.querySelector('[data-transcript-toolbar-toc-overlay]')).not.toBeNull()

    fireEvent.click(trigger)
    expect(document.querySelector('[data-transcript-toolbar-toc-overlay]')).toBeNull()
  })

  it('reflects the open state on the trigger via aria-expanded', () => {
    const entries = makeEntries(1)
    render(<TranscriptToolbar entries={entries} />)
    const trigger = document.querySelector('[data-transcript-toolbar-toc-trigger]') as HTMLButtonElement

    expect(trigger.getAttribute('aria-expanded')).toBe('false')
    fireEvent.click(trigger)
    expect(trigger.getAttribute('aria-expanded')).toBe('true')
  })

  it('closes the overlay on Escape and on outside click', () => {
    const entries = makeEntries(2)
    render(
      <div>
        <TranscriptToolbar entries={entries} />
        <button data-testid="outside">outside</button>
      </div>,
    )
    const trigger = document.querySelector('[data-transcript-toolbar-toc-trigger]') as HTMLButtonElement

    fireEvent.click(trigger)
    expect(document.querySelector('[data-transcript-toolbar-toc-overlay]')).not.toBeNull()

    fireEvent.keyDown(document, { key: 'Escape' })
    expect(document.querySelector('[data-transcript-toolbar-toc-overlay]')).toBeNull()

    fireEvent.click(trigger)
    expect(document.querySelector('[data-transcript-toolbar-toc-overlay]')).not.toBeNull()

    fireEvent.mouseDown(screen.getByTestId('outside'))
    expect(document.querySelector('[data-transcript-toolbar-toc-overlay]')).toBeNull()
  })

  it('clicking an entry inside the overlay scrolls its target and closes the overlay', () => {
    const entries = makeEntries(3)
    const onActivate = vi.fn()
    render(<TranscriptToolbar entries={entries} onTocEntryActivate={onActivate} />)

    const trigger = document.querySelector('[data-transcript-toolbar-toc-trigger]') as HTMLButtonElement
    fireEvent.click(trigger)

    const entry = document.querySelector('[data-turn-toc-entry-index="2"]') as HTMLButtonElement
    fireEvent.click(entry)

    expect(scrollIntoViewSpy).toHaveBeenCalledTimes(1)
    expect(scrollIntoViewSpy.mock.instances[0]).toBe(entries[1].target)
    expect(onActivate).toHaveBeenCalledWith(entries[1])
    expect(document.querySelector('[data-transcript-toolbar-toc-overlay]')).toBeNull()
  })

  it('renders a right slot for future actions (e.g. copy-full-text)', () => {
    const entries = makeEntries(1)
    render(
      <TranscriptToolbar
        entries={entries}
        rightSlot={<button data-testid="slot-button">copy</button>}
      />,
    )
    expect(screen.getByTestId('slot-button')).toBeInTheDocument()
  })

  it('highlights the active entry inside the overlay', () => {
    const entries = makeEntries(3)
    render(<TranscriptToolbar entries={entries} activeIndex={2} />)

    const trigger = document.querySelector('[data-transcript-toolbar-toc-trigger]') as HTMLButtonElement
    fireEvent.click(trigger)

    const activeButton = document.querySelector('[data-turn-toc-entry-index="2"]') as HTMLButtonElement
    expect(activeButton.getAttribute('aria-current')).toBe('true')
  })
})