import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AgentSessionRuntimeEventReceipt } from '../src/server/connection.js'
import { RUNTIME_EVENT_OUTBOX_FILE } from '../src/server/runtime-event-outbox.js'
import {
  RecordingFileSystem,
  flushMicrotasks,
  inputRecord,
  makeOutbox,
  workflowFact,
} from './support/runtime-event-outbox-fixture.js'

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

  it.each(['failure', 'non-matching receipt'] as const)(
    'retains a timed-out record after a late %s and retries only after the lease is released',
    async (lateOutcome) => {
      let resolveLate!: (receipts: AgentSessionRuntimeEventReceipt[][]) => void
      let rejectLate!: (error: Error) => void
      const late = new Promise<AgentSessionRuntimeEventReceipt[][]>((resolve, reject) => {
        resolveLate = resolve
        rejectLate = reject
      })
      const batches: string[][] = []
      let attempts = 0
      const fileSystem = new RecordingFileSystem()
      const { outbox } = makeOutbox({
        fileSystem,
        deliveryTimeoutMs: 10,
        retryDelayMs: 10,
        boundedConcurrency: 1,
        deliver: {
          async send() {
            throw new Error('batched delivery expected')
          },
          sendBatch(records) {
            batches.push(records.map((record) => record.id))
            attempts += 1
            if (attempts === 1) return late
            return Promise.resolve(
              records.map((record) => [
                {
                  type: record.event.type,
                  inputDeliveryId: record.id,
                  agentTurnId: 'turn-1',
                  agentSessionId: 'agent-session-1',
                },
              ]),
            )
          },
        },
      })
      await outbox.load()
      const awaitReceipt = outbox.awaitInputReceipt
      if (!awaitReceipt) throw new Error('outbox must support Workflow input receipts')

      await outbox.enqueueBeforeExecution(inputRecord({ id: 'late-input' }))
      const receipt = awaitReceipt.call(outbox, 'late-input')
      const receiptError = receipt.then(
        () => null,
        (error: unknown) => error,
      )
      await flushMicrotasks()
      expect(batches).toEqual([['late-input']])

      await vi.advanceTimersByTimeAsync(10)
      await flushMicrotasks()
      await expect(receiptError).resolves.toMatchObject({
        message: expect.stringMatching(/runtime-event delivery timeout/),
      })
      expect(outbox.snapshot().map((record) => record.id)).toEqual(['late-input'])

      await vi.advanceTimersByTimeAsync(100)
      await flushMicrotasks()
      expect(batches).toEqual([['late-input']])

      if (lateOutcome === 'failure') rejectLate(new Error('late transport failure'))
      else resolveLate([[{ type: 'message.delta' }]])
      await flushMicrotasks(12)

      // The late result neither loses the durable record nor starts a replay
      // before the original delivery lease has been released.
      expect(outbox.snapshot().map((record) => record.id)).toEqual(['late-input'])
      expect(batches).toEqual([['late-input']])

      await vi.advanceTimersByTimeAsync(10)
      await flushMicrotasks(12)
      expect(batches).toEqual([['late-input'], ['late-input']])
      expect(outbox.snapshot()).toEqual([])
      await outbox.stop()
    },
  )

  it('does not let a completion after stop mutate the snapshot or receipt state', async () => {
    let resolveLate!: (receipts: AgentSessionRuntimeEventReceipt[][]) => void
    const late = new Promise<AgentSessionRuntimeEventReceipt[][]>((resolve) => {
      resolveLate = resolve
    })
    const fileSystem = new RecordingFileSystem()
    const { outbox } = makeOutbox({
      fileSystem,
      deliveryTimeoutMs: 10,
      boundedConcurrency: 1,
      deliver: {
        async send() {
          throw new Error('batched delivery expected')
        },
        sendBatch() {
          return late
        },
      },
    })
    await outbox.load()
    const awaitReceipt = outbox.awaitInputReceipt
    if (!awaitReceipt) throw new Error('outbox must support Workflow input receipts')

    await outbox.enqueueBeforeExecution(inputRecord({ id: 'stopped-input' }))
    const receipt = awaitReceipt.call(outbox, 'stopped-input')
    const receiptError = receipt.then(
      () => null,
      (error: unknown) => error,
    )
    await flushMicrotasks()
    await vi.advanceTimersByTimeAsync(10)
    await flushMicrotasks()
    await expect(receiptError).resolves.toMatchObject({
      message: expect.stringMatching(/runtime-event delivery timeout/),
    })

    const snapshotBeforeStop = outbox.snapshot()
    const bodyBeforeStop = fileSystem.body(RUNTIME_EVENT_OUTBOX_FILE)
    await outbox.stop()

    resolveLate([
      [
        {
          type: 'session.input',
          inputDeliveryId: 'stopped-input',
          agentTurnId: 'turn-1',
          agentSessionId: 'agent-session-1',
        },
      ],
    ])
    await flushMicrotasks(12)

    expect(outbox.snapshot()).toEqual(snapshotBeforeStop)
    expect(fileSystem.body(RUNTIME_EVENT_OUTBOX_FILE)).toBe(bodyBeforeStop)
    await expect(awaitReceipt.call(outbox, 'stopped-input')).rejects.toThrow(/runtime-event delivery timeout/)
  })
})
