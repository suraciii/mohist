import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, renderHook } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { dispatchAgentEvent } from '../../../entities/agent'
import type { SessionTurn } from '../../../entities/coder-session'
import { useSessionTranscript } from './useSessionTranscript'
import { useSessionTimeline } from './useSessionTimeline'

const at = '2026-08-03T10:00:00.000Z'
const emptyTurns: SessionTurn[] = []

function readTurn(): SessionTurn {
  return {
    id: 'turn-1',
    startedAt: at,
    completedAt: at,
    user: { role: 'mohist', text: 'Read files', kind: 'task', sentAt: at },
    assistant: [1, 2, 3].map((index) => ({
      id: `part-${index}`,
      type: 'tool' as const,
      tool: {
        toolCallId: `read-${index}`,
        toolName: 'read',
        status: 'completed' as const,
        target: `src/${index}.ts`,
        startedAt: at,
        completedAt: at,
      },
    })),
  }
}

describe('useSessionTimeline', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date(at))
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('returns one fact set for both unfolded items and grouped entries', () => {
    const { result } = renderHook(() => useSessionTimeline({
      turns: [readTurn()],
      summary: { activity: 'idle' },
    }))

    expect(result.current.facts.filter(fact => fact.kind === 'tool')).toHaveLength(3)
    expect(result.current.items.filter(item => item.renderClass === 'file-read')).toHaveLength(3)
    expect(result.current.entries.some(entry => entry.summary === '读取了 3 个文件')).toBe(true)
    expect(result.current.currentActivity).toMatchObject({ state: 'idle', label: '空闲' })
  })

  it.each([
    ['active', 'active', '执行中'],
    ['idle', 'idle', '空闲'],
    ['unknown', 'unknown', '状态未知'],
  ] as const)('preserves summary activity %s as %s', (activity, state, label) => {
    const { result } = renderHook(() => useSessionTimeline({ summary: { activity } }))

    expect(result.current.currentActivity).toMatchObject({ state, label })
  })

  it('uses queued turn state before active session activity', () => {
    const { result } = renderHook(() => useSessionTimeline({
      summary: {
        activity: 'active',
        currentTurnId: 'turn-queued',
        turns: [{ id: 'turn-queued', sequence: 1, inputIds: [], status: 'queued' }],
      },
    }))

    expect(result.current.currentActivity).toMatchObject({ state: 'queued', label: '排队中', sourceId: 'turn:turn-queued:state' })
    expect(result.current.items).toContainEqual(expect.objectContaining({ renderClass: 'status', summary: '排队中' }))
  })

  it('does not treat a completed persisted message as current activity', () => {
    const turn: SessionTurn = {
      id: 'turn-executing',
      startedAt: at,
      completedAt: null,
      user: { role: 'mohist', text: 'Continue', kind: 'followup', sentAt: at },
      assistant: [{
        id: 'message-complete',
        type: 'text',
        text: 'Previous output',
        startedAt: at,
        completedAt: at,
      }],
    }
    const { result } = renderHook(() => useSessionTimeline({
      turns: [turn],
      summary: {
        activity: 'active',
        currentTurnId: 'turn-executing',
        turns: [{ id: 'turn-executing', sequence: 1, inputIds: [], status: 'executing' }],
      },
    }))

    expect(result.current.currentActivity).toMatchObject({ state: 'active', label: '执行中' })
  })

  it('combines identity-filtered live details without losing raw headers or tool identity', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const wrapper = ({ children }: { children: React.ReactNode }) => (
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    )
    const { result } = renderHook(() => {
      const transcript = useSessionTranscript({
        issueNumber: 0,
        sessionId: 'session-1',
        runtimeSessionId: 'runtime-1',
        runtime: 'opencode',
        isRunning: true,
        initialTurns: emptyTurns,
      })
      return {
        transcript,
        timeline: useSessionTimeline({
          turns: transcript.turns,
          liveDetails: transcript.liveDetails,
          summary: { activity: 'active' },
        }),
      }
    }, { wrapper })

    act(() => {
      dispatchAgentEvent('tool_call.started', {
        sessionId: 'other-session',
        runtimeSessionId: 'runtime-1',
        runtime: 'opencode',
        type: 'tool_call.started',
        sequence: 1,
        createdAt: at,
        payload: { shouldBeIgnored: true },
        toolCallId: 'ignored',
        toolName: 'read',
        state: 'started',
      })
      dispatchAgentEvent('tool_call.started', {
        sessionId: 'session-1',
        runtimeSessionId: 'runtime-1',
        runtime: 'opencode',
        type: 'tool_call.started',
        sequence: 2,
        createdAt: at,
        payload: { source: 'started' },
        toolCallId: 'call-1',
        toolName: 'read',
        state: 'started',
      })
      dispatchAgentEvent('tool_call.completed', {
        sessionId: 'session-1',
        runtimeSessionId: 'runtime-1',
        runtime: 'opencode',
        type: 'tool_call.completed',
        sequence: 3,
        createdAt: '2026-08-03T10:00:01.000Z',
        payload: { source: 'completed' },
        toolCallId: 'call-1',
        toolName: 'read',
        rawOutput: { ok: true },
        state: 'completed',
      })
    })

    expect(result.current.transcript.liveDetails).toHaveLength(2)
    expect(result.current.transcript.liveDetails[0]).toMatchObject({
      type: 'tool_call.started',
      sequence: 2,
      payload: { source: 'started' },
    })
    const toolItem = result.current.timeline.items.find(item => item.id === 'call-1')
    expect(toolItem).toMatchObject({
      id: 'call-1',
      renderClass: 'file-read',
      isTerminal: true,
      sourceIds: ['live:tool_call.started:2', 'live:tool_call.completed:3'],
    })
  })
})
