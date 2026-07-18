import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { screen, render } from './test-utils'
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
}): DisplayTurn {
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

function makeStreamingTextPart(overrides: { id?: string; completedAt?: string | null; isStreaming?: boolean } = {}): DisplayTurn['assistantParts'][number] {
  return {
    id: overrides.id ?? 'text-stream',
    partType: 'text',
    text: 'partial output',
    startedAt: '2026-06-12T00:00:01.000Z',
    completedAt: overrides.completedAt ?? null,
    isStreaming: overrides.isStreaming ?? true,
  } as DisplayTurn['assistantParts'][number]
}

describe('AssistantTextPartView — block cursor on streaming text', () => {
  it('renders a block cursor at the end of the streamed text when the session is running and the part is incomplete', () => {
    render(
      <SessionTranscriptLayout
        turns={[makeTurn({ assistantParts: [makeStreamingTextPart()] })]}
        isRunning
        isStreaming
        isThinking={false}
      />,
    )

    expect(screen.getByTestId('assistant-text-streaming-cursor')).toBeInTheDocument()
  })

  it('renders the block cursor while the session is running and the part is marked actively streaming, even with completedAt set', () => {
    render(
      <SessionTranscriptLayout
        turns={[makeTurn({
          assistantParts: [makeStreamingTextPart({
            completedAt: '2026-06-12T00:00:02.000Z',
            isStreaming: true,
          })],
        })]}
        isRunning
        isStreaming
        isThinking={false}
      />,
    )

    expect(screen.getByTestId('assistant-text-streaming-cursor')).toBeInTheDocument()
  })

  it('does not render the block cursor on a completed text part in a live session', () => {
    render(
      <SessionTranscriptLayout
        turns={[makeTurn({
          assistantParts: [{
            id: 'text-done',
            partType: 'text',
            text: 'done writing',
            startedAt: '2026-06-12T00:00:01.000Z',
            completedAt: '2026-06-12T00:00:02.000Z',
          }],
        })]}
        isRunning
        isStreaming={false}
        isThinking={false}
      />,
    )

    expect(screen.queryByTestId('assistant-text-streaming-cursor')).toBeNull()
  })

  it('does not render the block cursor when the session is not running, regardless of the streaming flag', () => {
    render(
      <SessionTranscriptLayout
        turns={[makeTurn({
          assistantParts: [makeStreamingTextPart({
            completedAt: '2026-06-12T00:00:02.000Z',
            isStreaming: true,
          })],
        })]}
        isRunning={false}
        isStreaming
        isThinking={false}
      />,
    )

    expect(screen.queryByTestId('assistant-text-streaming-cursor')).toBeNull()
  })

  it('removes the block cursor when a streaming text part completes', () => {
    function Harness({ completedAt }: { completedAt: string | null }) {
      return (
        <SessionTranscriptLayout
          turns={[makeTurn({
            assistantParts: [{
              id: 'text-completing',
              partType: 'text',
              text: 'writing',
              startedAt: '2026-06-12T00:00:01.000Z',
              completedAt,
              isStreaming: completedAt === null,
            }],
          })]}
          isRunning
          isStreaming={false}
          isThinking={false}
        />
      )
    }

    const { rerender } = render(<Harness completedAt={null} />)
    expect(screen.getByTestId('assistant-text-streaming-cursor')).toBeInTheDocument()

    rerender(<Harness completedAt="2026-06-12T00:00:03.000Z" />)
    expect(screen.queryByTestId('assistant-text-streaming-cursor')).toBeNull()
  })

  it('removes the block cursor when a running session transitions to not running mid-stream', () => {
    function Harness({ isRunning }: { isRunning: boolean }) {
      return (
        <SessionTranscriptLayout
          turns={[makeTurn({
            assistantParts: [makeStreamingTextPart()],
          })]}
          isRunning={isRunning}
          isStreaming
          isThinking={false}
        />
      )
    }

    const { rerender } = render(<Harness isRunning />)
    expect(screen.getByTestId('assistant-text-streaming-cursor')).toBeInTheDocument()

    rerender(<Harness isRunning={false} />)
    expect(screen.queryByTestId('assistant-text-streaming-cursor')).toBeNull()
  })

  it('marks the block cursor aria-hidden, not focusable, and without a semantic role', () => {
    const { container } = render(
      <SessionTranscriptLayout
        turns={[makeTurn({ assistantParts: [makeStreamingTextPart()] })]}
        isRunning
        isStreaming
        isThinking={false}
      />,
    )

    const cursor = screen.getByTestId('assistant-text-streaming-cursor')
    expect(cursor.getAttribute('aria-hidden')).toBe('true')
    expect(cursor.tagName.toLowerCase()).toBe('span')
    expect(cursor.getAttribute('tabindex')).toBeNull()
    expect(cursor.getAttribute('role')).toBeNull()

    const focusables = container.querySelectorAll('[data-testid="assistant-text-streaming-cursor"][tabindex], [data-testid="assistant-text-streaming-cursor"][role]')
    expect(focusables.length).toBe(0)
  })

  it('does not include the previous trailing dot glyph testid', () => {
    render(
      <SessionTranscriptLayout
        turns={[makeTurn({ assistantParts: [makeStreamingTextPart()] })]}
        isRunning
        isStreaming
        isThinking={false}
      />,
    )

    expect(screen.queryByTestId('assistant-text-streaming-glyph')).toBeNull()
    expect(screen.getByTestId('assistant-text-streaming-cursor')).toBeInTheDocument()
  })

  it('keeps the Copy button alongside the block cursor while streaming', () => {
    render(
      <SessionTranscriptLayout
        turns={[makeTurn({ assistantParts: [makeStreamingTextPart()] })]}
        isRunning
        isStreaming
        isThinking={false}
      />,
    )

    const cursor = screen.getByTestId('assistant-text-streaming-cursor')
    expect(cursor).toBeInTheDocument()

    const partRoot = cursor.parentElement
    expect(partRoot?.querySelector('button')?.textContent).toMatch(/copy/i)
  })

  it('places the block cursor after the TranscriptMarkdown output, before the Copy button row', () => {
    const { container } = render(
      <SessionTranscriptLayout
        turns={[makeTurn({ assistantParts: [makeStreamingTextPart()] })]}
        isRunning
        isStreaming
        isThinking={false}
      />,
    )

    const partRoot = container.querySelector('[data-testid="assistant-text-streaming-cursor"]')?.parentElement
    expect(partRoot).not.toBeNull()

    const children = Array.from(partRoot!.children)
    const cursorIndex = children.findIndex((el) => el.getAttribute('data-testid') === 'assistant-text-streaming-cursor')
    const copyRowIndex = children.findIndex((el) => el.querySelector('button')?.textContent?.match(/copy/i))

    expect(cursorIndex).toBeGreaterThanOrEqual(0)
    expect(copyRowIndex).toBeGreaterThanOrEqual(0)
    expect(cursorIndex).toBeLessThan(copyRowIndex)
  })
})
