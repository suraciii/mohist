import { act, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { describe, expect, it } from 'vitest'
import { dispatchAgentEvent } from '../../../entities/agent'
import type { SessionEvent } from '../../../entities/session/model/view'
import { useSessionTranscript } from './useSessionTranscript'

function Wrapper({
  events,
  isRunning = true,
}: {
  events: SessionEvent[]
  isRunning?: boolean
}) {
  const result = useSessionTranscript({
    issueNumber: 84,
    sessionId: 'session-84',
    acpSessionId: 'acp-84',
    initialEvents: events,
    isRunning,
  })
  const text = result.turns
    .flatMap((turn) => turn.assistant)
    .filter((part) => part.type === 'text' || part.type === 'reasoning')
    .map((part) => part.type === 'text' || part.type === 'reasoning' ? part.text : '')
    .join('')
  return <div data-testid="transcript">{text}</div>
}

function renderHookHarness(events: SessionEvent[], isRunning = true) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const result = render(
    <QueryClientProvider client={queryClient}>
      <Wrapper events={events} isRunning={isRunning} />
    </QueryClientProvider>,
  )
  return {
    ...result,
    rerenderWith: (nextEvents: SessionEvent[], nextIsRunning = true) =>
      result.rerender(
        <QueryClientProvider client={queryClient}>
          <Wrapper events={nextEvents} isRunning={nextIsRunning} />
        </QueryClientProvider>,
      ),
  }
}

function persistedEvent(text: string): SessionEvent {
  return {
    id: 1,
    sequence: 1,
    type: 'agent_message',
    payload: { text },
    createdAt: '2026-06-12T00:00:00.000Z',
  }
}

describe('useSessionTranscript', () => {
  it('does not let running persisted refetch overwrite live transcript tail', () => {
    const initial = [persistedEvent('persisted')]
    const { rerenderWith } = renderHookHarness(initial)
    expect(screen.getByTestId('transcript').textContent).toBe('persisted')

    act(() => {
      dispatchAgentEvent('coder_text_chunk', {
        issueId: '84',
        projectId: 'project',
        executionId: 'work',
        acpSessionId: 'acp-84',
        text: ' live',
      })
    })
    expect(screen.getByTestId('transcript').textContent).toBe('persisted live')

    const staleRefetch = [persistedEvent('persisted')]
    rerenderWith(staleRefetch)

    expect(screen.getByTestId('transcript').textContent).toBe('persisted live')
  })

  it('accepts persisted transcript once the session is no longer running', () => {
    const initial = [persistedEvent('persisted')]
    const { rerenderWith } = renderHookHarness(initial)

    act(() => {
      dispatchAgentEvent('coder_text_chunk', {
        issueId: '84',
        projectId: 'project',
        executionId: 'work',
        acpSessionId: 'acp-84',
        text: ' live',
      })
    })
    expect(screen.getByTestId('transcript').textContent).toBe('persisted live')

    const flushed = [persistedEvent('persisted live')]
    rerenderWith(flushed, false)

    expect(screen.getByTestId('transcript').textContent).toBe('persisted live')
  })
})
