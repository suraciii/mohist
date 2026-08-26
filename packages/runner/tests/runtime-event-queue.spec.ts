import { describe, expect, it, vi } from 'vitest'
import { createAgentSessionRuntimeEventQueue, type RuntimeEventRecord } from '../src/server/runtime-event-queue.js'

function event(id: string, type: string): RuntimeEventRecord {
  return {
    id,
    producerFamily: 'binding-reconcile',
    target: { kind: 'session', sessionId: 'session-1' },
    runtimeSessionId: 'runtime-1',
    work: null,
    event: { type, payload: {} },
    acknowledgementPolicy: 'successful-response',
  }
}

describe('in-memory runtime event queue', () => {
  it('PreservesPerSessionOrderWhileAliveAndDropsMissingCrashSuffix', async () => {
    const delivered: string[] = []
    let fail = false
    const queue = createAgentSessionRuntimeEventQueue({
      retryDelayMs: 60_000,
      deliver: {
        async send(record) {
          if (fail) throw new Error('server unavailable')
          delivered.push(record.event.type)
          return [{ type: record.event.type }]
        },
      },
    })
    await queue.load()
    await queue.enqueueProducedFact(event('one', 'message.delta'))
    fail = true
    await queue.enqueueProducedFact(event('two', 'session.activity'))
    await queue.kick()

    expect(delivered).toEqual(['message.delta'])
    expect(queue.snapshot().map((record) => record.event.type)).toEqual(['session.activity'])

    await queue.stop()
    const restarted = createAgentSessionRuntimeEventQueue({ deliver: { send: vi.fn(async () => []) } })
    await restarted.load()
    expect(restarted.snapshot()).toEqual([])
  })
})
