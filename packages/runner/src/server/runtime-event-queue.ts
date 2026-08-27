import type { AgentSessionRuntimeEventReceipt } from './connection.js'
import { RuntimeEventDeliveryError } from './connection-errors.js'
import { runtimeEventDeliveryKey, runtimeEventSchedulingKey } from './runtime-event-queue-identity.js'
import {
  InputReceiptWaitCancelledError,
  InputReceiptWaitTimeoutError,
  structuredInputReason,
  type InputReceiptWaitEvidence,
} from './runtime-event-queue-input-receipt.js'
import { runnerLogger } from '../system/logger.js'

export {
  InputReceiptWaitCancelledError,
  InputReceiptWaitTimeoutError,
  type InputReceiptWaitEvidence,
} from './runtime-event-queue-input-receipt.js'

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
  readonly now?: () => number
  readonly warn?: (message: string, fields: Record<string, unknown>) => void
}

export interface RuntimeEventInputReceiptWaitOptions {
  readonly budgetMs: number
  readonly signal: AbortSignal
}

export interface AgentSessionRuntimeEventQueue {
  ready(): boolean
  load(): Promise<void>
  recover(): Promise<void>
  enqueueBeforeExecution(record: RuntimeEventRecord): Promise<void>
  awaitInputReceipt?(
    recordId: string,
    options?: RuntimeEventInputReceiptWaitOptions,
  ): Promise<AgentSessionRuntimeEventReceipt>
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

interface DeliveryLease {
  readonly entries: readonly QueuedRecord[]
  readonly controller: AbortController
}

type DeliveryAttemptResult =
  | { readonly kind: 'settled'; readonly verdicts: readonly DeliveryVerdict[] }
  | { readonly kind: 'timed-out' }

interface MutableInputReceiptWaitEvidence {
  attempts: number
  lastReason: string | null
}

interface InputReceiptWaiter {
  readonly promise: Promise<AgentSessionRuntimeEventReceipt>
  resolve(receipt: AgentSessionRuntimeEventReceipt): void
  reject(error: unknown): void
  readonly bounded: boolean
  readonly budgetMs: number | null
  readonly startedAtMs: number | null
  timer: ReturnType<typeof setTimeout> | null
  cleanup: (() => void) | null
}

type DeliveryVerdict =
  | { readonly kind: 'accepted'; readonly receipt: AgentSessionRuntimeEventReceipt | null }
  | { readonly kind: 'refused' }
  | { readonly kind: 'retryable'; readonly reason: string }

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
  private readonly now: () => number
  private readonly warn: (message: string, fields: Record<string, unknown>) => void
  private readonly groups = new Map<string, DeliveryGroup>()
  private readonly readyGroups: string[] = []
  private readonly inputWaiters = new Map<string, InputReceiptWaiter>()
  private readonly inputReceiptEvidence = new Map<string, MutableInputReceiptWaitEvidence>()
  private readonly deliveryLeases = new Map<string, DeliveryLease>()
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
    this.now = options.now ?? Date.now
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

  async awaitInputReceipt(
    recordId: string,
    options?: RuntimeEventInputReceiptWaitOptions,
  ): Promise<AgentSessionRuntimeEventReceipt> {
    if (this.stopped) throw new Error('runtime-event queue is stopped')
    if (options?.signal.aborted) throw new InputReceiptWaitCancelledError(recordId, options.signal.reason)
    const existing = this.inputWaiters.get(recordId)
    if (existing) return await existing.promise
    if (!this.has(recordId)) throw new AlreadyConsumedRuntimeEventError(recordId)

    let resolve!: (receipt: AgentSessionRuntimeEventReceipt) => void
    let reject!: (error: unknown) => void
    const promise = new Promise<AgentSessionRuntimeEventReceipt>((resolvePromise, rejectPromise) => {
      resolve = resolvePromise
      reject = rejectPromise
    })
    const bounded = options !== undefined
    const budgetMs = bounded ? Math.max(0, options.budgetMs) : null
    const startedAtMs = bounded ? this.now() : null
    const waiter: InputReceiptWaiter = {
      promise,
      resolve,
      reject,
      bounded,
      budgetMs,
      startedAtMs,
      timer: null,
      cleanup: null,
    }
    this.inputWaiters.set(recordId, waiter)
    if (bounded) {
      waiter.timer = setTimeout(() => {
        if (this.inputWaiters.get(recordId) !== waiter) return
        const elapsedMs = Math.max(0, this.now() - (startedAtMs ?? this.now()))
        const error = new InputReceiptWaitTimeoutError(
          recordId,
          this.inputReceiptEvidenceFor(recordId),
          elapsedMs,
          budgetMs ?? 0,
        )
        this.removeInputReceiptWaiter(recordId, waiter)
        this.inputReceiptEvidence.delete(recordId)
        reject(error)
      }, budgetMs ?? 0)
      const abort = () => {
        if (this.inputWaiters.get(recordId) !== waiter) return
        this.removeInputReceiptWaiter(recordId, waiter)
        this.inputReceiptEvidence.delete(recordId)
        reject(new InputReceiptWaitCancelledError(recordId, options.signal.reason))
      }
      options.signal.addEventListener('abort', abort, { once: true })
      waiter.cleanup = () => options.signal.removeEventListener('abort', abort)
      if (options.signal.aborted) abort()
    }
    void this.kick().catch((error) => {
      this.noteInputReasonForRecordById(recordId, error)
      this.rejectInputReceipt(recordId, error, true)
    })
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
    const waiters = [...this.inputWaiters.entries()]
    for (const [recordId, waiter] of waiters) {
      this.removeInputReceiptWaiter(recordId, waiter)
      waiter.reject(new Error('runtime-event queue stopped'))
      this.inputReceiptEvidence.delete(recordId)
    }
    for (const lease of this.deliveryLeases.values()) lease.controller.abort(new Error('runtime-event queue stopped'))
    this.deliveryLeases.clear()
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
      if (this.deliveryLeases.has(key)) {
        this.readyGroups.push(key)
        unavailableVisits += 1
        continue
      }
      if (group.retryAt > this.now()) {
        this.readyGroups.push(key)
        unavailableVisits += 1
        continue
      }

      const batch = contiguousBatch(group.records, this.deliveryBatchSize)
      const attempt = await this.attempt(key, batch)
      if (attempt.kind === 'timed-out') {
        this.noteInputReason(batch, `retryable: runtime-event delivery timed out after ${this.deliveryTimeoutMs}ms`)
        this.readyGroups.push(key)
        unavailableVisits += 1
        continue
      }
      const progressed = this.applyVerdicts(group, batch, attempt.verdicts)

      if (group.records.length === 0) {
        this.groups.delete(key)
      } else {
        group.retryAt = progressed ? 0 : this.now() + this.retryDelayMs
        this.readyGroups.push(key)
      }
      // A successful group gets another turn only after the groups already
      // ahead of it in the ready ring. Delayed groups end the drain only after
      // every currently queued group has been observed unavailable.
      unavailableVisits = progressed ? 0 : unavailableVisits + 1
    }
    this.scheduleRetry()
  }

  private async attempt(key: string, entries: readonly QueuedRecord[]): Promise<DeliveryAttemptResult> {
    for (const entry of entries) this.noteInputAttempt(entry)
    const records = entries.map((entry) => entry.record)
    const controller = new AbortController()
    const lease: DeliveryLease = { entries, controller }
    this.deliveryLeases.set(key, lease)
    const stop = () => controller.abort(this.stopController.signal.reason)
    this.stopController.signal.addEventListener('abort', stop, { once: true })
    let timeout: ReturnType<typeof setTimeout> | null = null
    const delivery = this.performDelivery(records, controller.signal)
    const deadline = new Promise<DeliveryAttemptResult>((resolve) => {
      timeout = setTimeout(() => {
        controller.abort(new Error(`runtime-event delivery timed out after ${this.deliveryTimeoutMs}ms`))
        resolve({ kind: 'timed-out' })
      }, this.deliveryTimeoutMs)
      timeout.unref?.()
    })
    const settled = delivery.then<DeliveryAttemptResult>((verdicts) => ({ kind: 'settled', verdicts }))
    const result = await Promise.race([settled, deadline])
    if (timeout) clearTimeout(timeout)
    this.stopController.signal.removeEventListener('abort', stop)
    if (result.kind === 'settled') {
      if (this.deliveryLeases.get(key) === lease) this.deliveryLeases.delete(key)
      return result
    }

    void delivery.then((verdicts) => this.completeLateDelivery(key, lease, verdicts))
    return result
  }

  private async performDelivery(
    records: readonly RuntimeEventRecord[],
    signal: AbortSignal,
  ): Promise<readonly DeliveryVerdict[]> {
    try {
      const receipts = this.deliver.sendBatch
        ? await this.deliver.sendBatch(records, signal)
        : await Promise.all(records.map((record) => this.deliver.send(record, signal)))
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
      const reason = `retryable: ${structuredInputReason(error)}`
      return records.map(() => ({ kind: 'retryable', reason }) as const)
    }
  }

  private completeLateDelivery(key: string, lease: DeliveryLease, verdicts: readonly DeliveryVerdict[]): void {
    if (this.deliveryLeases.get(key) !== lease) return
    this.deliveryLeases.delete(key)
    if (this.stopped) return
    const group = this.groups.get(key)
    if (!group) return
    const progressed = this.applyVerdicts(group, lease.entries, verdicts)
    if (group.records.length === 0) {
      this.groups.delete(key)
    } else {
      group.retryAt = progressed ? 0 : this.now() + this.retryDelayMs
      if (!this.readyGroups.includes(key)) this.readyGroups.push(key)
    }
    this.scheduleRetry()
    queueMicrotask(() => void this.kick())
  }

  private applyVerdicts(
    group: DeliveryGroup,
    entries: readonly QueuedRecord[],
    verdicts: readonly DeliveryVerdict[],
  ): boolean {
    let progressed = false
    for (let index = 0; index < entries.length; index += 1) {
      const verdict = verdicts[index] ?? { kind: 'retryable' as const, reason: 'retryable: no delivery verdict' }
      const entry = entries[index]!
      if (verdict.kind === 'retryable') {
        this.noteInputReasonForRecord(entry.record, verdict.reason)
        break
      }
      this.retire(group, entry, verdict.kind === 'accepted' ? verdict.receipt : null, verdict.kind === 'refused')
      progressed = true
    }
    return progressed
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
      this.removeInputReceiptWaiter(entry.record.id, waiter)
      if (refused) waiter.reject(new AlreadyConsumedRuntimeEventError(entry.record.id))
      else if (receipt) waiter.resolve(receipt)
    }
    this.inputReceiptEvidence.delete(entry.record.id)
    // A successful receipt with no owner is intentionally retired here. The
    // volatile evidence queue is not a receipt store.
  }

  private scheduleRetry(): void {
    if (this.stopped || this.evidenceSize + this.admissionSize === 0 || this.retry) return
    const retryableGroups = [...this.groups.values()].filter((group) => !this.deliveryLeases.has(group.key))
    if (retryableGroups.length === 0) return
    const retryAt = Math.min(...retryableGroups.map((group) => group.retryAt || this.now()))
    const delay = Math.max(0, retryAt - this.now())
    this.retry = setTimeout(() => {
      this.retry = null
      void this.kick()
    }, delay)
    this.retry.unref?.()
  }

  private rejectInputReceipt(recordId: string, error: unknown, retryable = false): void {
    const waiter = this.inputWaiters.get(recordId)
    if (!waiter) return
    if (retryable && waiter.bounded) return
    this.removeInputReceiptWaiter(recordId, waiter)
    waiter.reject(error instanceof Error ? error : new Error(structuredInputReason(error)))
  }

  private removeInputReceiptWaiter(recordId: string, waiter: InputReceiptWaiter): void {
    if (this.inputWaiters.get(recordId) !== waiter) return
    this.inputWaiters.delete(recordId)
    if (waiter.timer) clearTimeout(waiter.timer)
    waiter.timer = null
    waiter.cleanup?.()
    waiter.cleanup = null
  }

  private inputReceiptEvidenceFor(recordId: string): InputReceiptWaitEvidence {
    const evidence = this.inputReceiptEvidence.get(recordId)
    return {
      attempts: evidence?.attempts ?? 0,
      retries: Math.max(0, (evidence?.attempts ?? 0) - 1),
      lastReason: evidence?.lastReason ?? null,
    }
  }

  private noteInputAttempt(entry: QueuedRecord): void {
    if (!requiresInputReceipt(entry.record)) return
    const evidence = this.inputReceiptEvidence.get(entry.record.id) ?? { attempts: 0, lastReason: null }
    evidence.attempts += 1
    this.inputReceiptEvidence.set(entry.record.id, evidence)
  }

  private noteInputReason(entries: readonly QueuedRecord[], reason: unknown): void {
    for (const entry of entries) this.noteInputReasonForRecord(entry.record, reason)
  }

  private noteInputReasonForRecord(record: RuntimeEventRecord, reason: unknown): void {
    if (!requiresInputReceipt(record)) return
    const evidence = this.inputReceiptEvidence.get(record.id) ?? { attempts: 0, lastReason: null }
    evidence.lastReason = typeof reason === 'string' ? reason : structuredInputReason(reason)
    this.inputReceiptEvidence.set(record.id, evidence)
  }

  private noteInputReasonForRecordById(recordId: string, reason: unknown): void {
    const evidence = this.inputReceiptEvidence.get(recordId) ?? { attempts: 0, lastReason: null }
    evidence.lastReason = structuredInputReason(reason)
    this.inputReceiptEvidence.set(recordId, evidence)
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
  if (receipt) return { kind: 'accepted', receipt }
  return {
    kind: 'retryable',
    reason:
      receipts.length === 0
        ? 'retryable: empty receipt response'
        : `retryable: no matching ${record.event.type} receipt`,
  }
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
