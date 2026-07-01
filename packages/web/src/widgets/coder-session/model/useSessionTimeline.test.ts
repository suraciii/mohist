// @vitest-environment jsdom
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { renderHook, act } from '@testing-library/react'
import { describe, it, expect, vi, afterEach } from 'vitest'
import { createElement, type ReactNode } from 'react'
import { dispatchAgentEvent } from '../../../entities/agent'
import { deriveToolCallTitle, reconstructRoundsFromEvents, useSessionTimeline } from './useSessionTimeline'

vi.mock('../../../entities/agent/api/client', () => ({
  getAgentStatus: vi.fn().mockResolvedValue({ running: true, issueNumber: 123 }),
}))

const queryClients: QueryClient[] = []

afterEach(() => {
  for (const queryClient of queryClients) queryClient.clear()
  queryClients.length = 0
})

function renderTimelineHook() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  queryClients.push(queryClient)
  return renderHook(
    () => useSessionTimeline(123, {
      id: 'coder-1',
      acpSessionId: 'acp-1',
      executionId: null,
      taskDescription: null,
      status: 'active',
      createdAt: '2024-01-01T00:00:00.000Z',
      completedAt: null,
      model: null,
      coderType: null,
      stage: null,
      title: null,
      lastDataAt: null,
      probeSentAt: null,
      probeDeadlineAt: null,
      failureReason: null,
    }),
    {
      wrapper: ({ children }: { children: ReactNode }) => (
        createElement(QueryClientProvider, { client: queryClient }, children)
      ),
    },
  )
}

describe('deriveToolCallTitle', () => {
  it('returns title when title differs from toolName', () => {
    expect(deriveToolCallTitle('read', 'server.ts', '{}')).toBe('server.ts')
  })

  it('derives filename from JSON file_path for read tool', () => {
    expect(
      deriveToolCallTitle('read', 'read', '{"file_path":"packages/server/src/Mohist.Server/Program.cs"}')
    ).toBe('Program.cs')
  })

  it('derives command from JSON command for bash tool', () => {
    expect(
      deriveToolCallTitle('bash', 'bash', '{"command":"npm run build"}')
    ).toBe('npm run build')
  })

  it('returns rawInput string when JSON parse fails', () => {
    expect(deriveToolCallTitle('bash', 'bash', 'npm test')).toBe('npm test')
  })

  it('returns toolName when rawInput is null', () => {
    expect(deriveToolCallTitle('unknown', 'unknown', null as unknown as string)).toBe('unknown')
  })

  it('returns toolName when rawInput is undefined', () => {
    expect(deriveToolCallTitle('read', 'read', undefined)).toBe('read')
  })

  it('truncates long bash commands', () => {
    const longCmd = 'a'.repeat(100)
    expect(deriveToolCallTitle('bash', 'bash', `{"command":"${longCmd}"}`)).toBe(
      'a'.repeat(57) + '...'
    )
  })

  it('derives pattern from glob tool', () => {
    expect(deriveToolCallTitle('glob', 'glob', '{"pattern":"**/*.ts"}')).toBe('**/*.ts')
  })

  it('handles filePath variant for read tool', () => {
    expect(
      deriveToolCallTitle('read_file', 'read_file', '{"filePath":"src/main.ts"}')
    ).toBe('main.ts')
  })
})

function makeSessionEvent(overrides: Partial<{
  id: number
  sequence: number
  type: string
  payload: unknown
  createdAt: string
}> = {}) {
  const sequence = overrides.sequence ?? 0
  return {
    id: overrides.id ?? sequence,
    sequence,
    type: overrides.type ?? 'input',
    payload: overrides.payload,
    createdAt: overrides.createdAt ?? '2024-01-01T00:00:00.000Z',
  }
}

describe('reconstructRoundsFromEvents', () => {
  it('returns empty array for empty events', () => {
    expect(reconstructRoundsFromEvents([])).toEqual([])
  })

  it('routes events through viewSessionEvents timeline projection', async () => {
    const viewModule = await import('../../../entities/session/model/view')
    const spy = vi.spyOn(viewModule, 'viewSessionEvents')
    try {
      const events = [makeSessionEvent({ type: 'input', payload: { text: 'hello' } })]
      reconstructRoundsFromEvents(events)
      expect(spy).toHaveBeenCalledWith(events, 'timeline')
    } finally {
      spy.mockRestore()
    }
  })

  it('creates one round per input with assistant and thought content grouped', () => {
    const events = [
      makeSessionEvent({ sequence: 0, type: 'input', payload: { text: 'first prompt', kind: 'initial' }, createdAt: '2024-01-01T00:00:00Z' }),
      makeSessionEvent({ sequence: 1, type: 'assistant_text', payload: { text: 'Hello' }, createdAt: '2024-01-01T00:00:01Z' }),
      makeSessionEvent({ sequence: 2, type: 'assistant_text', payload: { text: ' world' }, createdAt: '2024-01-01T00:00:02Z' }),
      makeSessionEvent({ sequence: 3, type: 'assistant_reasoning', payload: { text: 'thinking' }, createdAt: '2024-01-01T00:00:03Z' }),
      makeSessionEvent({ sequence: 4, type: 'input', payload: { text: 'second prompt', kind: 'task' }, createdAt: '2024-01-01T00:00:04Z' }),
      makeSessionEvent({ sequence: 5, type: 'assistant_text', payload: { text: 'second' }, createdAt: '2024-01-01T00:00:05Z' }),
    ]

    const rounds = reconstructRoundsFromEvents(events)

    expect(rounds).toHaveLength(2)
    expect(rounds[0].roundIndex).toBe(0)
    expect(rounds[0].userText).toBe('first prompt')
    expect(rounds[0].agentText).toBe('Hello world')
    expect(rounds[0].thoughtText).toBe('thinking')
    expect(rounds[0].startedAt).toBe('2024-01-01T00:00:00Z')
    expect(rounds[1].roundIndex).toBe(1)
    expect(rounds[1].userText).toBe('second prompt')
    expect(rounds[1].agentText).toBe('second')
    expect(rounds[1].thoughtText).toBe('')
  })

  it('groups tool_call and tool_call by toolCallId with updated status', () => {
    const events = [
      makeSessionEvent({ sequence: 0, type: 'input', payload: { text: 'use tools' }, createdAt: '2024-01-01T00:00:00Z' }),
      makeSessionEvent({ sequence: 1, type: 'tool_call', payload: { toolCallId: 'call-1', kind: 'bash', title: 'bash', rawInput: '{"command":"ls"}' }, createdAt: '2024-01-01T00:00:01Z' }),
      makeSessionEvent({ sequence: 2, type: 'tool_call', payload: { toolCallId: 'call-1', status: 'completed', rawOutput: 'file.txt' }, createdAt: '2024-01-01T00:00:02Z' }),
    ]

    const rounds = reconstructRoundsFromEvents(events)

    expect(rounds).toHaveLength(1)
    expect(rounds[0].toolCalls).toHaveLength(1)
    expect(rounds[0].toolCalls[0].toolCallId).toBe('call-1')
    expect(rounds[0].toolCalls[0].toolName).toBe('bash')
    expect(rounds[0].toolCalls[0].state).toBe('completed')
    expect(rounds[0].toolCalls[0].rawOutput).toBe('file.txt')
    expect(rounds[0].toolCalls[0].rawInput).toBe('{"command":"ls"}')
  })

  it('maps session.liveness events to recovery events on the active round', () => {
    const events = [
      makeSessionEvent({ sequence: 0, type: 'input', payload: { text: 'p' }, createdAt: '2024-01-01T00:00:00Z' }),
      makeSessionEvent({ sequence: 1, type: 'session.liveness', payload: { status: 'probing', activeProbeVersion: 2 }, createdAt: '2024-01-01T00:00:01Z' }),
      makeSessionEvent({ sequence: 2, type: 'session.liveness', payload: { status: 'failed', failureReason: 'timeout' }, createdAt: '2024-01-01T00:00:02Z' }),
    ]

    const rounds = reconstructRoundsFromEvents(events)

    expect(rounds).toHaveLength(1)
    expect(rounds[0].recoveryEvents).toHaveLength(2)
    expect(rounds[0].recoveryEvents[0].status).toBe('recovering')
    expect(rounds[0].recoveryEvents[0].attempt).toBe(2)
    expect(rounds[0].recoveryEvents[1].status).toBe('failed')
    expect(rounds[0].recoveryEvents[1].reason).toBe('timeout')
  })

  it('infers round labels from total count', () => {
    const events = [
      makeSessionEvent({ sequence: 0, type: 'input', payload: { text: 'p1' }, createdAt: '2024-01-01T00:00:00Z' }),
      makeSessionEvent({ sequence: 1, type: 'input', payload: { text: 'p2' }, createdAt: '2024-01-01T00:00:01Z' }),
    ]

    const rounds = reconstructRoundsFromEvents(events)

    expect(rounds[0].label).toBe('proposal.md')
    expect(rounds[1].label).toBe('specs/')
  })

  it('projects a compaction event into the active round with before/after counts', () => {
    const events = [
      makeSessionEvent({ sequence: 0, type: 'input', payload: { text: 'p' }, createdAt: '2024-01-01T00:00:00Z' }),
      makeSessionEvent({
        sequence: 1,
        type: 'compaction',
        payload: {
          strategy: 'summary',
          contextWindowUsedBefore: 950_000,
          contextWindowUsedAfter: 400_000,
          contextWindowSize: 1_000_000,
          summary: 'Kept the original task instructions.',
        },
        createdAt: '2024-01-01T00:00:05Z',
      }),
    ]

    const rounds = reconstructRoundsFromEvents(events)

    expect(rounds).toHaveLength(1)
    expect(rounds[0].compactions).toHaveLength(1)
    const compaction = rounds[0].compactions[0]
    expect(compaction.strategy).toBe('summary')
    expect(compaction.contextWindowUsedBefore).toBe(950_000)
    expect(compaction.contextWindowUsedAfter).toBe(400_000)
    expect(compaction.contextWindowSize).toBe(1_000_000)
    expect(compaction.summary).toBe('Kept the original task instructions.')
    expect(compaction.recordedAt).toBe('2024-01-01T00:00:05Z')
  })

  it('also recognises compaction_event type as a compaction source', () => {
    const events = [
      makeSessionEvent({ sequence: 0, type: 'input', payload: { text: 'p' }, createdAt: '2024-01-01T00:00:00Z' }),
      makeSessionEvent({
        sequence: 1,
        type: 'compaction_event',
        payload: {
          strategy: 'summary',
          contextWindowUsedBefore: 500_000,
          contextWindowUsedAfter: 100_000,
        },
        createdAt: '2024-01-01T00:00:01Z',
      }),
    ]

    const rounds = reconstructRoundsFromEvents(events)
    expect(rounds).toHaveLength(1)
    expect(rounds[0].compactions).toHaveLength(1)
    expect(rounds[0].compactions[0].contextWindowUsedBefore).toBe(500_000)
    expect(rounds[0].compactions[0].contextWindowUsedAfter).toBe(100_000)
  })

  it('attaches orphan compaction events (no preceding input) to a synthesised round', () => {
    const events = [
      makeSessionEvent({
        sequence: 0,
        type: 'compaction',
        payload: {
          contextWindowUsedBefore: 800_000,
          contextWindowUsedAfter: 200_000,
        },
        createdAt: '2024-01-01T00:00:00Z',
      }),
    ]

    const rounds = reconstructRoundsFromEvents(events)
    expect(rounds).toHaveLength(1)
    expect(rounds[0].compactions).toHaveLength(1)
    expect(rounds[0].agentText).toBe('')
    expect(rounds[0].userText).toBe('')
  })

  it('records multiple compaction events in the same round in stream order', () => {
    const events = [
      makeSessionEvent({ sequence: 0, type: 'input', payload: { text: 'p' }, createdAt: '2024-01-01T00:00:00Z' }),
      makeSessionEvent({
        sequence: 1,
        type: 'compaction',
        payload: { contextWindowUsedBefore: 900_000, contextWindowUsedAfter: 400_000 },
        createdAt: '2024-01-01T00:00:05Z',
      }),
      makeSessionEvent({
        sequence: 2,
        type: 'compaction',
        payload: { contextWindowUsedBefore: 800_000, contextWindowUsedAfter: 350_000 },
        createdAt: '2024-01-01T00:00:30Z',
      }),
    ]

    const rounds = reconstructRoundsFromEvents(events)
    expect(rounds).toHaveLength(1)
    expect(rounds[0].compactions).toHaveLength(2)
    expect(rounds[0].compactions[0].contextWindowUsedAfter).toBe(400_000)
    expect(rounds[0].compactions[1].contextWindowUsedAfter).toBe(350_000)
  })
})

describe('useSessionTimeline context health events', () => {
  it('uses server-provided usage.updated percent and healthStatus without deriving from window ratio', async () => {
    const hook = renderTimelineHook()

    act(() => {
      dispatchAgentEvent('usage.updated', {
        coderSessionId: 'coder-1',
        acpSessionId: 'acp-1',
        contextWindowUsed: 45_000,
        contextWindowSize: 100_000,
        contextUsagePercent: 72,
        healthStatus: 'red',
      })
    })

    expect(hook.result.current.contextHealth).toMatchObject({
      status: 'red',
      contextWindowUsed: 45_000,
      contextWindowSize: 100_000,
      contextUsagePercent: 72,
    })
  })

  it('does not fabricate health status from context_health_update when healthStatus is absent', () => {
    const hook = renderTimelineHook()

    act(() => {
      dispatchAgentEvent('context_health_update', {
        coderSessionId: 'coder-1',
        acpSessionId: 'acp-1',
        contextWindowUsed: 72_000,
        contextWindowSize: 100_000,
        contextUsagePercent: 72,
        healthStatus: undefined as unknown as 'green',
      })
    })

    expect(hook.result.current.contextHealth).toMatchObject({
      status: null,
      contextWindowUsed: 72_000,
      contextWindowSize: 100_000,
      contextUsagePercent: 72,
    })
  })

  it('records compaction window counts without deriving percent or health status', () => {
    const hook = renderTimelineHook()

    act(() => {
      dispatchAgentEvent('compaction_event', {
        coderSessionId: 'coder-1',
        acpSessionId: 'acp-1',
        contextWindowUsedAfter: 90_000,
        contextWindowSize: 100_000,
        recordedAt: '2024-01-01T00:00:00.000Z',
      })
    })

    expect(hook.result.current.contextHealth).toMatchObject({
      status: null,
      contextWindowUsed: 90_000,
      contextWindowSize: 100_000,
      contextUsagePercent: null,
    })
  })
})
