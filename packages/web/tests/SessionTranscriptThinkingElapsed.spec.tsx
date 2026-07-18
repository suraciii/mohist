import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { screen, render, act } from './test-utils'
import { SessionTranscriptLayout } from '../src/widgets/session-transcript/ui/SessionTranscriptLayout'
import type { DisplayTurn } from '../src/widgets/session-transcript/model/session-transcript-display'

beforeEach(() => {
  vi.clearAllMocks()
})

afterEach(() => {
  vi.useRealTimers()
})

function makeTurn(overrides: {
  id?: string
  assistantParts?: DisplayTurn['assistantParts']
} = {}): DisplayTurn {
  return {
    id: overrides.id ?? 'turn-1',
    startedAt: '2026-06-12T00:00:00.000Z',
    completedAt: null,
    prompt: {
      role: 'mohist',
      text: 'do the thing',
      kind: 'task',
      sentAt: '2026-06-12T00:00:00.000Z',
    },
    assistantParts: overrides.assistantParts ?? [],
    changedFiles: [],
    state: 'idle',
  }
}

function findElapsed(container: HTMLElement) {
  return container.querySelector('[data-testid="transcript-thinking-elapsed"]')
}

describe('ThinkingPlaceholder — elapsed timer on thinking indicator', () => {
  describe('rendering — gated on isRunning && isThinking', () => {
    it('renders the thinking indicator with a ticking elapsed time while the session is running and in thinking', () => {
      const now = new Date('2026-06-12T00:00:04.700Z').getTime()

      const { container } = render(
        <SessionTranscriptLayout
          turns={[makeTurn()]}
          isRunning
          isStreaming={false}
          isThinking
          now={now}
        />,
      )

      const indicator = screen.getByTestId('transcript-thinking-indicator')
      expect(indicator).toBeInTheDocument()

      const elapsed = findElapsed(container)
      expect(elapsed).not.toBeNull()
      expect(elapsed?.getAttribute('data-elapsed-mode')).toBe('live')
      expect(elapsed?.textContent).toBe('0ms')
    })

    it('does not render the thinking indicator or its elapsed display when the session is not running', () => {
      const now = new Date('2026-06-12T00:00:04.700Z').getTime()

      const { container } = render(
        <SessionTranscriptLayout
          turns={[makeTurn()]}
          isRunning={false}
          isStreaming={false}
          isThinking
          now={now}
        />,
      )

      expect(screen.queryByTestId('transcript-thinking-indicator')).toBeNull()
      expect(findElapsed(container)).toBeNull()
    })

    it('does not render the thinking indicator when there are no turns', () => {
      const now = new Date('2026-06-12T00:00:04.700Z').getTime()

      const { container } = render(
        <SessionTranscriptLayout
          turns={[]}
          isRunning
          isStreaming={false}
          isThinking
          now={now}
        />,
      )

      expect(screen.queryByTestId('transcript-thinking-indicator')).toBeNull()
      expect(findElapsed(container)).toBeNull()
    })

    it('does not render the thinking indicator when isThinking is false even while running', () => {
      const now = new Date('2026-06-12T00:00:04.700Z').getTime()

      const { container } = render(
        <SessionTranscriptLayout
          turns={[makeTurn()]}
          isRunning
          isStreaming
          isThinking={false}
          now={now}
        />,
      )

      expect(screen.queryByTestId('transcript-thinking-indicator')).toBeNull()
      expect(findElapsed(container)).toBeNull()
    })
  })

  describe('ticking — once per second using vi.useFakeTimers', () => {
    beforeEach(() => {
      vi.useFakeTimers()
      vi.setSystemTime(new Date('2026-06-12T00:00:00.000Z'))
    })

    it('renders 0ms sub-second at the moment thinking begins', () => {
      const { container } = render(
        <SessionTranscriptLayout
          turns={[makeTurn()]}
          isRunning
          isStreaming={false}
          isThinking
        />,
      )

      const elapsed = findElapsed(container)
      expect(elapsed).not.toBeNull()
      expect(elapsed?.textContent).toBe('0ms')
    })

    it('advances the elapsed display once per second while still in thinking state', () => {
      const { container } = render(
        <SessionTranscriptLayout
          turns={[makeTurn()]}
          isRunning
          isStreaming={false}
          isThinking
        />,
      )

      const elapsed = findElapsed(container)
      expect(elapsed?.textContent).toBe('0ms')

      act(() => {
        vi.advanceTimersByTime(1000)
      })
      expect(findElapsed(container)?.textContent).toBe('1.0s')

      act(() => {
        vi.advanceTimersByTime(4000)
      })
      expect(findElapsed(container)?.textContent).toBe('5.0s')
    })

    it('formats elapsed time using the same rules as tool durations (seconds tier)', () => {
      const { container } = render(
        <SessionTranscriptLayout
          turns={[makeTurn()]}
          isRunning
          isStreaming={false}
          isThinking
        />,
      )

      act(() => {
        vi.advanceTimersByTime(59_000)
      })

      expect(findElapsed(container)?.textContent).toBe('59.0s')
    })

    it('formats elapsed time using the same rules as tool durations (minutes tier)', () => {
      const { container } = render(
        <SessionTranscriptLayout
          turns={[makeTurn()]}
          isRunning
          isStreaming={false}
          isThinking
        />,
      )

      act(() => {
        vi.advanceTimersByTime(125_000)
      })

      expect(findElapsed(container)?.textContent).toBe('2m 05s')
    })

    it('uses an injected `now` prop verbatim without auto-ticking for elapsed', () => {
      const fixedNow = new Date('2026-06-12T00:00:42.500Z').getTime()

      function Host({ isThinking, now }: { isThinking: boolean; now: number }) {
        return (
          <SessionTranscriptLayout
            turns={[makeTurn()]}
            isRunning
            isStreaming={false}
            isThinking={isThinking}
            now={now}
          />
        )
      }

      const { container, rerender } = render(<Host isThinking={false} now={fixedNow} />)
      rerender(<Host isThinking now={fixedNow} />)

      expect(findElapsed(container)?.textContent).toBe('0ms')

      act(() => {
        vi.advanceTimersByTime(60_000)
      })

      expect(findElapsed(container)?.textContent).toBe('0ms')
    })
  })

  describe('removal — when the session ends or the agent leaves the thinking state', () => {
    beforeEach(() => {
      vi.useFakeTimers()
      vi.setSystemTime(new Date('2026-06-12T00:00:00.000Z'))
    })

    it('removes the indicator and its elapsed display when the session ends mid-think', () => {
      function Host({ isRunning }: { isRunning: boolean }) {
        return (
          <SessionTranscriptLayout
            turns={[makeTurn()]}
            isRunning={isRunning}
            isStreaming={false}
            isThinking
          />
        )
      }

      const { container, rerender } = render(<Host isRunning />)

      expect(screen.getByTestId('transcript-thinking-indicator')).toBeInTheDocument()
      expect(findElapsed(container)).not.toBeNull()

      rerender(<Host isRunning={false} />)

      expect(screen.queryByTestId('transcript-thinking-indicator')).toBeNull()
      expect(findElapsed(container)).toBeNull()
    })

    it('removes the elapsed display when isThinking transitions out (visible content begins streaming)', () => {
      function Host({ isThinking, isStreaming }: { isThinking: boolean; isStreaming: boolean }) {
        return (
          <SessionTranscriptLayout
            turns={[makeTurn()]}
            isRunning
            isStreaming={isStreaming}
            isThinking={isThinking}
          />
        )
      }

      const { container, rerender } = render(<Host isThinking isStreaming={false} />)

      expect(findElapsed(container)).not.toBeNull()

      rerender(<Host isThinking={false} isStreaming />)

      expect(findElapsed(container)).toBeNull()
    })

    it('does not advance the elapsed display after isThinking transitions out', () => {
      function Host({ isThinking }: { isThinking: boolean }) {
        return (
          <SessionTranscriptLayout
            turns={[makeTurn()]}
            isRunning
            isStreaming={!isThinking}
            isThinking={isThinking}
          />
        )
      }

      const { container, rerender } = render(<Host isThinking />)

      act(() => {
        vi.advanceTimersByTime(3000)
      })
      expect(findElapsed(container)?.textContent).toBe('3.0s')

      rerender(<Host isThinking={false} />)
      expect(findElapsed(container)).toBeNull()

      act(() => {
        vi.advanceTimersByTime(10_000)
      })
      expect(findElapsed(container)).toBeNull()
    })

    it('recaptures a fresh start when isThinking comes back after a streaming interlude', () => {
      function Host({ isThinking }: { isThinking: boolean }) {
        return (
          <SessionTranscriptLayout
            turns={[makeTurn()]}
            isRunning
            isStreaming={!isThinking}
            isThinking={isThinking}
          />
        )
      }

      const { container, rerender } = render(<Host isThinking />)

      act(() => {
        vi.advanceTimersByTime(5000)
      })
      expect(findElapsed(container)?.textContent).toBe('5.0s')

      rerender(<Host isThinking={false} />)
      expect(findElapsed(container)).toBeNull()

      act(() => {
        vi.advanceTimersByTime(7000)
      })

      rerender(<Host isThinking />)

      const afterElapsed = findElapsed(container)
      expect(afterElapsed).not.toBeNull()
      expect(afterElapsed?.textContent).toBe('0ms')
    })
  })

  describe('thinking-start timestamp capture — ref on false→true, reset on true→false', () => {
    beforeEach(() => {
      vi.useFakeTimers()
      vi.setSystemTime(new Date('2026-06-12T00:00:00.000Z'))
    })

    it('captures a fresh start on a later false→true transition (not the page-load start)', () => {
      function Host({ isThinking }: { isThinking: boolean }) {
        return (
          <SessionTranscriptLayout
            turns={[makeTurn()]}
            isRunning
            isStreaming={false}
            isThinking={isThinking}
          />
        )
      }

      const { container, rerender } = render(<Host isThinking />)
      const initialStartText = findElapsed(container)?.textContent
      expect(initialStartText).toBe('0ms')

      rerender(<Host isThinking={false} />)
      expect(findElapsed(container)).toBeNull()

      act(() => {
        vi.advanceTimersByTime(7000)
      })
      rerender(<Host isThinking />)

      const afterElapsed = findElapsed(container)
      expect(afterElapsed).not.toBeNull()
      expect(afterElapsed?.textContent).toBe('0ms')
    })

    it('does not reset the start across interval ticks while isThinking stays true', () => {
      const { container } = render(
        <SessionTranscriptLayout
          turns={[makeTurn()]}
          isRunning
          isStreaming={false}
          isThinking
        />,
      )

      const firstText = findElapsed(container)?.textContent
      expect(firstText).toBe('0ms')

      act(() => {
        vi.advanceTimersByTime(1000)
      })
      expect(findElapsed(container)?.textContent).toBe('1.0s')

      act(() => {
        vi.advanceTimersByTime(4000)
      })
      expect(findElapsed(container)?.textContent).toBe('5.0s')

      act(() => {
        vi.advanceTimersByTime(5000)
      })
      expect(findElapsed(container)?.textContent).toBe('10.0s')
    })

    it('does not capture a thinking start on initial render when isThinking is already true and stays true', () => {
      const { container } = render(
        <SessionTranscriptLayout
          turns={[makeTurn()]}
          isRunning
          isStreaming={false}
          isThinking
        />,
      )

      const elapsed = findElapsed(container)
      expect(elapsed).not.toBeNull()
      expect(elapsed?.textContent).toBe('0ms')

      act(() => {
        vi.advanceTimersByTime(1000)
      })
      expect(findElapsed(container)?.textContent).toBe('1.0s')
    })
  })
})
