// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { fireEvent, render } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useState } from 'react'
import { SessionTranscriptLayout } from './SessionTranscriptLayout'
import type { DisplayTurn } from '../model/session-transcript-display'

function makeTurn(overrides: {
  id?: string
  prompt?: Partial<DisplayTurn['prompt']>
  startedAt?: string
  assistantParts?: DisplayTurn['assistantParts']
}): DisplayTurn {
  const startedAt = overrides.startedAt ?? '2024-05-15T10:00:00.000Z'
  return {
    id: overrides.id ?? 'turn-1',
    startedAt,
    completedAt: null,
    prompt: {
      role: 'mohist',
      text: 'prompt body',
      kind: 'followup',
      sentAt: startedAt,
      ...overrides.prompt,
    },
    assistantParts: overrides.assistantParts ?? [],
    changedFiles: [],
    state: 'idle',
  }
}

describe('SessionTranscriptLayout TOC + toolbar + responsive integration', () => {
  let scrollIntoViewSpy: ReturnType<typeof vi.spyOn>
  const scrollContainerCleanups = new Set<() => void>()

  beforeEach(() => {
    scrollIntoViewSpy = vi.spyOn(Element.prototype, 'scrollIntoView').mockImplementation(() => {})
  })

  afterEach(() => {
    for (const dispose of [...scrollContainerCleanups]) dispose()
    scrollIntoViewSpy.mockRestore()
  })

  describe('TOC entry count matches rendered turn count and is keyed 1-based in document order', () => {
    it('lists N entries for N turns with the 1-based index', () => {
      const turns: DisplayTurn[] = [
        makeTurn({ id: 't1', prompt: { kind: 'initial', title: 'First' } }),
        makeTurn({ id: 't2', prompt: { kind: 'followup', title: 'Second' } }),
        makeTurn({ id: 't3', prompt: { kind: 'task', title: 'Third' } }),
      ]

      render(<SessionTranscriptLayout turns={turns} turnCount={turns.length} title="t" statusKind="completed" isRunning={false} />)

      const list = document.querySelector('[data-turn-toc-list]')
      const items = list?.querySelectorAll('[data-turn-toc-entry]') ?? []

      expect(items).toHaveLength(turns.length)
      expect(items[0].getAttribute('data-turn-toc-entry-index')).toBe('1')
      expect(items[1].getAttribute('data-turn-toc-entry-index')).toBe('2')
      expect(items[2].getAttribute('data-turn-toc-entry-index')).toBe('3')
    })

    it('renders zero entries for an empty turn list', () => {
      render(<SessionTranscriptLayout turns={[]} turnCount={0} title="t" statusKind="completed" isRunning={false} />)
      expect(document.querySelector('[data-turn-toc-list]')).toBeNull()
    })
  })

  describe('turn refs map is owned by the layout and registered by TurnList', () => {
    it('registers each turn element with its 1-based index', () => {
      const turns: DisplayTurn[] = [
        makeTurn({ id: 't1' }),
        makeTurn({ id: 't2' }),
        makeTurn({ id: 't3' }),
      ]

      render(<SessionTranscriptLayout turns={turns} turnCount={turns.length} title="t" statusKind="completed" isRunning={false} />)

      const turnRefs = document.querySelectorAll('[data-turn-ref]')
      expect(turnRefs).toHaveLength(3)

      const list = document.querySelector('[data-turn-toc-list]')
      const items = list?.querySelectorAll('[data-turn-toc-entry]') ?? []

      const firstButton = items[0] as HTMLButtonElement
      fireEvent.click(firstButton)

      expect(scrollIntoViewSpy).toHaveBeenCalledTimes(1)
      expect(scrollIntoViewSpy.mock.instances[0]).toBe(turnRefs[0])
    })

    it('clicking the TOC entry for turn K calls scrollIntoView on the K-th turn element', () => {
      const turns: DisplayTurn[] = [
        makeTurn({ id: 'a', prompt: { kind: 'task', title: 'A' } }),
        makeTurn({ id: 'b', prompt: { kind: 'task', title: 'B' } }),
        makeTurn({ id: 'c', prompt: { kind: 'task', title: 'C' } }),
      ]

      render(<SessionTranscriptLayout turns={turns} turnCount={turns.length} title="t" statusKind="completed" isRunning={false} />)

      const turnRefs = document.querySelectorAll('[data-turn-ref]')
      const list = document.querySelector('[data-turn-toc-list]')
      const secondButton = list?.querySelector('[data-turn-toc-entry-index="2"]') as HTMLButtonElement

      fireEvent.click(secondButton)

      expect(scrollIntoViewSpy).toHaveBeenCalledTimes(1)
      expect(scrollIntoViewSpy.mock.instances[0]).toBe(turnRefs[1])
    })

    it('boundary indices (first/last) are honored by scrollIntoView', () => {
      const turns: DisplayTurn[] = [
        makeTurn({ id: 'a' }),
        makeTurn({ id: 'b' }),
        makeTurn({ id: 'c' }),
      ]

      render(<SessionTranscriptLayout turns={turns} turnCount={turns.length} title="t" statusKind="completed" isRunning={false} />)

      const turnRefs = document.querySelectorAll('[data-turn-ref]')
      const list = document.querySelector('[data-turn-toc-list]')

      const firstButton = list?.querySelector('[data-turn-toc-entry-index="1"]') as HTMLButtonElement
      fireEvent.click(firstButton)
      expect(scrollIntoViewSpy.mock.instances[0]).toBe(turnRefs[0])

      const lastButton = list?.querySelector('[data-turn-toc-entry-index="3"]') as HTMLButtonElement
      fireEvent.click(lastButton)
      expect(scrollIntoViewSpy.mock.instances[scrollIntoViewSpy.mock.calls.length - 1]).toBe(turnRefs[2])
    })

    it('uses smooth scroll behavior and block:start on the target', () => {
      const turns: DisplayTurn[] = [makeTurn({ id: 'a' })]
      render(<SessionTranscriptLayout turns={turns} turnCount={1} title="t" statusKind="completed" isRunning={false} />)

      const list = document.querySelector('[data-turn-toc-list]')
      const button = list?.querySelector('[data-turn-toc-entry]') as HTMLButtonElement
      fireEvent.click(button)

      const arg = scrollIntoViewSpy.mock.calls[0]?.[0]
      expect(arg).toMatchObject({ behavior: 'smooth', block: 'start' })
    })
  })

  describe('streaming-appended turns appear in the TOC without remounting existing entries', () => {
    it('appends a new TOC entry when the turns array grows without a page reload', () => {
      const initialTurns: DisplayTurn[] = [
        makeTurn({ id: 'a' }),
        makeTurn({ id: 'b' }),
      ]

      function AppendableTranscript() {
        const [turns, setTurns] = useState<DisplayTurn[]>(initialTurns)
        return (
          <div>
            <button data-testid="append" onClick={() => setTurns((prev) => [...prev, makeTurn({ id: `c-${prev.length + 1}` })])}>append</button>
            <SessionTranscriptLayout turns={turns} turnCount={turns.length} title="t" statusKind="live" isRunning />
          </div>
        )
      }

      const { getByTestId } = render(<AppendableTranscript />)

      const listBefore = document.querySelector('[data-turn-toc-list]')
      const beforeItems = listBefore?.querySelectorAll('[data-turn-toc-entry]') ?? []
      expect(beforeItems).toHaveLength(2)

      const firstButtonBefore = beforeItems[0] as HTMLElement
      const firstIndexBefore = firstButtonBefore.getAttribute('data-turn-toc-entry-index')

      fireEvent.click(getByTestId('append'))

      const listAfter = document.querySelector('[data-turn-toc-list]')
      const afterItems = listAfter?.querySelectorAll('[data-turn-toc-entry]') ?? []
      expect(afterItems).toHaveLength(3)
      expect(afterItems[0]).toBe(firstButtonBefore)
      expect(afterItems[0].getAttribute('data-turn-toc-entry-index')).toBe(firstIndexBefore)
      expect(afterItems[2].getAttribute('data-turn-toc-entry-index')).toBe('3')
    })

    it('does not introduce any new TOC rail above the existing one on re-render', () => {
      function AppendableTranscript() {
        const [turns, setTurns] = useState<DisplayTurn[]>([makeTurn({ id: 'a' })])
        return (
          <div>
            <button data-testid="append" onClick={() => setTurns((prev) => [...prev, makeTurn({ id: `b-${prev.length}` })])}>append</button>
            <SessionTranscriptLayout turns={turns} turnCount={turns.length} title="t" statusKind="live" isRunning />
          </div>
        )
      }

      const { getByTestId } = render(<AppendableTranscript />)
      const railsBefore = document.querySelectorAll('[data-turn-toc-rail]').length

      fireEvent.click(getByTestId('append'))

      const railsAfter = document.querySelectorAll('[data-turn-toc-rail]').length
      expect(railsAfter).toBe(railsBefore)
    })
  })

  describe('lg+ two-column layout and below-lg toolbar trigger', () => {
    it('renders the rail with the lg-block class and hides it below lg', () => {
      const turns: DisplayTurn[] = [makeTurn({ id: 'a' })]
      render(<SessionTranscriptLayout turns={turns} turnCount={1} title="t" statusKind="completed" isRunning={false} />)

      const rail = document.querySelector('[data-turn-toc-rail]')
      expect(rail?.className).toContain('hidden')
      expect(rail?.className).toContain('lg:block')
    })

    it('renders the toolbar trigger below lg (with the lg:hidden class)', () => {
      const turns: DisplayTurn[] = [makeTurn({ id: 'a' })]
      render(<SessionTranscriptLayout turns={turns} turnCount={1} title="t" statusKind="completed" isRunning={false} />)

      const toolbar = document.querySelector('[data-transcript-toolbar]')
      expect(toolbar?.className).toContain('lg:hidden')
    })

    it('places the toolbar above the turn list (toolbar precedes turn list in document order)', () => {
      const turns: DisplayTurn[] = [makeTurn({ id: 'a' })]
      render(<SessionTranscriptLayout turns={turns} turnCount={1} title="t" statusKind="completed" isRunning={false} />)

      const toolbar = document.querySelector('[data-transcript-toolbar]')
      const firstTurn = document.querySelector('[data-turn-ref]')

      expect(toolbar).not.toBeNull()
      expect(firstTurn).not.toBeNull()

      const mask = (toolbar as Node).compareDocumentPosition(firstTurn as Node)
      expect(mask & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    })

    it('mounts the CopyFullTextButton inside the toolbar alongside the mobile TOC trigger', () => {
      const turns: DisplayTurn[] = [makeTurn({ id: 'a' })]
      render(<SessionTranscriptLayout turns={turns} turnCount={1} title="t" statusKind="completed" isRunning={false} />)

      const toolbar = document.querySelector('[data-transcript-toolbar]')
      expect(toolbar).not.toBeNull()
      const copyButton = toolbar!.querySelector('[data-copy-full-text]')
      const tocTrigger = toolbar!.querySelector('[data-transcript-toolbar-toc-trigger]')
      expect(copyButton).not.toBeNull()
      expect(tocTrigger).not.toBeNull()
    })

    it('also mounts a desktop-visible CopyFullTextButton in the lg+ TOC rail', () => {
      const turns: DisplayTurn[] = [makeTurn({ id: 'a' })]
      render(<SessionTranscriptLayout turns={turns} turnCount={1} title="t" statusKind="completed" isRunning={false} />)

      const rail = document.querySelector('[data-turn-toc-rail]')
      expect(rail).not.toBeNull()
      expect(rail?.className).toContain('lg:block')

      const copyButton = rail!.querySelector('[data-copy-full-text]') as HTMLButtonElement | null
      expect(copyButton).not.toBeNull()
      expect(copyButton?.disabled).toBe(false)
      expect(copyButton?.textContent).toBe('Copy')
    })

    it('keeps copy available in both viewport layouts for non-empty transcripts', () => {
      const turns: DisplayTurn[] = [makeTurn({ id: 'a' })]
      render(<SessionTranscriptLayout turns={turns} turnCount={1} title="t" statusKind="completed" isRunning={false} />)

      const copyButtons = document.querySelectorAll('[data-copy-full-text]')
      expect(copyButtons).toHaveLength(2)
      for (const button of Array.from(copyButtons) as HTMLButtonElement[]) {
        expect(button.disabled).toBe(false)
      }
    })

    it('renders the grid with the lg-only two-column measure on lg+', () => {
      const turns: DisplayTurn[] = [makeTurn({ id: 'a' })]
      const { container } = render(<SessionTranscriptLayout turns={turns} turnCount={1} title="t" statusKind="completed" isRunning={false} />)

      const grid = container.querySelector('[data-turn-toc-rail]')?.parentElement
      expect(grid?.className).toContain('lg:grid')
      expect(grid?.className).toContain('lg:grid-cols-[1fr_180px]')
    })

    it('mobile disclosure toggles the overlay that reuses the TurnTocList', () => {
      const turns: DisplayTurn[] = [makeTurn({ id: 'a' }), makeTurn({ id: 'b' })]
      render(<SessionTranscriptLayout turns={turns} turnCount={2} title="t" statusKind="completed" isRunning={false} />)

      const trigger = document.querySelector('[data-transcript-toolbar-toc-trigger]') as HTMLButtonElement
      expect(document.querySelector('[data-transcript-toolbar-toc-overlay]')).toBeNull()

      fireEvent.click(trigger)

      const overlay = document.querySelector('[data-transcript-toolbar-toc-overlay]')
      expect(overlay).not.toBeNull()
      expect(overlay?.querySelector('[data-turn-toc-list]')).not.toBeNull()
      expect(overlay?.querySelectorAll('[data-turn-toc-entry]')).toHaveLength(2)

      fireEvent.click(trigger)
      expect(document.querySelector('[data-transcript-toolbar-toc-overlay]')).toBeNull()
    })
  })

  describe('responsive card classes', () => {
    it('prompt cards use max-w-[90%] sm:max-w-[80%] with min-w-0 on the card parent', () => {
      const turns: DisplayTurn[] = [makeTurn({ id: 'a', prompt: { kind: 'task', title: 'Hello' } })]
      const { container } = render(<SessionTranscriptLayout turns={turns} turnCount={1} title="t" statusKind="completed" isRunning={false} />)

      const promptBubble = container.querySelector('.rounded-2xl')
      expect(promptBubble).not.toBeNull()
      const promptClass = promptBubble!.className
      expect(promptClass).toContain('max-w-[90%]')
      expect(promptClass).toContain('sm:max-w-[80%]')
      expect(promptClass).toContain('min-w-0')

      const promptOuter = promptBubble!.parentElement
      expect(promptOuter?.className).toContain('min-w-0')
    })

    it('assistant text parts use max-w-[90%] sm:max-w-[80%] with min-w-0', () => {
      const turns: DisplayTurn[] = [
        makeTurn({
          id: 'a',
          assistantParts: [
            {
              id: 'p1',
              partType: 'text',
              text: 'Hello, world.',
              startedAt: '2024-05-15T10:00:01.000Z',
              completedAt: '2024-05-15T10:00:02.000Z',
            },
          ],
        }),
      ]

      const { container } = render(<SessionTranscriptLayout turns={turns} turnCount={1} title="t" statusKind="completed" isRunning={false} />)

      const found = Array.from(container.querySelectorAll('div')).find((el) =>
        el.classList?.contains('max-w-[90%]') &&
        el.classList?.contains('sm:max-w-[80%]') &&
        el.classList?.contains('min-w-0') &&
        !!el.querySelector('.transcript-md')
      )
      expect(found).toBeTruthy()
      expect(found!.className).toContain('max-w-[90%]')
      expect(found!.className).toContain('sm:max-w-[80%]')
      expect(found!.className).toContain('min-w-0')
    })

    it('the turn list role=log element has min-w-0', () => {
      const turns: DisplayTurn[] = [makeTurn({ id: 'a' })]
      render(<SessionTranscriptLayout turns={turns} turnCount={1} title="t" statusKind="completed" isRunning={false} />)
      const log = document.querySelector('[role="log"]')
      expect(log?.className).toContain('min-w-0')
    })
  })

  describe('useTurnKeyboardNav wiring into SessionTranscriptLayout', () => {
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

    function renderWithScrollContainer({
      turns,
      containerTop = 0,
      turnTops,
    }: {
      turns: DisplayTurn[]
      containerTop?: number
      turnTops: number[]
    }) {
      const scrollContainer = document.createElement('div')
      document.body.appendChild(scrollContainer)
      const scrollContainerRef = { current: scrollContainer }

      const rectMap = new Map<Element, DOMRect>()
      rectMap.set(scrollContainer, makeRect(containerTop, 800))

      vi.spyOn(Element.prototype, 'getBoundingClientRect').mockImplementation(function (this: Element) {
        return rectMap.get(this) ?? makeRect(0, 0)
      })

      const view = render(
        <SessionTranscriptLayout
          turns={turns}
          turnCount={turns.length}
          title="t"
          statusKind="completed"
          isRunning={false}
          scrollContainerRef={scrollContainerRef}
        />,
      )

      const turnRefs = Array.from(document.querySelectorAll<HTMLDivElement>('[data-turn-ref]'))
      turnRefs.forEach((el, i) => {
        rectMap.set(el, makeRect(turnTops[i] ?? 1000 * (i + 1), 200))
      })

      let mounted = true
      const unmount = () => {
        if (!mounted) return
        mounted = false
        view.unmount()
        scrollContainer.remove()
        scrollContainerCleanups.delete(unmount)
      }
      scrollContainerCleanups.add(unmount)

      return {
        scrollContainer,
        scrollContainerRef,
        turnRefs,
        unmount,
      }
    }

    it('fires keydown from window and scrolls to the next turn', () => {
      const turns: DisplayTurn[] = [
        makeTurn({ id: 'a' }),
        makeTurn({ id: 'b' }),
        makeTurn({ id: 'c' }),
      ]
      const { turnRefs } = renderWithScrollContainer({
        turns,
        turnTops: [50, 1100, 2100],
      })

      fireEvent.keyDown(window, { key: 'j' })

      expect(scrollIntoViewSpy).toHaveBeenCalledTimes(1)
      expect(scrollIntoViewSpy.mock.instances[0]).toBe(turnRefs[1])
    })

    it('fires keydown from document body and scrolls to the next turn', () => {
      const turns: DisplayTurn[] = [
        makeTurn({ id: 'a' }),
        makeTurn({ id: 'b' }),
      ]
      const { turnRefs } = renderWithScrollContainer({
        turns,
        turnTops: [50, 1100],
      })

      fireEvent.keyDown(document.body, { key: 'j' })

      expect(scrollIntoViewSpy).toHaveBeenCalledTimes(1)
      expect(scrollIntoViewSpy.mock.instances[0]).toBe(turnRefs[1])
    })

    it('detaches the listener when the layout unmounts', () => {
      const turns: DisplayTurn[] = [makeTurn({ id: 'a' })]
      const { unmount } = renderWithScrollContainer({
        turns,
        turnTops: [50],
      })

      fireEvent.keyDown(window, { key: 'g' })
      expect(scrollIntoViewSpy).toHaveBeenCalledTimes(1)

      unmount()

      fireEvent.keyDown(window, { key: 'g' })
      expect(scrollIntoViewSpy).toHaveBeenCalledTimes(1)
    })

    it('respects the focus-deferral contract when the followup composer is focused', () => {
      const turns: DisplayTurn[] = [makeTurn({ id: 'a' })]
      const { scrollContainer } = renderWithScrollContainer({
        turns,
        turnTops: [50],
      })

      const textarea = document.createElement('textarea')
      scrollContainer.appendChild(textarea)
      textarea.focus()

      fireEvent.keyDown(window, { key: 'g' })
      fireEvent.keyDown(window, { key: 'G' })
      fireEvent.keyDown(window, { key: 'j' })
      fireEvent.keyDown(window, { key: 'k' })
      expect(scrollIntoViewSpy).not.toHaveBeenCalled()
    })
  })
})

describe('SessionTranscriptLayout narrow viewport no-overflow integration', () => {
  let originalInnerWidth: number
  let scrollIntoViewSpy: ReturnType<typeof vi.spyOn>

  beforeEach(() => {
    originalInnerWidth = window.innerWidth
    scrollIntoViewSpy = vi.spyOn(Element.prototype, 'scrollIntoView').mockImplementation(() => {})
  })

  afterEach(() => {
    Object.defineProperty(window, 'innerWidth', { writable: true, configurable: true, value: originalInnerWidth })
    scrollIntoViewSpy.mockRestore()
  })

  function renderLongLineTranscript(width: number) {
    Object.defineProperty(window, 'innerWidth', { writable: true, configurable: true, value: width })
    const turns: DisplayTurn[] = Array.from({ length: 3 }, (_, i) => makeTurn({
      id: `t${i + 1}`,
      prompt: {
        kind: 'task',
        title: 'A long prompt title without spaces'.repeat(8),
        text: 'Long line content '.repeat(40),
      },
      assistantParts: [
        {
          id: `p${i + 1}`,
          partType: 'text',
          text: 'A'.repeat(200),
          startedAt: '2024-05-15T10:00:01.000Z',
          completedAt: '2024-05-15T10:00:02.000Z',
        },
      ],
    }))

    const view = render(
      <SessionTranscriptLayout
        turns={turns}
        turnCount={turns.length}
        title="long-line fixture"
        statusKind="completed"
        isRunning={false}
      />,
    )

    const scrollable = view.container.querySelector('[data-scrollable]') as HTMLElement | null
    expect(scrollable).not.toBeNull()
    return { view, scrollable: scrollable as HTMLElement }
  }

  it.each([320, 375, 430])(
    'className contract protects long prompt/card/code content at %ipx',
    (width) => {
      const { view } = renderLongLineTranscript(width)
      const scrollable = view.container.querySelector('[data-scrollable]')
      expect(scrollable?.className).toContain('min-w-0')

      const promptBubble = view.container.querySelector('.rounded-2xl')
      expect(promptBubble).not.toBeNull()
      expect(promptBubble!.className).toContain('max-w-[90%]')
      expect(promptBubble!.className).toContain('sm:max-w-[80%]')
      expect(promptBubble!.className).toContain('min-w-0')

      const promptParent = promptBubble!.parentElement
      expect(promptParent?.className).toContain('min-w-0')

      const turnList = view.container.querySelector('[role="log"]')
      expect(turnList?.className).toContain('min-w-0')

      const railParent = view.container.querySelector('[data-turn-toc-rail]')?.parentElement
      expect(railParent?.className).toContain('lg:grid-cols-[1fr_180px]')
      expect(railParent?.className).toContain('lg:max-w-4xl')

      const markdown = view.container.querySelector('.transcript-md')
      expect(markdown?.className).toContain('leading-relaxed')
      const assistantCard = markdown?.parentElement
      expect(assistantCard?.className).toContain('max-w-[90%]')
      expect(assistantCard?.className).toContain('sm:max-w-[80%]')
      expect(assistantCard?.className).toContain('min-w-0')
    },
  )

  it('fenced assistant code blocks carry max-width and horizontal-scroll classes for long lines', () => {
    const turns: DisplayTurn[] = [makeTurn({
      id: 'code',
      assistantParts: [
        {
          id: 'code-part',
          partType: 'text',
          text: '```ts\nconst value = "' + 'x'.repeat(160) + '"\n```',
          startedAt: '2024-05-15T10:00:01.000Z',
          completedAt: '2024-05-15T10:00:02.000Z',
        },
      ],
    })]

    const { container } = render(<SessionTranscriptLayout turns={turns} turnCount={1} title="t" statusKind="completed" isRunning={false} />)
    const pre = container.querySelector('.transcript-md pre')
    expect(pre).not.toBeNull()
    expect(pre?.className).toContain('max-w-full')
    expect(pre?.className).toContain('overflow-x-auto')
  })
})
