import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { screen, render, act } from './test-utils'
import { SessionTranscriptLayout } from '../src/widgets/session-transcript/ui/SessionTranscriptLayout'
import type { DisplayTurn } from '../src/widgets/session-transcript/model/session-transcript-display'
import { useSessionTranscript } from '../src/widgets/session-transcript/model/useSessionTranscript'
import { dispatchAgentEvent } from '../src/entities/agent'
import { makeTurn, queryClients } from './session-page-test-utils'
import { setScopedValue } from './support/scoped-property'

beforeEach(() => {
  vi.clearAllMocks()
  setScopedValue(navigator, 'clipboard', { writeText: vi.fn().mockResolvedValue(undefined) })
  setScopedValue(Element.prototype, 'scrollTo', vi.fn())
  setScopedValue(Element.prototype, 'scrollIntoView', vi.fn())
})

afterEach(() => {
  vi.useRealTimers()
  for (const queryClient of queryClients) queryClient.clear()
  queryClients.length = 0
})

function makeRunningTurn(overrides: Partial<DisplayTurn> = {}): DisplayTurn {
  return {
    id: 'turn-1',
    startedAt: '2026-06-12T00:00:00.000Z',
    completedAt: null,
    prompt: {
      role: 'mohist',
      text: 'do the thing',
      kind: 'task',
      sentAt: '2026-06-12T00:00:00.000Z',
    },
    assistantParts: [
      {
        id: 'text-1',
        partType: 'text',
        text: 'I will do it.',
        startedAt: '2026-06-12T00:00:01.000Z',
        completedAt: '2026-06-12T00:00:02.000Z',
      },
    ],
    changedFiles: [],
    state: 'idle',
    ...overrides,
  }
}

describe('SessionTranscriptLayout activity indicators are gated on session liveness', () => {
  it('renders the streaming indicator when isRunning is true and isStreaming is true', () => {
    render(
      <SessionTranscriptLayout
        turns={[makeRunningTurn()]}
        isRunning
        isStreaming
        isThinking={false}
      />,
    )

    expect(screen.getByTestId('transcript-streaming-indicator')).toBeInTheDocument()
    expect(screen.queryByTestId('transcript-thinking-indicator')).toBeNull()
  })

  it('renders the thinking indicator when isRunning is true and isThinking is true', () => {
    render(
      <SessionTranscriptLayout
        turns={[makeRunningTurn()]}
        isRunning
        isStreaming={false}
        isThinking
      />,
    )

    expect(screen.getByTestId('transcript-thinking-indicator')).toBeInTheDocument()
    expect(screen.queryByTestId('transcript-streaming-indicator')).toBeNull()
  })

  it('renders no streaming indicator when isRunning is false even if isStreaming is true', () => {
    render(
      <SessionTranscriptLayout
        turns={[makeRunningTurn()]}
        isRunning={false}
        isStreaming
        isThinking={false}
      />,
    )

    expect(screen.queryByTestId('transcript-streaming-indicator')).toBeNull()
    expect(screen.queryByTestId('transcript-thinking-indicator')).toBeNull()
  })

  it('renders no thinking indicator when isRunning is false even if isThinking is true', () => {
    render(
      <SessionTranscriptLayout
        turns={[makeRunningTurn()]}
        isRunning={false}
        isStreaming={false}
        isThinking
      />,
    )

    expect(screen.queryByTestId('transcript-thinking-indicator')).toBeNull()
    expect(screen.queryByTestId('transcript-streaming-indicator')).toBeNull()
  })

  it('renders no per-part streaming glyph on a completed part of a non-running session', () => {
    render(
      <SessionTranscriptLayout
        turns={[
          makeRunningTurn({
            assistantParts: [
              {
                id: 'text-done',
                partType: 'text',
                text: 'done',
                startedAt: '2026-06-12T00:00:01.000Z',
                completedAt: '2026-06-12T00:00:02.000Z',
              },
            ],
          }),
        ]}
        isRunning={false}
        isStreaming
        isThinking={false}
      />,
    )

    expect(screen.queryByTestId('assistant-text-streaming-cursor')).toBeNull()
  })

  it('renders the per-part streaming cursor on an incomplete part of a live session', () => {
    render(
      <SessionTranscriptLayout
        turns={[
          makeRunningTurn({
            assistantParts: [
              {
                id: 'text-live',
                partType: 'text',
                text: 'streaming',
                startedAt: '2026-06-12T00:00:01.000Z',
                completedAt: null,
                isStreaming: true,
              } as DisplayTurn['assistantParts'][number],
            ],
          }),
        ]}
        isRunning
        isStreaming
        isThinking={false}
      />,
    )

    expect(screen.getByTestId('assistant-text-streaming-cursor')).toBeInTheDocument()
    expect(screen.getByTestId('assistant-text-streaming-cursor').getAttribute('aria-hidden')).toBe('true')
  })

  it('hides the per-part streaming cursor when isRunning is false even on an incomplete part', () => {
    render(
      <SessionTranscriptLayout
        turns={[
          makeRunningTurn({
            assistantParts: [
              {
                id: 'text-stale',
                partType: 'text',
                text: 'streaming',
                startedAt: '2026-06-12T00:00:01.000Z',
                completedAt: null,
                isStreaming: true,
              } as DisplayTurn['assistantParts'][number],
            ],
          }),
        ]}
        isRunning={false}
        isStreaming
        isThinking={false}
      />,
    )

    expect(screen.queryByTestId('assistant-text-streaming-cursor')).toBeNull()
  })

  it('exposes role="status" on the streaming indicator so its appearance/removal is announced', () => {
    render(
      <SessionTranscriptLayout
        turns={[makeRunningTurn()]}
        isRunning
        isStreaming
        isThinking={false}
      />,
    )

    const indicator = screen.getByTestId('transcript-streaming-indicator')
    expect(indicator.getAttribute('role')).toBe('status')
  })

  it('exposes role="status" on the thinking indicator so its appearance/removal is announced', () => {
    render(
      <SessionTranscriptLayout
        turns={[makeRunningTurn()]}
        isRunning
        isStreaming={false}
        isThinking
      />,
    )

    const indicator = screen.getByTestId('transcript-thinking-indicator')
    expect(indicator.getAttribute('role')).toBe('status')
  })

  it('retains role="log" on the TurnList root so streamed content is announced as a live region', () => {
    const { container } = render(
      <SessionTranscriptLayout
        turns={[makeRunningTurn()]}
        isRunning
        isStreaming
        isThinking={false}
      />,
    )

    const log = container.querySelector('[role="log"]')
    expect(log).not.toBeNull()
  })
})

describe('useSessionTranscript clears streaming flag when session stops running', () => {
  it('clears isStreaming when isRunning transitions from true to false (controlled fake time)', async () => {
    vi.useFakeTimers()

    const initial = [makeTurn({
      assistant: [{
        id: 'text-1',
        type: 'text',
        text: '',
        startedAt: '2024-01-01T10:00:00.000Z',
        completedAt: null,
      }],
    })]

    let latest: { isStreaming: boolean; isThinking: boolean } = { isStreaming: false, isThinking: false }
    function Harness({ isRunning }: { isRunning: boolean }) {
      const t = useSessionTranscript({
        issueNumber: 426,
        sessionId: 'session-426',
        runtimeSessionId: 'acp-426',
        initialTurns: initial,
        isRunning,
      })
      latest = { isStreaming: t.isStreaming, isThinking: t.isThinking }
      return <div data-testid="harness" />
    }

    const { rerender } = render(<Harness isRunning />)

    await act(async () => {
      dispatchAgentEvent('coder_text_chunk', {
        issueNumber: 426,
        projectId: 'proj-426',
        executionId: 'exec-426',
        runtimeSessionId: 'acp-426',
        sessionId: 'session-426',
        text: 'live chunk',
      })
    })

    expect(latest.isStreaming).toBe(true)

    await act(async () => {
      rerender(<Harness isRunning={false} />)
    })

    expect(latest.isStreaming).toBe(false)
    expect(latest.isThinking).toBe(false)
  })

  it('removes visible streaming indicator when a running session transitions to not running (no wall-clock wait)', async () => {
    vi.useFakeTimers()

    const initialTurns = [makeTurn({
      assistant: [{
        id: 'text-1',
        type: 'text',
        text: 'hello',
        startedAt: '2024-01-01T10:00:00.000Z',
        completedAt: null,
      }],
    })]

    function Harness({ isRunning }: { isRunning: boolean }) {
      const t = useSessionTranscript({
        issueNumber: 426,
        sessionId: 'session-426',
        runtimeSessionId: 'acp-426',
        initialTurns,
        isRunning,
      })
      const displayTurns: DisplayTurn[] = t.turns.map((turn) => ({
        id: turn.id,
        startedAt: turn.startedAt,
        completedAt: turn.completedAt,
        prompt: {
          role: turn.user.role,
          text: turn.user.text,
          kind: turn.user.kind as DisplayTurn['prompt']['kind'],
          sentAt: turn.user.sentAt,
        },
        assistantParts: turn.assistant.flatMap<DisplayTurn['assistantParts'][number]>((part) => {
          if (part.type === 'text') {
            return [{
              id: part.id,
              partType: 'text' as const,
              text: part.text,
              startedAt: part.startedAt,
              completedAt: part.completedAt,
              isStreaming: false,
            }]
          }
          if (part.type === 'reasoning') {
            return [{
              id: part.id,
              partType: 'reasoning' as const,
              text: part.text,
              startedAt: part.startedAt,
              completedAt: part.completedAt,
            }]
          }
          return []
        }),
        changedFiles: [],
        state: 'idle',
      }))
      return (
        <SessionTranscriptLayout
          turns={displayTurns}
          isRunning={isRunning}
          isStreaming={t.isStreaming}
          isThinking={t.isThinking}
        />
      )
    }

    const { rerender } = render(<Harness isRunning />)

    await act(async () => {
      dispatchAgentEvent('coder_text_chunk', {
        issueNumber: 426,
        projectId: 'proj-426',
        executionId: 'exec-426',
        runtimeSessionId: 'acp-426',
        sessionId: 'session-426',
        text: 'streaming',
      })
    })

    expect(screen.getByTestId('transcript-streaming-indicator')).toBeInTheDocument()

    await act(async () => {
      rerender(<Harness isRunning={false} />)
    })

    expect(screen.queryByTestId('transcript-streaming-indicator')).toBeNull()
  })
})