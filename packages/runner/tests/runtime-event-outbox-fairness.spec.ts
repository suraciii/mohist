import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { flushMicrotasks, makeOutbox, workflowFact } from './support/runtime-event-outbox-fixture.js'

beforeEach(() => {
  vi.useFakeTimers()
})

afterEach(() => {
  vi.useRealTimers()
})

describe('AgentSessionRuntimeEventOutbox - cross-group fairness', () => {
  it('round-robins failed sequence groups so later groups are not starved', async () => {
    const order: string[] = []
    const records = Array.from({ length: 5 }, (_, index) => {
      const id = `wf-${index + 1}`
      return workflowFact(id, {
        target: { kind: 'workflow', projectId: 'proj-1', workflowRunId: `run-${index + 1}`, sessionName: 'build' },
      })
    })
    const permanentlyFailed = new Set(records.slice(0, 4).map((record) => record.id))
    const { outbox } = makeOutbox({
      boundedConcurrency: 4,
      retryDelayMs: 100,
      deliver: {
        async send(record) {
          order.push(record.id)
          if (permanentlyFailed.has(record.id)) throw new Error('sequence group unavailable')
          return [{ type: record.event.type }]
        },
      },
    })
    await outbox.load()
    await outbox.enqueueProducedFactBatch(records)

    await outbox.kick()
    expect(order).toEqual(['wf-1', 'wf-2', 'wf-3', 'wf-4'])
    expect(outbox.snapshot()).toHaveLength(5)

    await vi.advanceTimersByTimeAsync(100)
    await flushMicrotasks()

    expect(order.slice(0, 5)).toEqual(['wf-1', 'wf-2', 'wf-3', 'wf-4', 'wf-5'])
    expect(outbox.snapshot().map((record) => record.id)).not.toContain('wf-5')
  })
})
