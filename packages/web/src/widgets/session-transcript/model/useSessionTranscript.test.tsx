import { act, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { describe, expect, it } from 'vitest'
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
    runtimeSessionId: 'acp-84',
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

function renderSessionTranscript(events: SessionTurn[], isRunning = true) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const result = render(
    <QueryClientProvider client={queryClient}>
      <Wrapper events={events} isRunning={isRunning} />
    </QueryClientProvider>,
  )
  return {
    ...result,
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

describe('useSessionTranscript', () => {
  it('does not let running persisted refetch overwrite live transcript tail', () => {
    const initial = [persistedEvent('persisted')]
    const { rerenderWith } = renderSessionTranscript(initial)
    expect(screen.getByTestId('transcript').textContent).toBe('persisted')

    act(() => {
      dispatchAgentEvent('message.delta', {
        runtimeSessionId: 'acp-84',
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
        runtimeSessionId: 'acp-old',
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

  it('accepts persisted transcript once the session is no longer running', () => {
    const initial = [persistedEvent('persisted')]
    const { rerenderWith } = renderSessionTranscript(initial)

    act(() => {
      dispatchAgentEvent('message.delta', {
        runtimeSessionId: 'acp-84',
        text: ' live',
      })
    })
    expect(screen.getByTestId('transcript').textContent).toBe('persisted live')

    const flushed = [persistedEvent('persisted live')]
    rerenderWith(flushed, false)

    expect(screen.getByTestId('transcript').textContent).toBe('persisted live')
  })
})
