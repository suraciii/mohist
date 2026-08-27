import { afterEach, describe, expect, it, vi } from 'vitest'
import { RuntimeEventDeliveryError, type AgentSessionRuntimeEventReceipt } from '../src/server/connection.js'
import {
  AlreadyConsumedRuntimeEventError,
  createAgentSessionRuntimeEventQueue,
  type RuntimeEventRecord,
} from '../src/server/runtime-event-queue.js'

function event(id: string, sessionId: string, type = id, turnId = `turn-${sessionId}`): RuntimeEventRecord {
  return {
    id,
    producerFamily: 'session-followup',
    target: { kind: 'session', sessionId },
    runtimeSessionId: `runtime-${sessionId}`,
    sessionTurnId: turnId,
    work: null,
    event: { type, payload: {} },
    acknowledgementPolicy: 'successful-response',
  }
}

function input(id: string, sessionId: string): RuntimeEventRecord {
  return {
    id,
    producerFamily: 'session-followup',
    target: { kind: 'session', sessionId },
    runtimeSessionId: `runtime-${sessionId}`,
    sessionTurnId: `turn-${id}`,
    work: null,
    event: { type: 'session.input', payload: {} },
    acknowledgementPolicy: 'matching-receipt',
  }
}

async function flush(): Promise<void> {
  await Promise.resolve()
  await Promise.resolve()
}

afterEach(() => {
  vi.useRealTimers()
})

describe('in-memory runtime event queue', () => {
  it('preserves exact per-group FIFO for interleaved A1/B1/A2 records', async () => {
    const delivered: string[] = []
    const queue = createAgentSessionRuntimeEventQueue({
      deliveryBatchSize: 1,
      deliver: {
        async send(record) {
          delivered.push(record.id)
          return []
        },
      },
    })

    await queue.enqueueProducedFactBatch([event('A1', 'A'), event('B1', 'B'), event('A2', 'A')])
    await queue.kick()

    expect(delivered).toEqual(['A1', 'B1', 'A2'])
    expect(queue.snapshot()).toEqual([])
    await queue.stop()
  })

  it('does not let a later Session turn overtake a retrying earlier turn', async () => {
    vi.useFakeTimers()
    const delivered: string[] = []
    let failFirst = true
    const queue = createAgentSessionRuntimeEventQueue({
      retryDelayMs: 100,
      deliveryBatchSize: 1,
      deliver: {
        async send(record) {
          delivered.push(record.id)
          if (record.id === 'turn-1' && failFirst) throw new Error('retry turn 1')
          return []
        },
      },
    })

    await queue.enqueueProducedFactBatch([
      event('turn-1', 'session-1', 'message.delta', 'turn-1'),
      event('turn-2', 'session-1', 'message.delta', 'turn-2'),
    ])
    await queue.kick()
    expect(delivered).toEqual(['turn-1'])

    failFirst = false
    await vi.advanceTimersByTimeAsync(100)
    expect(delivered).toEqual(['turn-1', 'turn-1', 'turn-2'])
    await queue.stop()
  })

  it('moves fairly after repeated failures and successes in another group', async () => {
    vi.useFakeTimers()
    const delivered: string[] = []
    let failA = true
    const queue = createAgentSessionRuntimeEventQueue({
      retryDelayMs: 100,
      deliveryBatchSize: 1,
      deliver: {
        async send(record) {
          delivered.push(record.id)
          if (record.id.startsWith('A') && failA) throw new Error('retry A')
          return []
        },
      },
    })

    await queue.enqueueProducedFactBatch([event('A1', 'A'), event('B1', 'B'), event('A2', 'A'), event('B2', 'B')])
    await queue.kick()
    expect(delivered).toEqual(['A1', 'B1', 'B2'])
    expect(queue.snapshot().map((record) => record.id)).toEqual(['A1', 'A2'])

    failA = false
    await vi.advanceTimersByTimeAsync(100)
    expect(delivered).toEqual(['A1', 'B1', 'B2', 'A1', 'A2'])
    await queue.stop()
  })

  it('bounds a never-settling group and advances unrelated groups', async () => {
    vi.useFakeTimers()
    const delivered: string[] = []
    const queue = createAgentSessionRuntimeEventQueue({
      deliveryTimeoutMs: 50,
      retryDelayMs: 1_000,
      deliveryBatchSize: 1,
      deliver: {
        async send(record) {
          delivered.push(record.id)
          if (record.id === 'A1') return await new Promise<AgentSessionRuntimeEventReceipt[]>(() => {})
          return []
        },
      },
    })

    await queue.enqueueProducedFactBatch([event('A1', 'A'), event('B1', 'B')])
    const drain = queue.kick()
    await vi.advanceTimersByTimeAsync(50)
    await drain

    expect(delivered).toEqual(['A1', 'B1'])
    expect(queue.snapshot().map((record) => record.id)).toEqual(['A1'])
    await queue.stop()
  })

  it('drops the newest suffix deterministically at the configured ceiling', async () => {
    const warnings: Array<Record<string, unknown>> = []
    const queue = createAgentSessionRuntimeEventQueue({
      queueCapacity: 2,
      warn: (_message, fields) => warnings.push(fields),
      deliver: {
        async send() {
          return []
        },
      },
    })

    await queue.enqueueProducedFactBatch([event('A1', 'A'), event('A2', 'A'), event('A3', 'A')])

    expect(queue.snapshot().map((record) => record.id)).toEqual(['A1', 'A2'])
    expect(warnings).toEqual([expect.objectContaining({ recordId: 'A3', capacity: 2, policy: 'drop-newest' })])
    await queue.stop()
  })

  it('retires explicit permanent refusal but retries malformed and mismatched receipts', async () => {
    vi.useFakeTimers()
    const warnings: string[] = []
    let mode: 'refused' | 'empty' | 'malformed' | 'mismatch' | 'accepted' = 'refused'
    const queue = createAgentSessionRuntimeEventQueue({
      retryDelayMs: 100,
      warn: (message) => warnings.push(message),
      deliver: {
        async send(record) {
          if (mode === 'refused') throw new RuntimeEventDeliveryError('runtime event', 409, 'conflict', '')
          if (mode === 'empty') return []
          if (mode === 'malformed') return [{} as AgentSessionRuntimeEventReceipt]
          if (mode === 'mismatch') return [{ type: 'message.delta' }]
          return [{ type: record.event.type }]
        },
      },
    })

    await queue.enqueueProducedFact(event('refused', 'A'))
    await queue.kick()
    expect(queue.snapshot()).toEqual([])
    expect(warnings).toContain('runtime-event evidence permanently refused and dropped')

    mode = 'empty'
    await queue.enqueueProducedFact(input('malformed', 'B'))
    await queue.kick()
    expect(queue.snapshot().map((record) => record.id)).toEqual(['malformed'])

    mode = 'malformed'
    await vi.advanceTimersByTimeAsync(100)
    expect(queue.snapshot().map((record) => record.id)).toEqual(['malformed'])

    mode = 'mismatch'
    await vi.advanceTimersByTimeAsync(100)
    expect(queue.snapshot().map((record) => record.id)).toEqual(['malformed'])

    mode = 'accepted'
    await vi.advanceTimersByTimeAsync(100)
    expect(queue.snapshot()).toEqual([])
    await queue.stop()
  })

  it('lets a reconnect waiter progress while another group is stalled', async () => {
    vi.useFakeTimers()
    const queue = createAgentSessionRuntimeEventQueue({
      deliveryTimeoutMs: 50,
      retryDelayMs: 1_000,
      deliveryBatchSize: 1,
      deliver: {
        async send(record) {
          if (record.id === 'stalled') return await new Promise<AgentSessionRuntimeEventReceipt[]>(() => {})
          return [{ type: 'session.input' }]
        },
      },
    })

    await queue.enqueueProducedFactBatch([event('stalled', 'A'), input('reconnect', 'B')])
    const receipt = queue.awaitInputReceipt!('reconnect')
    await vi.advanceTimersByTimeAsync(50)

    await expect(receipt).resolves.toEqual({ type: 'session.input' })
    expect(queue.snapshot().map((record) => record.id)).toEqual(['stalled'])
    await queue.stop()
  })

  it('coalesces duplicate input receipt waiters before delivery', async () => {
    let release!: (receipts: AgentSessionRuntimeEventReceipt[]) => void
    const delivery = new Promise<AgentSessionRuntimeEventReceipt[]>((resolve) => {
      release = resolve
    })
    const queue = createAgentSessionRuntimeEventQueue({
      deliver: {
        async send() {
          return await delivery
        },
      },
    })

    await queue.enqueueBeforeExecution(input('shared-receipt', 'A'))
    const first = queue.awaitInputReceipt!('shared-receipt')
    const second = queue.awaitInputReceipt!('shared-receipt')
    release([{ type: 'session.input' }])

    await expect(Promise.all([first, second])).resolves.toEqual([{ type: 'session.input' }, { type: 'session.input' }])
    await expect(queue.awaitInputReceipt!('shared-receipt')).rejects.toBeInstanceOf(AlreadyConsumedRuntimeEventError)
    await queue.stop()
  })

  it('rejects duplicate waiters together on permanent refusal and removes their state', async () => {
    const queue = createAgentSessionRuntimeEventQueue({
      warn: () => undefined,
      deliver: {
        async send() {
          throw new RuntimeEventDeliveryError('runtime event', 409, 'conflict', '')
        },
      },
    })

    await queue.enqueueBeforeExecution(input('refused-input', 'A'))
    const first = queue.awaitInputReceipt!('refused-input')
    const second = queue.awaitInputReceipt!('refused-input')

    await expect(first).rejects.toBeInstanceOf(AlreadyConsumedRuntimeEventError)
    await expect(second).rejects.toBeInstanceOf(AlreadyConsumedRuntimeEventError)
    await expect(queue.awaitInputReceipt!('refused-input')).rejects.toBeInstanceOf(AlreadyConsumedRuntimeEventError)
    await queue.stop()
  })

  it('rejects every coalesced waiter on shutdown and removes their state', async () => {
    const queue = createAgentSessionRuntimeEventQueue({
      retryDelayMs: 60_000,
      deliver: {
        async send() {
          throw new Error('retry later')
        },
      },
    })

    await queue.enqueueBeforeExecution(input('stopped-input', 'A'))
    const first = queue.awaitInputReceipt!('stopped-input')
    const second = queue.awaitInputReceipt!('stopped-input')
    await flush()
    await queue.stop()

    await expect(first).rejects.toThrow('runtime-event queue stopped')
    await expect(second).rejects.toThrow('runtime-event queue stopped')
    await expect(queue.awaitInputReceipt!('stopped-input')).rejects.toThrow('runtime-event queue is stopped')
  })

  it('retires a successful input receipt when no waiter exists instead of retaining it', async () => {
    const queue = createAgentSessionRuntimeEventQueue({
      deliver: {
        async send() {
          return [{ type: 'session.input' }]
        },
      },
    })

    await queue.enqueueProducedFact(input('orphan-receipt', 'A'))
    await queue.kick()
    expect(queue.snapshot()).toEqual([])

    await expect(queue.awaitInputReceipt!('orphan-receipt')).rejects.toBeInstanceOf(AlreadyConsumedRuntimeEventError)
    await queue.stop()
  })

  it('admits a receipt-bearing input when the ordinary evidence lane is full', async () => {
    vi.useFakeTimers()
    const queue = createAgentSessionRuntimeEventQueue({
      queueCapacity: 1,
      admissionCapacity: 1,
      retryDelayMs: 100,
      warn: () => undefined,
      deliver: {
        async send(record) {
          if (record.id === 'full') throw new Error('hold ordinary evidence')
          return [{ type: 'session.input' }]
        },
      },
    })

    await queue.enqueueProducedFact(event('full', 'A'))
    await queue.enqueueBeforeExecution(input('admitted-input', 'B'))
    const receipt = queue.awaitInputReceipt!('admitted-input')
    await queue.kick()
    await expect(receipt).resolves.toEqual({ type: 'session.input' })
    expect(queue.snapshot().map((record) => record.id)).toEqual(['full'])
    await queue.stop()
  })

  it('rejects a second receipt-bearing input when the bounded admission lane is full', async () => {
    const queue = createAgentSessionRuntimeEventQueue({
      queueCapacity: 1,
      admissionCapacity: 1,
      retryDelayMs: 60_000,
      warn: () => undefined,
      deliver: {
        async send() {
          throw new Error('hold admission')
        },
      },
    })

    await queue.enqueueBeforeExecution(input('first-input', 'A'))
    await expect(queue.enqueueBeforeExecution(input('second-input', 'B'))).rejects.toBeInstanceOf(
      AlreadyConsumedRuntimeEventError,
    )
    expect(queue.snapshot().map((record) => record.id)).toEqual(['first-input'])
    await queue.stop()
  })
})
