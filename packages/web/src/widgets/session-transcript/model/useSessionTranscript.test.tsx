import { act, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { describe, expect, it, vi } from 'vitest'
import { dispatchAgentEvent } from '../../../entities/agent'
import type { SessionTurn } from '../../../entities/coder-session'
import { useSessionTranscript } from './useSessionTranscript'

function Wrapper({
  events,
  isRunning = true,
}: {
  events: SessionTurn[]
  isRunning?: boolean
}) {
  const result = useSessionTranscript({
    issueNumber: 84,
    sessionId: 'session-84',
    runtimeSessionId: 'runtime-84',
    runtime: 'opencode',
    initialTurns: events,
    isRunning,
    terminalInvalidationKey: ['agent-session', 'project-1', 'session-84'],
  })
  const text = result.turns
    .flatMap((turn) => turn.assistant)
    .filter((part) => part.type === 'text' || part.type === 'reasoning')
    .map((part) => part.type === 'text' || part.type === 'reasoning' ? part.text : '')
    .join('')
  const latestTurn = result.turns.at(-1)
  const error = latestTurn?.assistant.find((part) => part.type === 'error')
  const outcome = error?.type === 'error' && error.kind === 'failed'
    ? 'failed'
    : latestTurn?.completedAt
      ? 'completed'
      : 'in-flight'
  return (
    <div>
      <div data-testid="transcript">{text}</div>
      <div data-testid="turn-outcome">{outcome}</div>
      <div data-testid="error-message">{error?.type === 'error' ? error.message : ''}</div>
      <div data-testid="session-status">{result.isFinalizing ? 'finalizing' : 'running'}</div>
      <div data-testid="thinking-state">{result.isThinking ? 'thinking' : 'idle'}</div>
      <div data-testid="streaming-state">{result.isStreaming ? 'streaming' : 'idle'}</div>
      <div data-testid="turn-count">{result.turns.length}</div>
      <div data-testid="latest-user">{result.turns.at(-1)?.user.text ?? ''}</div>
    </div>
  )
}

function renderSessionTranscript(events: SessionTurn[], isRunning = true) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const result = render(
    <QueryClientProvider client={queryClient}>
      <Wrapper events={events} isRunning={isRunning} />
    </QueryClientProvider>,
  )
  return {
    ...result,
    queryClient,
    rerenderWith: (nextEvents: SessionTurn[], nextIsRunning = true) =>
      result.rerender(
        <QueryClientProvider client={queryClient}>
          <Wrapper events={nextEvents} isRunning={nextIsRunning} />
        </QueryClientProvider>,
      ),
  }
}

function persistedEvent(text: string): SessionTurn {
  return {
    id: 'turn-1',
    startedAt: '2026-06-12T00:00:00.000Z',
    completedAt: null,
    incomplete: false,
    user: {
      role: 'mohist',
      text: '',
      kind: 'task',
      sentAt: '2026-06-12T00:00:00.000Z',
    },
    assistant: [
      {
        id: 'part-1',
        type: 'text',
        text,
        startedAt: '2026-06-12T00:00:00.000Z',
        completedAt: null,
      },
    ],
  }
}

function followupTurn(): SessionTurn {
  return {
    id: 'followup-turn',
    startedAt: '2026-06-12T00:01:00.000Z',
    completedAt: null,
    incomplete: true,
    user: {
      role: 'mohist',
      text: 'Continue',
      kind: 'followup',
      sentAt: '2026-06-12T00:01:00.000Z',
    },
    assistant: [
      {
        id: 'followup-part',
        type: 'text',
        text: 'Working',
        startedAt: '2026-06-12T00:01:01.000Z',
        completedAt: null,
      },
    ],
  }
}

function WrapperWithoutBinding({
  events,
  isRunning = true,
}: {
  events: SessionTurn[]
  isRunning?: boolean
}) {
  const result = useSessionTranscript({
    issueNumber: 84,
    sessionId: 'session-84',
    runtimeSessionId: '',
    runtime: null,
    initialTurns: events,
    isRunning,
  })
  const text = result.turns
    .flatMap((turn) => turn.assistant)
    .filter((part) => part.type === 'text' || part.type === 'reasoning')
    .map((part) => part.type === 'text' || part.type === 'reasoning' ? part.text : '')
    .join('')
  return <div data-testid="transcript">{text}</div>
}

function WrapperHistorical({
  events,
  isRunning = true,
}: {
  events: SessionTurn[]
  isRunning?: boolean
}) {
  const result = useSessionTranscript({
    issueNumber: 84,
    sessionId: 'session-84',
    runtimeSessionId: 'runtime-84',
    runtime: 'opencode',
    isHistoricalRuntimeView: true,
    initialTurns: events,
    isRunning,
  })
  const text = result.turns
    .flatMap((turn) => turn.assistant)
    .filter((part) => part.type === 'text' || part.type === 'reasoning')
    .map((part) => part.type === 'text' || part.type === 'reasoning' ? part.text : '')
    .join('')
  return <div data-testid="transcript">{text}</div>
}

function renderSessionTranscriptWithoutBinding(events: SessionTurn[], isRunning = true) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <WrapperWithoutBinding events={events} isRunning={isRunning} />
    </QueryClientProvider>,
  )
}

function renderHistoricalSessionTranscript(events: SessionTurn[], isRunning = true) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <WrapperHistorical events={events} isRunning={isRunning} />
    </QueryClientProvider>,
  )
}

describe('useSessionTranscript', () => {
  it('does not let running persisted refetch overwrite live transcript tail', () => {
    const initial = [persistedEvent('persisted')]
    const { rerenderWith } = renderSessionTranscript(initial)
    expect(screen.getByTestId('transcript').textContent).toBe('persisted')

    act(() => {
      dispatchAgentEvent('message.delta', {
        sessionId: 'session-84',
        runtimeSessionId: 'runtime-84',
        runtime: 'opencode',
        text: ' live',
      })
    })
    expect(screen.getByTestId('transcript').textContent).toBe('persisted live')

    const staleRefetch = [persistedEvent('persisted')]
    rerenderWith(staleRefetch)

    expect(screen.getByTestId('transcript').textContent).toBe('persisted live')
  })

  it('ignores events from a replaced runtime even when they carry the same logical session id', () => {
    renderSessionTranscript([persistedEvent('persisted')])

    act(() => {
      dispatchAgentEvent('message.delta', {
        sessionId: 'session-84',
        runtimeSessionId: 'runtime-old',
        runtime: 'opencode',
        text: ' stale',
      })
    })

    expect(screen.getByTestId('transcript').textContent).toBe('persisted')
  })

  it('ignores runtime events that provide only the logical session id', () => {
    renderSessionTranscript([persistedEvent('persisted')])

    act(() => {
      dispatchAgentEvent('message.delta', {
        sessionId: 'session-84',
        text: ' stale',
      })
    })

    expect(screen.getByTestId('transcript').textContent).toBe('persisted')
  })

  it('ignores events from another runtime with the same physical session id', () => {
    renderSessionTranscript([persistedEvent('persisted')])

    act(() => {
      dispatchAgentEvent('message.delta', {
        sessionId: 'session-84',
        runtimeSessionId: 'runtime-84',
        runtime: 'other-runtime',
        text: ' stale',
      })
    })

    expect(screen.getByTestId('transcript').textContent).toBe('persisted')
  })

  it('accepts persisted transcript once the session is no longer running', () => {
    const initial = [persistedEvent('persisted')]
    const { rerenderWith } = renderSessionTranscript(initial)

    act(() => {
      dispatchAgentEvent('message.delta', {
        sessionId: 'session-84',
        runtimeSessionId: 'runtime-84',
        runtime: 'opencode',
        text: ' live',
      })
    })
    expect(screen.getByTestId('transcript').textContent).toBe('persisted live')

    const flushed = [persistedEvent('persisted live')]
    rerenderWith(flushed, false)

    expect(screen.getByTestId('transcript').textContent).toBe('persisted live')
  })

  it('preserves a \\n\\n paragraph boundary across interleaved message.delta and coder_text_chunk events', () => {
    renderSessionTranscript([])

    const deltas: Array<{ type: 'message.delta' | 'coder_text_chunk'; text: string }> = [
      { type: 'message.delta', text: 'usage:' },
      { type: 'coder_text_chunk', text: '\n' },
      { type: 'message.delta', text: '\n' },
      { type: 'coder_text_chunk', text: 'Let me read the file.' },
    ]

    for (const delta of deltas) {
      act(() => {
        const baseDetail = {
          sessionId: 'session-84',
          runtimeSessionId: 'runtime-84',
          runtime: 'opencode',
          text: delta.text,
        }
        if (delta.type === 'coder_text_chunk') {
          dispatchAgentEvent('coder_text_chunk', {
            issueNumber: 84,
            projectId: 'proj-test',
            ...baseDetail,
          })
        } else {
          dispatchAgentEvent('message.delta', baseDetail)
        }
      })
    }

    const transcript = screen.getByTestId('transcript').textContent ?? ''
    expect(transcript).toBe('usage:\n\nLet me read the file.')
    expect(transcript).not.toContain('usage:Let me')
    expect(transcript).toContain('\n\n')
  })

  it('accepts events when page physical binding is temporarily missing but logical session identity matches', () => {
    renderSessionTranscriptWithoutBinding([persistedEvent('persisted')])
    expect(screen.getByTestId('transcript').textContent).toBe('persisted')

    act(() => {
      dispatchAgentEvent('message.delta', {
        sessionId: 'session-84',
        runtimeSessionId: '',
        runtime: '',
        text: ' live',
      })
    })

    expect(screen.getByTestId('transcript').textContent).toBe('persisted live')
  })

  it('accepts events when page physical binding is missing and event provides a runtimeSessionId', () => {
    renderSessionTranscriptWithoutBinding([persistedEvent('persisted')])

    act(() => {
      dispatchAgentEvent('message.delta', {
        sessionId: 'session-84',
        runtimeSessionId: 'runtime-new',
        runtime: 'opencode',
        text: ' live',
      })
    })

    expect(screen.getByTestId('transcript').textContent).toBe('persisted live')
  })

  it('rejects events from a different logical session', () => {
    renderSessionTranscript([persistedEvent('persisted')])

    act(() => {
      dispatchAgentEvent('message.delta', {
        sessionId: 'session-other',
        runtimeSessionId: 'runtime-84',
        runtime: 'opencode',
        text: ' stale',
      })
    })

    expect(screen.getByTestId('transcript').textContent).toBe('persisted')
  })

  it('rejects events that omit runtimeSessionId when page has a physical binding', () => {
    renderSessionTranscript([persistedEvent('persisted')])

    act(() => {
      dispatchAgentEvent('message.delta', {
        sessionId: 'session-84',
        text: ' stale',
      })
    })

    expect(screen.getByTestId('transcript').textContent).toBe('persisted')
  })

  it('rejects events with ambiguous identity when page has no binding and event has no sessionId', () => {
    renderSessionTranscriptWithoutBinding([persistedEvent('persisted')])

    act(() => {
      dispatchAgentEvent('message.delta', {
        text: ' stale',
      })
    })

    expect(screen.getByTestId('transcript').textContent).toBe('persisted')
  })

  it('historical runtime view rejects events that omit physical runtime identity', () => {
    renderHistoricalSessionTranscript([persistedEvent('persisted')])

    act(() => {
      dispatchAgentEvent('message.delta', {
        sessionId: 'session-84',
        text: ' stale',
      })
    })

    expect(screen.getByTestId('transcript').textContent).toBe('persisted')
  })

  it('historical runtime view accepts events with full matching identity', () => {
    renderHistoricalSessionTranscript([persistedEvent('persisted')])

    act(() => {
      dispatchAgentEvent('message.delta', {
        sessionId: 'session-84',
        runtimeSessionId: 'runtime-84',
        runtime: 'opencode',
        text: ' live',
      })
    })

    expect(screen.getByTestId('transcript').textContent).toBe('persisted live')
  })

  it('historical runtime view rejects events with different physical runtime identity', () => {
    renderHistoricalSessionTranscript([persistedEvent('persisted')])

    act(() => {
      dispatchAgentEvent('message.delta', {
        sessionId: 'session-84',
        runtimeSessionId: 'runtime-different',
        runtime: 'opencode',
        text: ' stale',
      })
    })

    expect(screen.getByTestId('transcript').textContent).toBe('persisted')
  })

  it('closes an in-flight follow-up as completed and refreshes session status without making the session terminal', () => {
    const { queryClient } = renderSessionTranscript([followupTurn()])
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')

    act(() => {
      dispatchAgentEvent('session.followup_completed', {
        sessionId: 'session-84',
        runtimeSessionId: 'runtime-84',
        runtime: 'opencode',
        operationId: 'operation-1',
        status: 'completed',
      })
    })

    expect(screen.getByTestId('turn-outcome')).toHaveTextContent('completed')
    expect(screen.getByTestId('session-status')).toHaveTextContent('running')
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['agent-session', 'project-1', 'session-84'] })
  })

  it('closes an in-flight follow-up as failed and refreshes session status without making the session terminal', () => {
    const { queryClient } = renderSessionTranscript([followupTurn()])
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')

    act(() => {
      dispatchAgentEvent('session.followup_failed', {
        sessionId: 'session-84',
        runtimeSessionId: 'runtime-84',
        runtime: 'opencode',
        operationId: 'operation-2',
        status: 'failed',
        failureReason: 'Follow-up rejected',
      })
    })

    expect(screen.getByTestId('turn-outcome')).toHaveTextContent('failed')
    expect(screen.getByTestId('error-message')).toHaveTextContent('Follow-up rejected')
    expect(screen.getByTestId('session-status')).toHaveTextContent('running')
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['agent-session', 'project-1', 'session-84'] })
  })

  it('renders follow-up input without engaging activity until a runtime response arrives', () => {
    renderSessionTranscript([persistedEvent('Earlier response')])

    act(() => {
      dispatchAgentEvent('session.input', {
        sessionId: 'session-84',
        runtimeSessionId: 'runtime-84',
        runtime: 'opencode',
        text: 'Continue',
        kind: 'followup',
        sentAt: '2026-06-12T00:01:00.000Z',
      })
    })

    expect(screen.getByTestId('transcript')).toHaveTextContent('Earlier response')
    expect(screen.getByTestId('turn-count')).toHaveTextContent('2')
    expect(screen.getByTestId('latest-user')).toHaveTextContent('Continue')
    expect(screen.getByTestId('thinking-state')).toHaveTextContent('idle')
    expect(screen.getByTestId('streaming-state')).toHaveTextContent('idle')

    act(() => {
      dispatchAgentEvent('message.delta', {
        sessionId: 'session-84',
        runtimeSessionId: 'runtime-84',
        runtime: 'opencode',
        text: 'Response',
      })
    })

    expect(screen.getByTestId('transcript')).toHaveTextContent('Response')
    expect(screen.getByTestId('streaming-state')).toHaveTextContent('streaming')
  })

  it('does not infer thinking for a follow-up-only turn while the session is running', () => {
    renderSessionTranscript([{
      ...followupTurn(),
      assistant: [],
    }])

    expect(screen.getByTestId('thinking-state')).toHaveTextContent('idle')
    expect(screen.getByTestId('streaming-state')).toHaveTextContent('idle')
  })

  it('reasoning.delta interrupt closes the open text part and a later text delta opens a new part (distinct blocks)', () => {
    renderSessionTranscript([])

    act(() => {
      dispatchAgentEvent('message.delta', {
        sessionId: 'session-84',
        runtimeSessionId: 'runtime-84',
        runtime: 'opencode',
        text: 'usage:',
      })
    })
    act(() => {
      dispatchAgentEvent('reasoning.delta', {
        sessionId: 'session-84',
        runtimeSessionId: 'runtime-84',
        runtime: 'opencode',
        text: 'thinking about it',
      })
    })
    act(() => {
      dispatchAgentEvent('coder_text_chunk', {
        issueNumber: 84,
        projectId: 'proj-test',
        sessionId: 'session-84',
        runtimeSessionId: 'runtime-84',
        runtime: 'opencode',
        text: '\n\nLet me check the docs.',
      })
    })

    const transcript = screen.getByTestId('transcript').textContent ?? ''
    expect(transcript).toContain('usage:')
    expect(transcript).toContain('thinking about it')
    expect(transcript).toContain('Let me check the docs.')
    expect(transcript).not.toContain('usage:Let me')
  })
})
