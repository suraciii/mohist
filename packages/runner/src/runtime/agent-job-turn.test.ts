import { describe, expect, it, vi } from 'vitest'
import { createAgentSessionEventSink } from './agent-job-turn.js'
import type { ServerConnection } from '../server/connection.js'
import type { DispatchWorkItem } from '../core/types.js'

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
