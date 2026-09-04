import { describe, expect, it, vi } from 'vitest'
import { collectUnknownKeys, createAgentSessionEventSink } from './agent-job-turn.js'
import type { ServerConnection } from '../server/connection.js'
import type { DispatchWorkItem } from '../core/types.js'

describe('collectUnknownKeys', () => {
  it('classifies the full Server-authored AgentJob payload as known', () => {
    const payload = {
      prompt: 'PI_MIGRATION_SMOKE_OK',
      instructions: 'keep replies minimal',
      model: 'minimax/MiniMax-M3',
      variant: null,
      reasoningEffort: null,
      runtime: 'pi',
      skills: [],
      attachments: [],
      executionSource: 'non-slack',
      slackExecutionContext: undefined,
    }
    expect(collectUnknownKeys(payload as never)).toBeUndefined()
  })

  it('does not classify a Slack execution source or context as unknown', () => {
    const payload = {
      prompt: 'hi',
      runtime: 'pi',
      executionSource: 'slack',
      slackExecutionContext: { version: 1, channel: 'c1', threadTs: '1.1' },
    }
    expect(collectUnknownKeys(payload)).toBeUndefined()
  })

  it('still reports genuinely unknown payload keys', () => {
    expect(collectUnknownKeys({ prompt: 'hi', rogueKey: 'x' })).toEqual(['rogueKey'])
  })

  it('returns undefined for a null payload', () => {
    expect(collectUnknownKeys(null)).toBeUndefined()
  })
})

// A stalled runtime-event delivery must never hold the post-turn drain (and
// therefore the work) forever: the drain is bounded and abandons the chain.
describe('createAgentSessionEventSink drain bound', () => {
  it('resolves drain when a delivery never settles', async () => {
    vi.useFakeTimers()
    try {
      const connection = {
        agentSessionRuntimeEvents: () => new Promise(() => {}),
      } as unknown as ServerConnection
      const work = {
        projectId: 'p1',
        workId: 'work-1',
        workType: 'task',
        stage: 'check',
        agentJobId: 'job-1',
      } as unknown as DispatchWorkItem
      const sink = createAgentSessionEventSink(connection, work, new AbortController().signal, 'session-1')
      sink.observePiEvent({
        id: 'evt-1',
        type: 'tool_call.started',
        runtimeSessionId: 'rt',
        workDir: '/w',
        payload: {},
      })
      await Promise.resolve()
      const drained = sink.drain()
      await vi.advanceTimersByTimeAsync(130_000)
      await expect(drained).resolves.toBeUndefined()
    } finally {
      vi.useRealTimers()
    }
  })
})
