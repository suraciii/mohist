import '@testing-library/jest-dom'
import { fireEvent, render } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useState } from 'react'
import { SessionTranscriptLayout } from './SessionTranscriptLayout'
import type { DisplayTurn } from '../model/session-transcript-display'
import { setScopedValue, restoreScopedProperties } from '../../../../tests/support/scoped-property'

function makeTurn(overrides: {
  id?: string
  prompt?: Partial<DisplayTurn['prompt']>
  startedAt?: string
  completedAt?: string | null
  assistantParts?: DisplayTurn['assistantParts']
}): DisplayTurn {
  const startedAt = overrides.startedAt ?? '2024-05-15T10:00:00.000Z'
  return {
    id: overrides.id ?? 'turn-1',
    startedAt,
    completedAt: overrides.completedAt ?? null,
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

describe('SessionTranscriptLayout — flat single-column timeline', () => {
  let scrollIntoViewSpy: ReturnType<typeof vi.spyOn>

  beforeEach(() => {
    scrollIntoViewSpy = vi.spyOn(Element.prototype, 'scrollIntoView').mockImplementation(() => {})
  })

  afterEach(() => {
    scrollIntoViewSpy.mockRestore()
  })

  describe('flat single-column container', () => {
    it('renders a single full-width column with no two-column grid or max-width cap', () => {
      const turns: DisplayTurn[] = [makeTurn({ id: 'a' })]
      const { container } = render(
        <SessionTranscriptLayout turns={turns} isRunning={false} />,
      )

      expect(container.querySelector('[data-turn-toc-rail]')).toBeNull()
      expect(container.querySelector('[data-turn-toc-list]')).toBeNull()
      expect(container.querySelector('[data-transcript-toolbar]')).toBeNull()
      expect(container.querySelector('[data-transcript-toolbar-toc-trigger]')).toBeNull()

      const gridCandidates = Array.from(container.querySelectorAll('[class*="grid-cols"]'))
      expect(gridCandidates).toHaveLength(0)

      const maxWidthCandidates = Array.from(container.querySelectorAll('[class*="max-w-4xl"]'))
      expect(maxWidthCandidates).toHaveLength(0)
    })

    it('keeps the CopyFullTextButton in the column header for non-empty transcripts', () => {
      const turns: DisplayTurn[] = [makeTurn({ id: 'a' })]
      render(<SessionTranscriptLayout turns={turns} isRunning={false} />)

      const copyButtons = document.querySelectorAll('[data-copy-full-text]')
      expect(copyButtons).toHaveLength(1)
      expect((copyButtons[0] as HTMLButtonElement).disabled).toBe(false)
    })

    it('renders a mini-timeline rail that is hidden below xl and visible at xl+', () => {
      const turns: DisplayTurn[] = [
        makeTurn({ id: 'a' }),
        makeTurn({ id: 'b' }),
      ]
      const { container } = render(
        <SessionTranscriptLayout turns={turns} isRunning={false} />,
      )

      const rail = container.querySelector('[data-mini-timeline]') as HTMLElement
      expect(rail).not.toBeNull()
      expect(rail.className).toContain('hidden')
      expect(rail.className).toContain('xl:flex')
    })

    it('mounts the mini-timeline as a sibling of the transcript column inside a flex-row root at xl', () => {
      const turns: DisplayTurn[] = [makeTurn({ id: 'a' })]
      const { container } = render(
        <SessionTranscriptLayout turns={turns} isRunning={false} />,
      )

      const root = container.querySelector('[data-scrollable]') as HTMLElement
      expect(root).not.toBeNull()
      expect(root.className).toContain('xl:flex-row')

      const rail = root.querySelector(':scope > [data-mini-timeline]')
      expect(rail).not.toBeNull()
      const transcriptColumn = root.querySelector(':scope > [data-mini-timeline] + div')
      expect(transcriptColumn).not.toBeNull()
      expect(transcriptColumn!.className).toContain('flex-1')

      const turnList = transcriptColumn!.querySelector('[role="log"]')
      expect(turnList).not.toBeNull()
    })
  })

  describe('turn divider bar — ordinal, kind label, start time, duration', () => {
    it('renders a divider bar for every turn with ordinal, kind label, and start time', () => {
      const turns: DisplayTurn[] = [
        makeTurn({ id: 't1', prompt: { kind: 'initial' } }),
        makeTurn({ id: 't2', prompt: { kind: 'followup' } }),
        makeTurn({ id: 't3', prompt: { kind: 'task' } }),
      ]

      const { container } = render(
        <SessionTranscriptLayout turns={turns} isRunning={false} />,
      )

      const dividers = container.querySelectorAll('[data-turn-divider]')
      expect(dividers).toHaveLength(3)
      expect(dividers[0].getAttribute('data-turn-index')).toBe('1')
      expect(dividers[1].getAttribute('data-turn-index')).toBe('2')
      expect(dividers[2].getAttribute('data-turn-index')).toBe('3')

      const ordinals = container.querySelectorAll('[data-turn-index-label]')
      expect(ordinals[0].textContent).toBe('Turn 1')
      expect(ordinals[1].textContent).toBe('Turn 2')
      expect(ordinals[2].textContent).toBe('Turn 3')

      const kindLabels = container.querySelectorAll('[data-turn-kind-label]')
      expect(kindLabels[0].textContent).toBe('Initial Task')
      expect(kindLabels[1].textContent).toBe('Follow-up')
      expect(kindLabels[2].textContent).toBe('Task')

      const timestamps = container.querySelectorAll('[data-turn-timestamp]')
      expect(timestamps[0].getAttribute('datetime')).toBe(turns[0].startedAt)
      expect(timestamps[1].getAttribute('datetime')).toBe(turns[1].startedAt)
      expect(timestamps[2].getAttribute('datetime')).toBe(turns[2].startedAt)
    })

    it('shows duration on completed turns and omits it on running turns', () => {
      const turns: DisplayTurn[] = [
        makeTurn({
          id: 'completed',
          startedAt: '2024-05-15T10:00:00.000Z',
          completedAt: '2024-05-15T10:01:00.000Z',
          prompt: { kind: 'initial' },
        }),
        makeTurn({
          id: 'running',
          startedAt: '2024-05-15T10:02:00.000Z',
          completedAt: null,
          prompt: { kind: 'followup' },
        }),
      ]

      const { container } = render(
        <SessionTranscriptLayout turns={turns} isRunning />,
      )

      const durations = container.querySelectorAll('[data-turn-duration]')
      expect(durations).toHaveLength(1)
      expect(durations[0].textContent).toBe('1m 00s')
    })
  })

  describe('turn refs map is owned by the layout and registered by TurnList', () => {
    it('registers each turn element with its 1-based index via data-turn-ref', () => {
      const turns: DisplayTurn[] = [
        makeTurn({ id: 't1' }),
        makeTurn({ id: 't2' }),
        makeTurn({ id: 't3' }),
      ]

      render(<SessionTranscriptLayout turns={turns} isRunning={false} />)

      const turnRefs = document.querySelectorAll('[data-turn-ref]')
      expect(turnRefs).toHaveLength(3)
      expect(turnRefs[0].getAttribute('data-turn-id')).toBe('t1')
      expect(turnRefs[1].getAttribute('data-turn-id')).toBe('t2')
      expect(turnRefs[2].getAttribute('data-turn-id')).toBe('t3')
    })

    it('streaming-appended turns grow the turn refs map without dropping existing ids', () => {
      const initialTurns: DisplayTurn[] = [
        makeTurn({ id: 'a' }),
        makeTurn({ id: 'b' }),
      ]

      function AppendableTranscript() {
        const [turns, setTurns] = useState<DisplayTurn[]>(initialTurns)
        return (
          <div>
            <button data-testid="append" onClick={() => setTurns((prev) => [...prev, makeTurn({ id: `c-${prev.length + 1}` })])}>append</button>
            <SessionTranscriptLayout turns={turns} isRunning />
          </div>
        )
      }

      const { getByTestId } = render(<AppendableTranscript />)

      const beforeRefs = document.querySelectorAll('[data-turn-ref]')
      expect(beforeRefs).toHaveLength(2)
      const firstRefBefore = beforeRefs[0]

      fireEvent.click(getByTestId('append'))

      const afterRefs = document.querySelectorAll('[data-turn-ref]')
      expect(afterRefs).toHaveLength(3)
      expect(afterRefs[0]).toBe(firstRefBefore)
      expect(afterRefs[0].getAttribute('data-turn-id')).toBe('a')
    })
  })

  describe('responsive card classes', () => {
    it('does not render the legacy rounded-2xl bubble on the prompt block', () => {
      const turns: DisplayTurn[] = [makeTurn({ id: 'a', prompt: { kind: 'task', title: 'Hello' } })]
      const { container } = render(
        <SessionTranscriptLayout turns={turns} isRunning={false} />,
      )

      const promptBlock = container.querySelector('[data-prompt-block]')
      expect(promptBlock).not.toBeNull()
      expect(promptBlock!.className).not.toContain('rounded-2xl')
      expect(promptBlock!.className).not.toContain('justify-end')
      expect(promptBlock!.className).not.toContain('max-w-[80%]')
      expect(promptBlock!.className).not.toContain('max-w-[90%]')
    })

    it('assistant text parts render without a max-width cap', () => {
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

      const { container } = render(
        <SessionTranscriptLayout turns={turns} isRunning={false} />,
      )

      const found = Array.from(container.querySelectorAll('div')).find((el) =>
        !!el.querySelector('.transcript-md'),
      )
      expect(found).toBeTruthy()
      expect(found!.className).not.toContain('max-w-[80%]')
      expect(found!.className).not.toContain('max-w-[90%]')
    })

    it('the turn list role=log element has min-w-0', () => {
      const turns: DisplayTurn[] = [makeTurn({ id: 'a' })]
      render(<SessionTranscriptLayout turns={turns} isRunning={false} />)
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
      }

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
  let scrollIntoViewSpy: ReturnType<typeof vi.spyOn>

  beforeEach(() => {
    scrollIntoViewSpy = vi.spyOn(Element.prototype, 'scrollIntoView').mockImplementation(() => {})
  })

  afterEach(() => {
    scrollIntoViewSpy.mockRestore()
    restoreScopedProperties()
  })

  it.each([320, 375, 430])(
    'flat-column timeline protects long prompt/code content at %ipx width',
    (width) => {
      setScopedValue(window, 'innerWidth', width)

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
          isRunning={false}
        />,
      )

      const scrollable = view.container.querySelector('[data-scrollable]')
      expect(scrollable?.className).toContain('min-w-0')

      const promptBlock = view.container.querySelector('[data-prompt-block]')
      expect(promptBlock).not.toBeNull()
      expect(promptBlock!.className).not.toContain('rounded-2xl')
      expect(promptBlock!.className).not.toContain('max-w-[80%]')

      const turnList = view.container.querySelector('[role="log"]')
      expect(turnList?.className).toContain('min-w-0')
      expect(turnList?.className).not.toContain('max-w-2xl')

      const markdown = view.container.querySelector('.transcript-md')
      expect(markdown?.className).toContain('leading-relaxed')
      const assistantCard = markdown?.parentElement
      expect(assistantCard?.className).not.toContain('max-w-[80%]')
      expect(assistantCard?.className).not.toContain('max-w-[90%]')
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

    const { container } = render(<SessionTranscriptLayout turns={turns} isRunning={false} />)
    const pre = container.querySelector('.transcript-md pre')
    expect(pre).not.toBeNull()
    expect(pre?.className).toContain('max-w-full')
    expect(pre?.className).toContain('overflow-x-auto')
  })
})