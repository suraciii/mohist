import { act, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { describe, expect, it } from 'vitest'
import { dispatchAgentEvent } from '../../../entities/agent'
import type { SessionTurn } from '../../../entities/coder-session'
import { useSessionTranscript } from './useSessionTranscript'

function Wrapper({ events }: { events: SessionTurn[] }) {
  const result = useSessionTranscript({
    issueNumber: 84,
    sessionId: 'session-84',
    runtimeSessionId: 'runtime-84',
    runtime: 'opencode',
    initialTurns: events,
    isRunning: true,
  })
  return (
    <div>
      <div data-testid="thinking-state">{result.isThinking ? 'thinking' : 'idle'}</div>
      <div data-testid="streaming-state">{result.isStreaming ? 'streaming' : 'idle'}</div>
      <div data-testid="transcript">
        {result.turns
          .flatMap((turn) => turn.assistant)
          .filter((part) => part.type === 'text' || part.type === 'reasoning')
          .map((part) => part.type === 'text' || part.type === 'reasoning' ? part.text : '')
          .join('')}
      </div>
    </div>
  )
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

function renderSessionTranscript(events: SessionTurn[]) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <Wrapper events={events} />
    </QueryClientProvider>,
  )
}

describe('useSessionTranscript activity behavior', () => {
  it('does not infer thinking for a follow-up-only turn while the session is running', () => {
    renderSessionTranscript([{ ...followupTurn(), assistant: [] }])

    expect(screen.getByTestId('thinking-state')).toHaveTextContent('idle')
    expect(screen.getByTestId('streaming-state')).toHaveTextContent('idle')
  })

  it('reasoning.delta interrupt closes the open text part and a later text delta opens a new part', () => {
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
