import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AgentSessionRuntimeEventReceipt } from '../src/server/connection.js'
import { flushMicrotasks, inputRecord, makeOutbox, workflowFact } from './support/runtime-event-outbox-fixture.js'

beforeEach(() => {
  vi.useFakeTimers()
})

afterEach(() => {
  vi.useRealTimers()
})

describe('AgentSessionRuntimeEventOutbox delivery liveness', () => {
  it('isolates a non-settling batch without replaying it before its late receipt', async () => {
    let resolveStalled!: (receipts: AgentSessionRuntimeEventReceipt[][]) => void
    const stalled = new Promise<AgentSessionRuntimeEventReceipt[][]>((resolve) => {
      resolveStalled = resolve
    })
    const batches: string[][] = []
    const stalledSignals: AbortSignal[] = []
    const { outbox } = makeOutbox({
      deliveryTimeoutMs: 10,
      retryDelayMs: 10,
      boundedConcurrency: 1,
      deliver: {
        async send() {
          throw new Error('batched delivery expected')
        },
        sendBatch(records, signal) {
          const ids = records.map((record) => record.id)
          batches.push(ids)
          if (ids[0] === 'stalled-input') {
            stalledSignals.push(signal)
            return stalled
          }
          return Promise.resolve(records.map((record) => [{ type: record.event.type }]))
        },
      },
    })
    await outbox.load()
    const awaitReceipt = outbox.awaitInputReceipt
    if (!awaitReceipt) throw new Error('outbox must support Workflow input receipts')

    await outbox.enqueueBeforeExecution(inputRecord({ id: 'stalled-input' }))
    const receipt = awaitReceipt.call(outbox, 'stalled-input')
    const receiptError = receipt.then(
      () => null,
      (error: unknown) => error,
    )
    await outbox.enqueueProducedFact(
      workflowFact('other-group', {
        target: { kind: 'workflow', projectId: 'proj-1', workflowRunId: 'wf-other', sessionName: 'build' },
      }),
    )
    await flushMicrotasks()
    expect(batches).toEqual([['stalled-input']])

    await vi.advanceTimersByTimeAsync(20)
    await flushMicrotasks()

    expect(batches).toEqual([['stalled-input'], ['other-group']])
    expect(stalledSignals).toHaveLength(1)
    expect(stalledSignals[0]?.aborted).toBe(true)
    expect(outbox.snapshot().map((record) => record.id)).toEqual(['stalled-input'])
    await expect(receiptError).resolves.toMatchObject({
      message: expect.stringMatching(/runtime-event delivery timeout/),
    })

    await vi.advanceTimersByTimeAsync(100)
    expect(batches.filter((batch) => batch[0] === 'stalled-input')).toHaveLength(1)

    resolveStalled([
      [
        {
          type: 'session.input',
          inputDeliveryId: 'stalled-input',
          agentTurnId: 'turn-1',
          agentSessionId: 'agent-session-1',
        },
      ],
    ])
    await flushMicrotasks(12)

    expect(outbox.snapshot()).toEqual([])
    expect(batches.filter((batch) => batch[0] === 'stalled-input')).toHaveLength(1)
    await expect(awaitReceipt.call(outbox, 'stalled-input')).resolves.toEqual({
      type: 'session.input',
      inputDeliveryId: 'stalled-input',
      agentTurnId: 'turn-1',
      agentSessionId: 'agent-session-1',
    })
    await outbox.stop()
  })
})
