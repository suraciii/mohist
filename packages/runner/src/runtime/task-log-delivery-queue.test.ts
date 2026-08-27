import { afterEach, describe, expect, it, vi } from 'vitest'
import { createTaskLogDeliveryQueue, type TaskLogDeliveryRecord } from './task-log-delivery-queue.js'

function record(id: string, sequence: number, terminal = false): TaskLogDeliveryRecord {
  return {
    ownerId: 'workflow-1',
    ownerKind: 'workflow',
    workId: id,
    batch: {
      entries: [
        { seq: sequence, timestamp: new Date('2026-01-01T00:00:00.000Z'), source: 'test', text: `${sequence}` },
      ],
      truncated: false,
    },
    terminal,
    timeoutMs: 1_000,
  }
}

async function flush(): Promise<void> {
  for (let index = 0; index < 8; index += 1) await Promise.resolve()
}

afterEach(() => vi.useRealTimers())

describe('volatile task-log delivery queue', () => {
  it('retries a transient failure and then succeeds', async () => {
    vi.useFakeTimers()
    const attempts: number[] = []
    const queue = createTaskLogDeliveryQueue({
      retryDelayMs: 100,
      connection: {
        uploadTaskLog: vi.fn(async (_owner, _work, batch) => {
          attempts.push(batch.entries[0]!.seq)
          if (attempts.length === 1) throw new Error('transient')
          return { status: 'changed' as const, accepted: 1, truncated: false }
        }),
      },
    })

    expect(queue.enqueue(record('work-1', 1))).toBe(true)
    await flush()
    expect(queue.snapshot()).toHaveLength(1)
    await vi.advanceTimersByTimeAsync(100)
    expect(attempts).toEqual([1, 1])
    expect(queue.snapshot()).toEqual([])
    await queue.stop()
  })

  it('preserves incremental-before-terminal order for one work across retry', async () => {
    vi.useFakeTimers()
    const delivered: number[] = []
    let failed = false
    const queue = createTaskLogDeliveryQueue({
      retryDelayMs: 100,
      connection: {
        uploadTaskLog: vi.fn(async (_owner, _work, batch) => {
          const sequence = batch.entries[0]!.seq
          delivered.push(sequence)
          if (sequence === 1 && !failed) {
            failed = true
            throw new Error('retry first')
          }
          return { status: 'changed' as const, accepted: 1, truncated: false }
        }),
      },
    })

    queue.enqueue(record('work-1', 1))
    queue.enqueue(record('work-1', 2, true))
    await flush()
    expect(delivered).toEqual([1])
    await vi.advanceTimersByTimeAsync(100)
    expect(delivered).toEqual([1, 1, 2])
    expect(queue.snapshot()).toEqual([])
    await queue.stop()
  })

  it('keeps one per-work lease after timeout and accepts a late ignored-abort completion', async () => {
    vi.useFakeTimers()
    const attempts: number[] = []
    let complete!: (value: { status: 'changed'; accepted: number; truncated: boolean }) => void
    const original = new Promise<{ status: 'changed'; accepted: number; truncated: boolean }>((resolve) => {
      complete = resolve
    })
    const queue = createTaskLogDeliveryQueue({
      retryDelayMs: 100,
      connection: {
        uploadTaskLog: vi.fn(async (_owner, _work, batch) => {
          attempts.push(batch.entries[0]!.seq)
          return await original
        }),
      },
    })

    queue.enqueue({ ...record('work-1', 1), timeoutMs: 50 })
    queue.enqueue(record('work-1', 2, true))
    await vi.advanceTimersByTimeAsync(550)
    expect(attempts).toEqual([1])
    expect(queue.snapshot()).toHaveLength(2)

    complete({ status: 'changed', accepted: 1, truncated: false })
    await flush()
    expect(attempts).toEqual([1, 2])
    expect(queue.snapshot()).toEqual([])
    await queue.stop()
  })

  it('retries only after the timed-out ignored-abort upload fails late', async () => {
    vi.useFakeTimers()
    const attempts: number[] = []
    let fail!: (error: Error) => void
    const original = new Promise<never>((_resolve, reject) => {
      fail = reject
    })
    let calls = 0
    const queue = createTaskLogDeliveryQueue({
      retryDelayMs: 100,
      connection: {
        uploadTaskLog: vi.fn(async (_owner, _work, batch) => {
          attempts.push(batch.entries[0]!.seq)
          calls += 1
          if (calls === 1) return await original
          return { status: 'changed' as const, accepted: 1, truncated: false }
        }),
      },
    })

    queue.enqueue({ ...record('work-1', 1), timeoutMs: 50 })
    await vi.advanceTimersByTimeAsync(550)
    expect(attempts).toEqual([1])

    fail(new Error('late failure'))
    await flush()
    await vi.advanceTimersByTimeAsync(99)
    expect(attempts).toEqual([1])
    await vi.advanceTimersByTimeAsync(1)
    expect(attempts).toEqual([1, 1])
    expect(queue.snapshot()).toEqual([])
    await queue.stop()
  })

  it('stops without waiting for an ignored-abort upload and ignores its late completion', async () => {
    vi.useFakeTimers()
    let complete!: (value: { status: 'changed'; accepted: number; truncated: boolean }) => void
    const original = new Promise<{ status: 'changed'; accepted: number; truncated: boolean }>((resolve) => {
      complete = resolve
    })
    const queue = createTaskLogDeliveryQueue({
      connection: {
        uploadTaskLog: vi.fn(async () => await original),
      },
    })

    queue.enqueue({ ...record('work-1', 1), timeoutMs: 50 })
    await vi.advanceTimersByTimeAsync(50)
    await expect(queue.stop()).resolves.toBeUndefined()
    expect(queue.snapshot()).toEqual([])
    complete({ status: 'changed', accepted: 1, truncated: false })
    await flush()
    expect(queue.snapshot()).toEqual([])
  })

  it('bounds queue growth, drops after bounded attempts, and drops pending evidence on stop', async () => {
    vi.useFakeTimers()
    const warnings: Array<{ message: string; fields: Record<string, unknown> }> = []
    const queue = createTaskLogDeliveryQueue({
      capacity: 2,
      maxAttempts: 2,
      retryDelayMs: 100,
      warn: (message, fields) => warnings.push({ message, fields }),
      connection: {
        uploadTaskLog: vi.fn(async () => {
          throw new Error('offline')
        }),
      },
    })

    expect(queue.enqueue(record('work-1', 1))).toBe(true)
    expect(queue.enqueue(record('work-1', 2, true))).toBe(true)
    expect(queue.enqueue(record('work-2', 1))).toBe(false)
    await flush()
    await vi.advanceTimersByTimeAsync(100)
    expect(warnings).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ message: 'task-log evidence dropped because the volatile queue is full' }),
        expect.objectContaining({ message: 'task-log evidence dropped after bounded delivery attempts' }),
      ]),
    )
    expect(queue.snapshot()).toHaveLength(1)
    await queue.stop()
    expect(queue.snapshot()).toEqual([])
    expect(warnings).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ message: 'task-log evidence dropped when the volatile queue stopped' }),
      ]),
    )
  })
})
