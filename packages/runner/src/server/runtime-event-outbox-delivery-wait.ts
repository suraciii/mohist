import { cleanupPredecessorDeliveryKey, isCleanupPredecessorRecord } from './runtime-event-outbox-identity.js'
import type {
  CleanupPredecessorDeliveryTarget,
  CleanupPredecessorDeliveryWaitOptions,
  RuntimeEventOutboxTimer,
  RuntimeEventRecord,
} from './runtime-event-outbox-ports.js'

export class CleanupPredecessorDeliveryWaitTimeoutError extends Error {
  readonly projectId: string
  readonly workflowRunId: string
  readonly sessionName: string
  readonly cleanupAttempt: number
  readonly precedingCleanupOperationId: string | null
  readonly budgetMs: number

  constructor(target: CleanupPredecessorDeliveryTarget, budgetMs: number) {
    super(
      `cleanup predecessor delivery wait timed out after ${budgetMs}ms for ` +
        `${target.projectId}/${target.workflowRunId}/${target.sessionName} ` +
        `(attempt ${target.cleanupAttempt}${
          target.precedingCleanupOperationId === null ? '' : `, predecessor ${target.precedingCleanupOperationId}`
        })`,
    )
    this.name = 'CleanupPredecessorDeliveryWaitTimeoutError'
    this.projectId = target.projectId
    this.workflowRunId = target.workflowRunId
    this.sessionName = target.sessionName
    this.cleanupAttempt = target.cleanupAttempt
    this.precedingCleanupOperationId = target.precedingCleanupOperationId
    this.budgetMs = budgetMs
  }
}

interface Waiter {
  readonly target: CleanupPredecessorDeliveryTarget
  readonly key: string
  readonly resolve: () => void
  readonly reject: (error: Error) => void
  readonly abort: () => void
  readonly signal: AbortSignal
  timer: { unref(): void } | null
}

export class CleanupPredecessorDeliveryWaiters {
  private readonly waiters = new Map<string, Set<Waiter>>()

  constructor(
    private readonly timer: RuntimeEventOutboxTimer,
    private readonly hasRecords: (target: CleanupPredecessorDeliveryTarget) => boolean,
    private readonly kick: () => Promise<void>,
  ) {}

  async wait(target: CleanupPredecessorDeliveryTarget, options: CleanupPredecessorDeliveryWaitOptions): Promise<void> {
    if (options.signal.aborted) throw abortError(options.signal.reason)
    if (!this.hasRecords(target)) return
    const key = cleanupPredecessorDeliveryKey(target)
    const budgetMs = Math.max(0, options.budgetMs)
    return await new Promise<void>((resolve, reject) => {
      const waiter: Waiter = {
        target,
        key,
        resolve,
        reject,
        signal: options.signal,
        timer: null,
        abort: () => this.reject(waiter, abortError(options.signal.reason)),
      }
      waiter.timer = this.timer.setTimeout(
        () => this.reject(waiter, new CleanupPredecessorDeliveryWaitTimeoutError(target, options.budgetMs)),
        budgetMs,
      )
      const keyWaiters = this.waiters.get(key)
      if (keyWaiters) keyWaiters.add(waiter)
      else this.waiters.set(key, new Set([waiter]))
      options.signal.addEventListener('abort', waiter.abort, { once: true })
      void this.kick().catch(() => undefined)
    })
  }

  recordsRemoved(removed: readonly RuntimeEventRecord[]): void {
    const keys = new Set<string>()
    for (const record of removed) {
      if (record.producerFamily === 'workflow-session' && record.target.kind === 'workflow') {
        keys.add(
          cleanupPredecessorDeliveryKey({
            projectId: record.target.projectId,
            workflowRunId: record.target.workflowRunId,
            sessionName: record.target.sessionName,
            cleanupAttempt: 1,
            precedingCleanupOperationId: null,
          }),
        )
      } else if (record.producerFamily === 'workflow-cleanup') {
        keys.add(`cleanup-operation:${record.id}`)
      } else if (record.producerFamily === 'session-followup') {
        const operationId = record.event.payload.cleanupOperationId
        if (typeof operationId === 'string') keys.add(`cleanup-operation:${operationId}`)
      }
    }
    for (const key of keys) this.resolveSettled(key)
  }

  stateLoaded(): void {
    for (const key of this.waiters.keys()) this.resolveSettled(key)
  }

  stop(): void {
    for (const keyWaiters of [...this.waiters.values()]) {
      for (const waiter of [...keyWaiters]) this.reject(waiter, new Error('runtime-event outbox stopped'))
    }
  }

  private resolveSettled(key: string): void {
    const keyWaiters = this.waiters.get(key)
    if (!keyWaiters) return
    for (const waiter of [...keyWaiters]) {
      if (!this.hasRecords(waiter.target)) this.resolve(waiter)
    }
  }

  private resolve(waiter: Waiter): void {
    const keyWaiters = this.waiters.get(waiter.key)
    if (!keyWaiters?.delete(waiter)) return
    if (keyWaiters.size === 0) this.waiters.delete(waiter.key)
    this.timer.clearTimeout(waiter.timer)
    waiter.signal.removeEventListener('abort', waiter.abort)
    waiter.timer = null
    waiter.resolve()
  }

  private reject(waiter: Waiter, error: Error): void {
    const keyWaiters = this.waiters.get(waiter.key)
    if (!keyWaiters?.delete(waiter)) return
    if (keyWaiters.size === 0) this.waiters.delete(waiter.key)
    this.timer.clearTimeout(waiter.timer)
    waiter.signal.removeEventListener('abort', waiter.abort)
    waiter.timer = null
    waiter.reject(error)
  }
}

function abortError(reason: unknown): Error {
  if (reason instanceof Error) return reason
  const error = new Error(typeof reason === 'string' ? reason : 'cleanup predecessor delivery wait aborted')
  error.name = 'AbortError'
  return error
}
