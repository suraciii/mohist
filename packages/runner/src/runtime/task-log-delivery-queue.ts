import type { RunnerOptions } from '../core/types.js'
import type { ServerConnection } from '../server/connection.js'
import { runnerLogger } from '../system/logger.js'
import type { TaskLogBatch } from './task-log.js'

const log = runnerLogger.child('task-log-queue')

export const DEFAULT_TASK_LOG_QUEUE_CAPACITY = 256
export const DEFAULT_TASK_LOG_DELIVERY_ATTEMPTS = 3
export const DEFAULT_TASK_LOG_RETRY_DELAY_MS = 2_000

export interface TaskLogDeliveryRecord {
  readonly ownerId: string
  readonly ownerKind: 'workflow' | 'agent-job'
  readonly workId: string
  readonly batch: TaskLogBatch
  readonly terminal: boolean
  readonly timeoutMs: number
}

export interface TaskLogDeliveryQueueOptions {
  readonly connection: Pick<ServerConnection, 'uploadTaskLog'>
  readonly capacity?: number
  readonly maxAttempts?: number
  readonly retryDelayMs?: number
  readonly warn?: (message: string, fields: Record<string, unknown>) => void
}

export interface TaskLogDeliveryQueue {
  enqueue(record: TaskLogDeliveryRecord): boolean
  stop(): Promise<void>
  snapshot(): readonly TaskLogDeliveryRecord[]
}

interface PendingDelivery {
  readonly record: TaskLogDeliveryRecord
  attempts: number
  retry: ReturnType<typeof setTimeout> | null
  wakeRetry: (() => void) | null
}

export function createTaskLogDeliveryQueue(options: TaskLogDeliveryQueueOptions): TaskLogDeliveryQueue {
  return new InMemoryTaskLogDeliveryQueue(options)
}

export function createHostTaskLogDeliveryQueue(
  connection: ServerConnection,
  _options: RunnerOptions,
): TaskLogDeliveryQueue {
  return createTaskLogDeliveryQueue({ connection })
}

class InMemoryTaskLogDeliveryQueue implements TaskLogDeliveryQueue {
  private readonly connection: Pick<ServerConnection, 'uploadTaskLog'>
  private readonly capacity: number
  private readonly maxAttempts: number
  private readonly retryDelayMs: number
  private readonly warn: (message: string, fields: Record<string, unknown>) => void
  private readonly groups = new Map<string, PendingDelivery[]>()
  private readonly draining = new Map<string, Promise<void>>()
  private readonly activeControllers = new Set<AbortController>()
  private size = 0
  private stopped = false

  constructor(options: TaskLogDeliveryQueueOptions) {
    this.connection = options.connection
    this.capacity = Math.max(1, Math.floor(options.capacity ?? DEFAULT_TASK_LOG_QUEUE_CAPACITY))
    this.maxAttempts = Math.max(1, Math.floor(options.maxAttempts ?? DEFAULT_TASK_LOG_DELIVERY_ATTEMPTS))
    this.retryDelayMs = Math.max(1, Math.floor(options.retryDelayMs ?? DEFAULT_TASK_LOG_RETRY_DELAY_MS))
    this.warn = options.warn ?? ((message, fields) => log.warn(message, fields))
  }

  enqueue(record: TaskLogDeliveryRecord): boolean {
    if (this.stopped) {
      this.warn('task-log evidence dropped because the volatile queue is stopped', fields(record))
      return false
    }
    if (this.size >= this.capacity) {
      this.warn('task-log evidence dropped because the volatile queue is full', {
        ...fields(record),
        capacity: this.capacity,
        policy: 'drop-newest',
      })
      return false
    }

    let group = this.groups.get(record.workId)
    if (!group) {
      group = []
      this.groups.set(record.workId, group)
    }
    group.push({ record, attempts: 0, retry: null, wakeRetry: null })
    this.size += 1
    this.kick(record.workId)
    return true
  }

  snapshot(): readonly TaskLogDeliveryRecord[] {
    return [...this.groups.values()].flatMap((group) => group.map((entry) => entry.record))
  }

  async stop(): Promise<void> {
    if (this.stopped) return
    this.stopped = true
    for (const group of this.groups.values()) {
      for (const entry of group) {
        if (entry.retry) clearTimeout(entry.retry)
        entry.wakeRetry?.()
      }
    }
    for (const controller of this.activeControllers) controller.abort(new Error('task-log queue stopped'))
    const pending = this.size
    this.groups.clear()
    this.size = 0
    if (pending > 0) this.warn('task-log evidence dropped when the volatile queue stopped', { count: pending })
    await Promise.allSettled(this.draining.values())
  }

  private kick(workId: string): void {
    if (this.stopped || this.draining.has(workId)) return
    const drain = this.drain(workId).finally(() => {
      this.draining.delete(workId)
      if (!this.stopped && (this.groups.get(workId)?.length ?? 0) > 0) this.kick(workId)
    })
    this.draining.set(workId, drain)
  }

  private async drain(workId: string): Promise<void> {
    while (!this.stopped) {
      const group = this.groups.get(workId)
      const entry = group?.[0]
      if (!group || !entry) return
      entry.attempts += 1
      if (await this.deliver(entry.record)) {
        this.retire(workId, entry)
        continue
      }
      if (this.stopped) return
      if (entry.attempts >= this.maxAttempts) {
        this.warn('task-log evidence dropped after bounded delivery attempts', {
          ...fields(entry.record),
          attempts: entry.attempts,
        })
        this.retire(workId, entry)
        continue
      }
      await new Promise<void>((resolve) => {
        const wake = () => {
          entry.retry = null
          entry.wakeRetry = null
          resolve()
        }
        entry.wakeRetry = wake
        entry.retry = setTimeout(wake, this.retryDelayMs)
        entry.retry.unref?.()
      })
    }
  }

  private async deliver(record: TaskLogDeliveryRecord): Promise<boolean> {
    const controller = new AbortController()
    this.activeControllers.add(controller)
    let timeout: ReturnType<typeof setTimeout> | null = null
    try {
      await Promise.race([
        this.connection.uploadTaskLog(
          record.ownerId,
          record.workId,
          record.batch,
          controller.signal,
          record.ownerKind,
          record.terminal,
        ),
        new Promise<never>((_resolve, reject) => {
          controller.signal.addEventListener(
            'abort',
            () => reject(controller.signal.reason ?? new Error('task-log upload aborted')),
            { once: true },
          )
          timeout = setTimeout(
            () => controller.abort(new Error(`task-log upload timed out after ${record.timeoutMs}ms`)),
            Math.max(1, record.timeoutMs),
          )
          timeout.unref?.()
        }),
      ])
      return true
    } catch {
      return false
    } finally {
      if (timeout) clearTimeout(timeout)
      this.activeControllers.delete(controller)
    }
  }

  private retire(workId: string, entry: PendingDelivery): void {
    const group = this.groups.get(workId)
    if (!group || group[0] !== entry) return
    group.shift()
    this.size -= 1
    if (group.length === 0) this.groups.delete(workId)
  }
}

function fields(record: TaskLogDeliveryRecord): Record<string, unknown> {
  return {
    work: record.workId,
    path: record.terminal ? 'terminal' : 'incremental',
    firstSequence: record.batch.entries[0]?.seq ?? null,
    lastSequence: record.batch.entries.at(-1)?.seq ?? null,
  }
}
