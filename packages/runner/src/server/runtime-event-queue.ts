import type { AgentSessionRuntimeEventReceipt } from './connection.js'
import { RuntimeEventDeliveryError } from './connection-errors.js'
import { runtimeEventDeliveryKey, runtimeEventSchedulingKey } from './runtime-event-queue-identity.js'
import { runnerLogger } from '../system/logger.js'

export type RuntimeEventAcknowledgementPolicy = 'matching-receipt' | 'successful-response'

export interface RuntimeEventRecord {
  readonly id: string
  readonly producerFamily:
    | 'workflow-session'
    | 'workflow-cleanup'
    | 'session-followup'
    | 'generic-followup'
    | 'binding-reconcile'
  readonly target: RuntimeEventTarget
  readonly runtimeSessionId: string
  readonly runtime?: string | null
  readonly sessionTurnId?: string | null
  readonly work: RuntimeEventWorkMetadata | null
  readonly event: RuntimeEventEntry
  readonly acknowledgementPolicy: RuntimeEventAcknowledgementPolicy
}

export type RuntimeEventTarget =
  | { kind: 'workflow'; projectId: string; workflowRunId: string; sessionName: string }
  | { kind: 'generic'; projectId: string; sessionId: string }
  | { kind: 'session'; sessionId: string }

export interface RuntimeEventWorkMetadata {
  readonly workId: string
  readonly workType: string
  readonly stage: string | null
  readonly taskRunId?: string | null
  readonly runnerId?: string | null
  readonly agentSessionId?: string | null
  readonly inputDeliveryId?: string | null
  readonly agentTurnId?: string | null
}

export interface RuntimeEventEntry {
  readonly type: string
  readonly payload: Record<string, unknown>
}

export interface RuntimeEventDelivery {
  send(record: RuntimeEventRecord, signal: AbortSignal): Promise<AgentSessionRuntimeEventReceipt[]>
  sendBatch?(records: readonly RuntimeEventRecord[], signal: AbortSignal): Promise<AgentSessionRuntimeEventReceipt[][]>
}

export interface AgentSessionRuntimeEventQueueOptions {
  readonly deliver?: RuntimeEventDelivery
  readonly retryDelayMs?: number
  readonly deliveryTimeoutMs?: number
  readonly deliveryBatchSize?: number
  readonly queueCapacity?: number
  readonly admissionCapacity?: number
  readonly warn?: (message: string, fields: Record<string, unknown>) => void
}

export interface AgentSessionRuntimeEventQueue {
  ready(): boolean
  load(): Promise<void>
  recover(): Promise<void>
  enqueueBeforeExecution(record: RuntimeEventRecord): Promise<void>
  awaitInputReceipt?(recordId: string): Promise<AgentSessionRuntimeEventReceipt>
  enqueueProducedFact(record: RuntimeEventRecord): Promise<void>
  enqueueProducedFactBatch(records: readonly RuntimeEventRecord[]): Promise<void>
  kick(): Promise<void>
  stop(): Promise<void>
  snapshot(): readonly RuntimeEventRecord[]
}

export class AlreadyConsumedRuntimeEventError extends Error {
  readonly classification = 'already-consumed' as const
  constructor(readonly recordId: string) {
    super(`runtime-event record ${recordId} was already consumed`)
    this.name = 'AlreadyConsumedRuntimeEventError'
  }
}

const log = runnerLogger.child('runtime-event-queue')
export const DEFAULT_RUNTIME_EVENT_QUEUE_CAPACITY = 1_024
export const DEFAULT_RUNTIME_EVENT_ADMISSION_CAPACITY = 64
export const DEFAULT_RUNTIME_EVENT_DELIVERY_TIMEOUT_MS = 10_000
const DEFAULT_RETRY_DELAY_MS = 2_000
const DEFAULT_DELIVERY_BATCH_SIZE = 64

const PERMANENT_409_CODES = new Set([
  'conflict',
  'agent_session_changed',
  'workflow_agent_session_changed',
  'workflow_runtime_binding_rejected',
  'workflow_cleanup_binding_rejected',
])
const PERMANENT_400_CODES = new Set([
  'validation',
  'runtime_session_id_required',
  'session_runtime_identity_required',
  'session_runtime_task_identity_invalid',
  'workflow_runtime_binding_required',
])

interface QueuedRecord {
  readonly record: RuntimeEventRecord
  readonly sequence: number
  readonly admission: boolean
}

interface DeliveryGroup {
  readonly key: string
  readonly records: QueuedRecord[]
  retryAt: number
}

interface InputReceiptWaiter {
  readonly promise: Promise<AgentSessionRuntimeEventReceipt>
  resolve(receipt: AgentSessionRuntimeEventReceipt): void
  reject(error: unknown): void
}

type DeliveryVerdict =
  | { readonly kind: 'accepted'; readonly receipt: AgentSessionRuntimeEventReceipt | null }
  | { readonly kind: 'refused' }
  | { readonly kind: 'retryable' }

export function createAgentSessionRuntimeEventQueue(
  options: AgentSessionRuntimeEventQueueOptions = {},
): AgentSessionRuntimeEventQueue {
  return new InMemoryRuntimeEventQueue(options)
}

class InMemoryRuntimeEventQueue implements AgentSessionRuntimeEventQueue {
  private readonly deliver: RuntimeEventDelivery
  private readonly retryDelayMs: number
  private readonly deliveryTimeoutMs: number
  private readonly deliveryBatchSize: number
  private readonly queueCapacity: number
  private readonly admissionCapacity: number
  private readonly warn: (message: string, fields: Record<string, unknown>) => void
  private readonly groups = new Map<string, DeliveryGroup>()
  private readonly readyGroups: string[] = []
  private readonly inputWaiters = new Map<string, InputReceiptWaiter>()
  private sequence = 0
  private evidenceSize = 0
  private admissionSize = 0
  private draining: Promise<void> | null = null
  private retry: ReturnType<typeof setTimeout> | null = null
  private stopped = false
  private readonly stopController = new AbortController()

  constructor(options: AgentSessionRuntimeEventQueueOptions) {
    this.deliver = options.deliver ?? { send: async () => [] }
    this.retryDelayMs = Math.max(1, options.retryDelayMs ?? DEFAULT_RETRY_DELAY_MS)
    this.deliveryTimeoutMs = Math.max(1, options.deliveryTimeoutMs ?? DEFAULT_RUNTIME_EVENT_DELIVERY_TIMEOUT_MS)
    this.deliveryBatchSize = Math.max(1, options.deliveryBatchSize ?? DEFAULT_DELIVERY_BATCH_SIZE)
    this.queueCapacity = Math.max(1, options.queueCapacity ?? DEFAULT_RUNTIME_EVENT_QUEUE_CAPACITY)
    this.admissionCapacity = Math.max(1, options.admissionCapacity ?? DEFAULT_RUNTIME_EVENT_ADMISSION_CAPACITY)
    this.warn = options.warn ?? ((message, fields) => log.warn(message, fields))
  }

  ready(): boolean {
    return !this.stopped
  }

  async load(): Promise<void> {}

  async recover(): Promise<void> {
    await this.kick()
  }

  snapshot(): readonly RuntimeEventRecord[] {
    return [...this.groups.values()]
      .flatMap((group) => group.records)
      .sort((left, right) => left.sequence - right.sequence)
      .map((entry) => entry.record)
  }

  async enqueueBeforeExecution(record: RuntimeEventRecord): Promise<void> {
    // Receipt-bearing inputs register their waiter before starting delivery, so
    // a fast successful response is consumed directly rather than retained.
    if (!this.enqueue(record, false, true)) throw new AlreadyConsumedRuntimeEventError(record.id)
  }

  async awaitInputReceipt(recordId: string): Promise<AgentSessionRuntimeEventReceipt> {
    if (this.stopped) throw new Error('runtime-event queue is stopped')
    const existing = this.inputWaiters.get(recordId)
    if (existing) return await existing.promise
    if (!this.has(recordId)) throw new AlreadyConsumedRuntimeEventError(recordId)

    let resolve!: (receipt: AgentSessionRuntimeEventReceipt) => void
    let reject!: (error: unknown) => void
    const promise = new Promise<AgentSessionRuntimeEventReceipt>((resolvePromise, rejectPromise) => {
      resolve = resolvePromise
      reject = rejectPromise
    })
    this.inputWaiters.set(recordId, { promise, resolve, reject })
    void this.kick()
    return await promise
  }

  async enqueueProducedFact(record: RuntimeEventRecord): Promise<void> {
    this.enqueue(record)
  }

  async enqueueProducedFactBatch(records: readonly RuntimeEventRecord[]): Promise<void> {
    for (const record of records) this.enqueue(record, false)
    void this.kick()
  }

  async kick(): Promise<void> {
    if (this.stopped) return
    if (!this.draining) {
      this.draining = this.drain().finally(() => {
        this.draining = null
      })
    }
    await this.draining
  }

  async stop(): Promise<void> {
    this.stopped = true
    this.stopController.abort()
    if (this.retry) clearTimeout(this.retry)
    this.retry = null
    const waiters = [...this.inputWaiters.values()]
    this.inputWaiters.clear()
    for (const waiter of waiters) waiter.reject(new Error('runtime-event queue stopped'))
  }

  private enqueue(record: RuntimeEventRecord, kick = true, admission = false): boolean {
    if (this.stopped) throw new Error('runtime-event queue is stopped')
    if (this.has(record.id)) return true
    const size = admission ? this.admissionSize : this.evidenceSize
    const capacity = admission ? this.admissionCapacity : this.queueCapacity
    if (size >= capacity) {
      this.warn(
        admission
          ? 'runtime-event admission rejected because the reserved volatile lane is full'
          : 'runtime-event evidence dropped because the volatile queue is full',
        {
          recordId: record.id,
          eventType: record.event.type,
          capacity,
          lane: admission ? 'admission' : 'evidence',
          policy: 'drop-newest',
        },
      )
      return false
    }
    const key = runtimeEventSchedulingKey(record)
    let group = this.groups.get(key)
    if (!group) {
      group = { key, records: [], retryAt: 0 }
      this.groups.set(key, group)
      this.readyGroups.push(key)
    }
    group.records.push({ record, sequence: this.sequence++, admission })
    if (admission) this.admissionSize += 1
    else this.evidenceSize += 1
    if (kick) void this.kick()
    return true
  }

  private async drain(): Promise<void> {
    let unavailableVisits = 0
    while (!this.stopped && this.readyGroups.length > 0 && unavailableVisits < this.readyGroups.length) {
      const key = this.readyGroups.shift()!
      const group = this.groups.get(key)
      if (!group || group.records.length === 0) continue
      if (group.retryAt > Date.now()) {
        this.readyGroups.push(key)
        unavailableVisits += 1
        continue
      }

      const batch = contiguousBatch(group.records, this.deliveryBatchSize)
      const verdicts = await this.attempt(batch.map((entry) => entry.record))
      let progressed = false
      let retryable = false
      for (let index = 0; index < batch.length; index += 1) {
        const verdict = verdicts[index] ?? { kind: 'retryable' as const }
        if (verdict.kind === 'retryable') {
          retryable = true
          break
        }
        const entry = batch[index]!
        this.retire(group, entry, verdict.kind === 'accepted' ? verdict.receipt : null, verdict.kind === 'refused')
        progressed = true
      }

      if (group.records.length === 0) {
        this.groups.delete(key)
      } else {
        group.retryAt = retryable || !progressed ? Date.now() + this.retryDelayMs : 0
        this.readyGroups.push(key)
      }
      // A successful group gets another turn only after the groups already
      // ahead of it in the ready ring. Delayed groups end the drain only after
      // every currently queued group has been observed unavailable.
      unavailableVisits = progressed ? 0 : unavailableVisits + 1
    }
    this.scheduleRetry()
  }

  private async attempt(records: readonly RuntimeEventRecord[]): Promise<readonly DeliveryVerdict[]> {
    const controller = new AbortController()
    const stop = () => controller.abort(this.stopController.signal.reason)
    this.stopController.signal.addEventListener('abort', stop, { once: true })
    const timeout = setTimeout(
      () => controller.abort(new Error(`runtime-event delivery timed out after ${this.deliveryTimeoutMs}ms`)),
      this.deliveryTimeoutMs,
    )
    timeout.unref?.()
    const delivery = (async () => {
      try {
        const receipts = this.deliver.sendBatch
          ? await this.deliver.sendBatch(records, controller.signal)
          : await Promise.all(records.map((record) => this.deliver.send(record, controller.signal)))
        return records.map((record, index) => deliveryVerdict(record, receipts[index] ?? []))
      } catch (error) {
        if (isPermanentRefusal(error)) {
          for (const record of records) {
            this.warn('runtime-event evidence permanently refused and dropped', {
              recordId: record.id,
              eventType: record.event.type,
              status: error.status,
              code: error.code,
            })
          }
          return records.map(() => ({ kind: 'refused' }) as const)
        }
        return records.map(() => ({ kind: 'retryable' }) as const)
      }
    })()
    const timed = new Promise<readonly DeliveryVerdict[]>((resolve) => {
      const onAbort = () => resolve(records.map(() => ({ kind: 'retryable' }) as const))
      controller.signal.addEventListener('abort', onAbort, { once: true })
    })
    try {
      return await Promise.race([delivery, timed])
    } finally {
      clearTimeout(timeout)
      this.stopController.signal.removeEventListener('abort', stop)
    }
  }

  private retire(
    group: DeliveryGroup,
    entry: QueuedRecord,
    receipt: AgentSessionRuntimeEventReceipt | null,
    refused: boolean,
  ): void {
    if (group.records[0] !== entry) return
    group.records.shift()
    if (entry.admission) this.admissionSize -= 1
    else this.evidenceSize -= 1
    if (!requiresInputReceipt(entry.record)) return
    const waiter = this.inputWaiters.get(entry.record.id)
    if (waiter) {
      this.inputWaiters.delete(entry.record.id)
      if (refused) waiter.reject(new AlreadyConsumedRuntimeEventError(entry.record.id))
      else if (receipt) waiter.resolve(receipt)
    }
    // A successful receipt with no owner is intentionally retired here. The
    // volatile evidence queue is not a receipt store.
  }

  private scheduleRetry(): void {
    if (this.stopped || this.evidenceSize + this.admissionSize === 0 || this.retry) return
    const retryAt = Math.min(...[...this.groups.values()].map((group) => group.retryAt || Date.now()))
    const delay = Math.max(0, retryAt - Date.now())
    this.retry = setTimeout(() => {
      this.retry = null
      void this.kick()
    }, delay)
    this.retry.unref?.()
  }

  private has(recordId: string): boolean {
    return [...this.groups.values()].some((group) => group.records.some((entry) => entry.record.id === recordId))
  }
}

function contiguousBatch(records: readonly QueuedRecord[], limit: number): readonly QueuedRecord[] {
  const head = records[0]
  if (!head) return []
  const deliveryKey = runtimeEventDeliveryKey(head.record)
  const batch: QueuedRecord[] = []
  for (const entry of records) {
    if (batch.length >= limit || runtimeEventDeliveryKey(entry.record) !== deliveryKey) break
    batch.push(entry)
  }
  return batch
}

function deliveryVerdict(
  record: RuntimeEventRecord,
  receipts: readonly AgentSessionRuntimeEventReceipt[],
): DeliveryVerdict {
  if (record.acknowledgementPolicy === 'successful-response') return { kind: 'accepted', receipt: null }
  const receipt = matchingReceipt(record, receipts)
  return receipt ? { kind: 'accepted', receipt } : { kind: 'retryable' }
}

function matchingReceipt(
  record: RuntimeEventRecord,
  receipts: readonly AgentSessionRuntimeEventReceipt[],
): AgentSessionRuntimeEventReceipt | null {
  const matching = receipts.find((receipt) => receipt.type === record.event.type)
  if (!matching) return null
  if (record.producerFamily === 'workflow-session' && record.event.type === 'session.input') {
    return matching.inputDeliveryId === record.id &&
      matching.agentSessionId === record.work?.agentSessionId &&
      typeof matching.agentTurnId === 'string' &&
      matching.agentTurnId.length > 0
      ? matching
      : null
  }
  if (record.producerFamily === 'workflow-cleanup' && record.event.type === 'session.cleanup') {
    return matching.cleanupOperationId === record.id &&
      matching.inputDeliveryId === record.event.payload.inputDeliveryId &&
      matching.agentSessionId === record.work?.agentSessionId &&
      matching.agentTurnId === record.event.payload.turnId
      ? matching
      : null
  }
  return matching
}

function requiresInputReceipt(record: RuntimeEventRecord): boolean {
  return record.event.type === 'session.input' || record.event.type === 'session.cleanup'
}

function isPermanentRefusal(error: unknown): error is RuntimeEventDeliveryError {
  if (!(error instanceof RuntimeEventDeliveryError)) return false
  if (error.status === 409) return PERMANENT_409_CODES.has(error.code ?? '')
  if (error.status === 400) return PERMANENT_400_CODES.has(error.code ?? '')
  return false
}

export {
  runtimeEventDeliveryKey,
  runtimeEventSchedulingKey,
  workflowExecutionIdentity,
} from './runtime-event-queue-identity.js'
export type { WorkflowRuntimeEventExecutionIdentity } from './runtime-event-queue-identity.js'
