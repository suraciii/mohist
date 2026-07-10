// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { act, fireEvent, render } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useTurnKeyboardNav } from './useTurnKeyboardNav'

function makeRect(top: number, height = 200): DOMRect {
  return {
    top,
    left: 0,
    right: 0,
    bottom: top + height,
    width: 0,
    height,
    x: 0,
    y: top,
    toJSON: () => ({}),
  } as DOMRect
}

interface SetupOptions {
  turnCount: number
  containerTop?: number
  turnTops: number[]
}

interface SetupResult {
  container: HTMLDivElement
  turnRefs: Map<number, HTMLDivElement>
  unmount: () => void
}

const harnessCleanups = new Set<() => void>()

function dispatchPageKeyDown(key: string, init: KeyboardEventInit = {}) {
  fireEvent.keyDown(window, { key, ...init })
}

function setupHarness(options: SetupOptions): SetupResult {
  const container = document.createElement('div')
  document.body.appendChild(container)
  const turnRefs = new Map<number, HTMLDivElement>()

  const rectMap = new Map<Element, DOMRect>()
  rectMap.set(container, makeRect(options.containerTop ?? 0, 800))

  for (let i = 0; i < options.turnCount; i++) {
    const el = document.createElement('div')
    rectMap.set(el, makeRect(options.turnTops[i] ?? 1000 * (i + 1), 200))
    turnRefs.set(i + 1, el)
  }

  vi.spyOn(Element.prototype, 'getBoundingClientRect').mockImplementation(function (this: Element) {
    const rect = rectMap.get(this)
    return rect ?? makeRect(0, 0)
  })

  const scrollContainerRef = { current: container }

  function Harness() {
    useTurnKeyboardNav({
      scrollContainerRef,
      turnRefs,
      turnCount: options.turnCount,
    })
    return null
  }

  const view = render(<Harness />)
  let mounted = true
  const unmount = () => {
    if (!mounted) return
    mounted = false
    view.unmount()
    container.remove()
    harnessCleanups.delete(unmount)
  }
  harnessCleanups.add(unmount)

  return {
    container,
    turnRefs,
    unmount,
  }
}

describe('useTurnKeyboardNav', () => {
  let scrollIntoViewSpy: ReturnType<typeof vi.spyOn>

  beforeEach(() => {
    if (document.activeElement instanceof HTMLElement) {
      document.activeElement.blur()
    }
    scrollIntoViewSpy = vi.spyOn(Element.prototype, 'scrollIntoView').mockImplementation(() => {})
  })

  afterEach(() => {
    for (const dispose of [...harnessCleanups]) dispose()
    scrollIntoViewSpy.mockRestore()
    vi.restoreAllMocks()
    if (document.activeElement instanceof HTMLElement) {
      document.activeElement.blur()
    }
  })

  describe('j moves to the next turn', () => {
    it('scrolls to ref K+1 when current turn is K=1 (only turn 1 above threshold)', () => {
      const { turnRefs } = setupHarness({
        turnCount: 3,
        containerTop: 0,
        turnTops: [50, 1100, 2100],
      })

      dispatchPageKeyDown('j')

      expect(scrollIntoViewSpy).toHaveBeenCalledTimes(1)
      expect(scrollIntoViewSpy.mock.instances[0]).toBe(turnRefs.get(2))
    })

    it('scrolls to ref K+1 when current turn is K=2 (turns 1-2 above threshold)', () => {
      const { turnRefs } = setupHarness({
        turnCount: 3,
        containerTop: 0,
        turnTops: [-500, 50, 1100],
      })

      dispatchPageKeyDown('j')

      expect(scrollIntoViewSpy).toHaveBeenCalledTimes(1)
      expect(scrollIntoViewSpy.mock.instances[0]).toBe(turnRefs.get(3))
    })

    it('calls scrollIntoView({ block: "start" }) on the target ref', () => {
      setupHarness({
        turnCount: 2,
        containerTop: 0,
        turnTops: [50, 1100],
      })

      dispatchPageKeyDown('j')

      const arg = scrollIntoViewSpy.mock.calls[0]?.[0]
      expect(arg).toMatchObject({ block: 'start' })
    })
  })

  describe('k moves to the previous turn', () => {
    it('scrolls to ref K-1 when current turn is K=2', () => {
      const { turnRefs } = setupHarness({
        turnCount: 3,
        containerTop: 0,
        turnTops: [-500, 50, 1100],
      })

      dispatchPageKeyDown('k')

      expect(scrollIntoViewSpy).toHaveBeenCalledTimes(1)
      expect(scrollIntoViewSpy.mock.instances[0]).toBe(turnRefs.get(1))
    })

    it('scrolls to ref K-1 when current turn is K=3 (all turns above threshold)', () => {
      const { turnRefs } = setupHarness({
        turnCount: 3,
        containerTop: 0,
        turnTops: [-1500, -500, 50],
      })

      dispatchPageKeyDown('k')

      expect(scrollIntoViewSpy).toHaveBeenCalledTimes(1)
      expect(scrollIntoViewSpy.mock.instances[0]).toBe(turnRefs.get(2))
    })
  })

  describe('boundary clamping (no-op at ends)', () => {
    it('j at the last turn is a no-op (does not scroll past the last turn)', () => {
      setupHarness({
        turnCount: 3,
        containerTop: 0,
        turnTops: [-1500, -500, 50],
      })

      dispatchPageKeyDown('j')

      expect(scrollIntoViewSpy).not.toHaveBeenCalled()
    })

    it('k at the first turn is a no-op (does not scroll before the first turn)', () => {
      setupHarness({
        turnCount: 3,
        containerTop: 0,
        turnTops: [50, 1100, 2100],
      })

      dispatchPageKeyDown('k')

      expect(scrollIntoViewSpy).not.toHaveBeenCalled()
    })
  })

  describe('g and G move to transcript boundaries', () => {
    it('g (no shift) scrolls to the first turn regardless of current position', () => {
      const { turnRefs } = setupHarness({
        turnCount: 3,
        containerTop: 0,
        turnTops: [-1500, -500, 50],
      })

      dispatchPageKeyDown('g')

      expect(scrollIntoViewSpy).toHaveBeenCalledTimes(1)
      expect(scrollIntoViewSpy.mock.instances[0]).toBe(turnRefs.get(1))
    })

    it('G (uppercase) scrolls to the last turn', () => {
      const { turnRefs } = setupHarness({
        turnCount: 3,
        containerTop: 0,
        turnTops: [50, 1100, 2100],
      })

      dispatchPageKeyDown('G')

      expect(scrollIntoViewSpy).toHaveBeenCalledTimes(1)
      expect(scrollIntoViewSpy.mock.instances[0]).toBe(turnRefs.get(3))
    })

    it('shift+g (key=g with shiftKey=true) scrolls to the last turn', () => {
      const { turnRefs } = setupHarness({
        turnCount: 3,
        containerTop: 0,
        turnTops: [50, 1100, 2100],
      })

      dispatchPageKeyDown('g', { shiftKey: true })

      expect(scrollIntoViewSpy).toHaveBeenCalledTimes(1)
      expect(scrollIntoViewSpy.mock.instances[0]).toBe(turnRefs.get(3))
    })

    it('g and G both target the same turn when there is only one turn', () => {
      const { turnRefs } = setupHarness({
        turnCount: 1,
        containerTop: 0,
        turnTops: [50],
      })

      dispatchPageKeyDown('g')
      expect(scrollIntoViewSpy.mock.instances[0]).toBe(turnRefs.get(1))

      dispatchPageKeyDown('G')
      expect(scrollIntoViewSpy.mock.instances[scrollIntoViewSpy.mock.calls.length - 1]).toBe(turnRefs.get(1))
    })
  })

  describe('focus deferral', () => {
    it('does not navigate when a textarea is focused (j)', () => {
      const { container } = setupHarness({
        turnCount: 3,
        containerTop: 0,
        turnTops: [50, 1100, 2100],
      })

      const textarea = document.createElement('textarea')
      container.appendChild(textarea)
      textarea.focus()

      expect(document.activeElement).toBe(textarea)

      dispatchPageKeyDown('j')
      expect(scrollIntoViewSpy).not.toHaveBeenCalled()
    })

    it('does not navigate when an input is focused (k)', () => {
      const { container } = setupHarness({
        turnCount: 3,
        containerTop: 0,
        turnTops: [-500, 50, 1100],
      })

      const input = document.createElement('input')
      container.appendChild(input)
      input.focus()

      expect(document.activeElement).toBe(input)

      dispatchPageKeyDown('k')
      expect(scrollIntoViewSpy).not.toHaveBeenCalled()
    })

    it('does not navigate when a select is focused (g)', () => {
      const { container } = setupHarness({
        turnCount: 3,
        containerTop: 0,
        turnTops: [50, 1100, 2100],
      })

      const select = document.createElement('select')
      container.appendChild(select)
      select.focus()

      expect(document.activeElement).toBe(select)

      dispatchPageKeyDown('g')
      expect(scrollIntoViewSpy).not.toHaveBeenCalled()
    })

    it('does not navigate when a [contenteditable] element is focused (G)', () => {
      const { container } = setupHarness({
        turnCount: 3,
        containerTop: 0,
        turnTops: [50, 1100, 2100],
      })

      const editable = document.createElement('div')
      editable.setAttribute('contenteditable', 'true')
      editable.tabIndex = 0
      container.appendChild(editable)
      editable.focus()

      expect(document.activeElement).toBe(editable)

      dispatchPageKeyDown('G')
      expect(scrollIntoViewSpy).not.toHaveBeenCalled()
    })

    it('does not navigate when a [contenteditable] element without an explicit true value is focused', () => {
      const { container } = setupHarness({
        turnCount: 3,
        containerTop: 0,
        turnTops: [50, 1100, 2100],
      })

      const editable = document.createElement('div')
      editable.setAttribute('contenteditable', '')
      editable.tabIndex = 0
      container.appendChild(editable)
      editable.focus()

      expect(document.activeElement).toBe(editable)

      dispatchPageKeyDown('j')
      expect(scrollIntoViewSpy).not.toHaveBeenCalled()
    })

    it('does not navigate when a [data-composer-input] element is focused (j)', () => {
      const { container } = setupHarness({
        turnCount: 3,
        containerTop: 0,
        turnTops: [50, 1100, 2100],
      })

      const composer = document.createElement('div')
      composer.setAttribute('data-composer-input', '')
      composer.tabIndex = 0
      container.appendChild(composer)
      composer.focus()

      expect(document.activeElement).toBe(composer)

      dispatchPageKeyDown('j')
      expect(scrollIntoViewSpy).not.toHaveBeenCalled()
    })

    it('does not navigate when an editable nested inside a sibling is focused', () => {
      const { container } = setupHarness({
        turnCount: 3,
        containerTop: 0,
        turnTops: [50, 1100, 2100],
      })

      const sibling = document.createElement('div')
      const nested = document.createElement('textarea')
      sibling.appendChild(nested)
      container.appendChild(sibling)
      nested.focus()

      expect(document.activeElement).toBe(nested)

      dispatchPageKeyDown('j')
      expect(scrollIntoViewSpy).not.toHaveBeenCalled()
    })

    it('navigates once focus is cleared from the editable', () => {
      const { container, turnRefs } = setupHarness({
        turnCount: 3,
        containerTop: 0,
        turnTops: [50, 1100, 2100],
      })

      const textarea = document.createElement('textarea')
      container.appendChild(textarea)
      textarea.focus()

      expect(document.activeElement).toBe(textarea)

      dispatchPageKeyDown('j')
      expect(scrollIntoViewSpy).not.toHaveBeenCalled()

      textarea.blur()
      expect(document.activeElement).not.toBe(textarea)

      dispatchPageKeyDown('j')
      expect(scrollIntoViewSpy).toHaveBeenCalledTimes(1)
      expect(scrollIntoViewSpy.mock.instances[0]).toBe(turnRefs.get(2))
    })
  })

  describe('modifier-key suppression', () => {
    it('does not navigate when metaKey is held (j)', () => {
      setupHarness({
        turnCount: 3,
        containerTop: 0,
        turnTops: [50, 1100, 2100],
      })

      dispatchPageKeyDown('j', { metaKey: true })
      expect(scrollIntoViewSpy).not.toHaveBeenCalled()
    })

    it('does not navigate when ctrlKey is held (k)', () => {
      setupHarness({
        turnCount: 3,
        containerTop: 0,
        turnTops: [-500, 50, 1100],
      })

      dispatchPageKeyDown('k', { ctrlKey: true })
      expect(scrollIntoViewSpy).not.toHaveBeenCalled()
    })

    it('does not navigate when altKey is held (g)', () => {
      setupHarness({
        turnCount: 3,
        containerTop: 0,
        turnTops: [50, 1100, 2100],
      })

      dispatchPageKeyDown('g', { altKey: true })
      expect(scrollIntoViewSpy).not.toHaveBeenCalled()
    })

    it('does not navigate when altKey+shift is held on G', () => {
      setupHarness({
        turnCount: 3,
        containerTop: 0,
        turnTops: [50, 1100, 2100],
      })

      dispatchPageKeyDown('G', { altKey: true })
      expect(scrollIntoViewSpy).not.toHaveBeenCalled()
    })
  })

  describe('current-turn derivation uses getBoundingClientRect on demand', () => {
    it('derives currentIndex as 0 when no turn is above the threshold', () => {
      const { turnRefs } = setupHarness({
        turnCount: 3,
        containerTop: 0,
        turnTops: [500, 1500, 2500],
      })

      dispatchPageKeyDown('j')

      expect(scrollIntoViewSpy.mock.instances[0]).toBe(turnRefs.get(1))
    })

    it('derives currentIndex as the LAST turn whose top is at or above the threshold (j scrolls to next)', () => {
      const { turnRefs } = setupHarness({
        turnCount: 5,
        containerTop: 0,
        turnTops: [-500, -300, 100, 1500, 2500],
      })

      dispatchPageKeyDown('j')

      expect(scrollIntoViewSpy.mock.instances[0]).toBe(turnRefs.get(4))
    })

    it('honors the scroll container offset (containerTop > 0)', () => {
      const { turnRefs } = setupHarness({
        turnCount: 3,
        containerTop: 200,
        turnTops: [180, 290, 1500],
      })

      dispatchPageKeyDown('j')

      expect(scrollIntoViewSpy.mock.instances[0]).toBe(turnRefs.get(3))
    })
  })

  describe('listener lifecycle', () => {
    it('detaches the listener on unmount', () => {
      const { unmount } = setupHarness({
        turnCount: 3,
        containerTop: 0,
        turnTops: [50, 1100, 2100],
      })

      dispatchPageKeyDown('j')
      expect(scrollIntoViewSpy).toHaveBeenCalledTimes(1)

      unmount()

      dispatchPageKeyDown('j')
      expect(scrollIntoViewSpy).toHaveBeenCalledTimes(1)
    })
  })

  describe('edge cases', () => {
    it('is a no-op when turnCount is 0', () => {
      setupHarness({
        turnCount: 0,
        containerTop: 0,
        turnTops: [],
      })

      dispatchPageKeyDown('j')
      dispatchPageKeyDown('k')
      dispatchPageKeyDown('g')
      dispatchPageKeyDown('G')
      expect(scrollIntoViewSpy).not.toHaveBeenCalled()
    })

    it('is a no-op on unrelated keys', () => {
      setupHarness({
        turnCount: 3,
        containerTop: 0,
        turnTops: [50, 1100, 2100],
      })

      dispatchPageKeyDown('a')
      dispatchPageKeyDown('Enter')
      dispatchPageKeyDown('ArrowDown')
      dispatchPageKeyDown('?')
      expect(scrollIntoViewSpy).not.toHaveBeenCalled()
    })
  })

  describe('fallback to window when scrollContainerRef.current is null', () => {
    it('attaches the listener to window and still responds to j', () => {
      vi.spyOn(Element.prototype, 'getBoundingClientRect').mockImplementation(function (this: Element) {
        return makeRect(0, 0)
      })

      const turnEl = document.createElement('div')
      const scrollIntoViewElSpy = vi.fn()
      turnEl.scrollIntoView = scrollIntoViewElSpy

      const turnRefs = new Map<number, HTMLDivElement>()
      turnRefs.set(1, turnEl)

      const ref = { current: null as HTMLDivElement | null }

      function Harness() {
        useTurnKeyboardNav({
          scrollContainerRef: ref,
          turnRefs,
          turnCount: 1,
        })
        return null
      }

      render(<Harness />)

      act(() => {
        fireEvent.keyDown(window, { key: 'g' })
      })

      expect(scrollIntoViewElSpy).toHaveBeenCalledTimes(1)
    })
  })
})
