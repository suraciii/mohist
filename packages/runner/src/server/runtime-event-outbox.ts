// `AgentSessionRuntimeEventOutbox` is the shared delivery primitive owned
// by `RunnerHost`: one durable ordered queue for Workflow reporter and
// follow-up uploads, with operation-fenced terminal settlement for
// follow-up activity outcomes.
//
// Two acknowledgement policies share the same ordered state:
//   - `matching-receipt` (Workflow input/activity/close and follow-up
//     input): the head is removed only when the response carries the
//     submitted event type. A timeout, transport failure, non-2xx,
//     malformed/empty response, or a receipt without that type retains
//     the head for retry. Stale binding (a 2xx with `[]`) is also a
//     "no-match" outcome.
//   - `successful-response` (operation-correlated follow-up terminals):
//     any 2xx with a valid receipt array — even `[]` — settles the head.
//     A consumed operation lease means replay legitimately returns `[]`,
//     so requiring a match would fence the Session forever.
//
// The outbox persists a coalesced snapshot of every retained record to
// `.mohist/runner-state/runtime-events.json` via an injected
// `RuntimeEventOutboxFileSystem` port. Production uses a Node filesystem
// adapter (atomic rename, owner-only write); tests drive a recording
// in-memory implementation. No Node filesystem adapter is constructed
// inside the test tree.
//
// Recovery model:
//   - The outbox loads before the runner starts accepting commands or
//     claiming work. A missing file is treated as an empty queue; an
//     unreadable or invalid file marks the outbox unhealthy and never
//     replaces it with empty state.
//   - Post-start snapshot failure retains the latest desired in-memory
//     state (minus a rolled-back pre-execution input) and schedules an
//     autonomous retry timer. Only a successful rename restores health
//     and kicks network delivery.
//   - `stop()` cancels network and local-persistence retry timers and
//     in-flight HTTP attempts but never deletes durable records.

import { errorMessage } from '../core/errors.js'
import { runnerLogger } from '../system/logger.js'
import type { AgentSessionRuntimeEventReceipt } from './connection.js'
import {
  isWorkflowSessionBoundary,
  runtimeEventDeliveryKey,
  runtimeEventSchedulingKey,
} from './runtime-event-outbox-identity.js'
import {
  defaultRuntimeEventOutboxTimer,
  NodeRuntimeEventOutboxFileSystem,
  type AgentSessionRuntimeEventOutbox,
  type AgentSessionRuntimeEventOutboxOptions,
  type RuntimeEventAcknowledgementPolicy,
  type RuntimeEventDelivery,
  type RuntimeEventEntry,
  type RuntimeEventOutboxFileSystem,
  type RuntimeEventOutboxTimer,
  type RuntimeEventRecord,
  type RuntimeEventTarget,
  type RuntimeEventWorkMetadata,
} from './runtime-event-outbox-ports.js'

export { nextTemporaryFilePath } from './runtime-event-outbox-ports.js'
export { runtimeEventDeliveryKey, workflowExecutionIdentity } from './runtime-event-outbox-identity.js'
export type {
  AgentSessionRuntimeEventOutbox,
  AgentSessionRuntimeEventOutboxOptions,
  RuntimeEventAcknowledgementPolicy,
  RuntimeEventDelivery,
  RuntimeEventEntry,
  RuntimeEventOutboxFileSystem,
  RuntimeEventOutboxTimer,
  RuntimeEventRecord,
  RuntimeEventTarget,
  RuntimeEventWorkMetadata,
} from './runtime-event-outbox-ports.js'
export type { WorkflowRuntimeEventExecutionIdentity } from './runtime-event-outbox-identity.js'

const log = runnerLogger.child('session')

export const RUNTIME_EVENT_OUTBOX_FILE = '.mohist/runner-state/runtime-events.json'
const RUNTIME_EVENT_OUTBOX_VERSION = 1
const DEFAULT_DELIVERY_TIMEOUT_MS = 5_000
const DEFAULT_RETRY_DELAY_MS = 2_000
const DEFAULT_LOCAL_RETRY_DELAY_MS = 1_000
const DEFAULT_BOUNDED_CONCURRENCY = 4
// Maximum records delivered in one server POST per sequence key per tick.
// Streaming deltas (reasoning/message tokens) arrive in the thousands per
// turn; batching them into one HTTP request per batch collapses the
// O(n) persist amplification that previously drove the runner out of memory.
const DEFAULT_DELIVERY_BATCH_SIZE = 64
// Hard cap on retained records. When exceeded, the earliest streaming
// deltas are dropped (they are reconstructible from later deltas / the
// final assistant message; non-delta facts are never dropped). This is
// the last line of defense against a runaway turn exhausting runner memory.
const DEFAULT_MAX_RETENTION_ENTRIES = 5_000
// Event types that are pure streaming increments — losing a bounded
// number of them does not corrupt the transcript, which is rebuilt from
// later deltas and the final message. These are eligible for batch
// delivery and are the first to be dropped under retention pressure.
const STREAMING_DELTA_TYPES = new Set(['reasoning.delta', 'message.delta'])

interface InternalRecord extends RuntimeEventRecord {
  readonly sequence: number
  readonly enqueuedAt: string
}

interface SnapshotShape {
  version: number
  entries: InternalRecord[]
}

interface DeliveryLease {
  readonly label: string
  readonly batch: readonly InternalRecord[]
}

type DeliveryCompletion =
  | { readonly kind: 'response'; readonly perRecord: AgentSessionRuntimeEventReceipt[][] }
  | { readonly kind: 'failure'; readonly error: unknown }

type DeliveryRaceOutcome = DeliveryCompletion | { readonly kind: 'timed-out' } | { readonly kind: 'stopped' }

export function createAgentSessionRuntimeEventOutbox(
  options: AgentSessionRuntimeEventOutboxOptions = {},
): AgentSessionRuntimeEventOutbox {
  return new AgentSessionRuntimeEventOutboxImpl(options)
}

let sharedSequenceCounter = 0

class AgentSessionRuntimeEventOutboxImpl implements AgentSessionRuntimeEventOutbox {
  private readonly filePath: string
  private readonly fileSystem: RuntimeEventOutboxFileSystem
  private readonly deliver: RuntimeEventDelivery
  private readonly deliveryTimeoutMs: number
  private readonly retryDelayMs: number
  private readonly localRetryDelayMs: number
  private readonly boundedConcurrency: number
  private readonly deliveryBatchSize: number
  private readonly maxRetentionEntries: number
  private readonly randomId: () => string
  private readonly monotonicSequence: () => number
  private readonly now: () => Date
  private readonly timer: RuntimeEventOutboxTimer
  private readonly records = new Map<string, InternalRecord>()
  private readonly inputReceiptWaiters = new Map<
    string,
    {
      resolve: (receipt: AgentSessionRuntimeEventReceipt) => void
      reject: (error: Error) => void
    }
  >()
  private readonly receivedInputReceipts = new Map<string, AgentSessionRuntimeEventReceipt>()
  // A deadline fails the current action while its original request remains fenced.
  private readonly timedOutInputReceiptErrors = new Map<string, Error>()
  private readonly activeDeliveryGroups = new Map<string, DeliveryLease>()
  private loaded = false
  private healthy = false
  private kicked: Promise<void> | null = null
  private networkRetry: { unref(): void } | null = null
  private localRetry: { unref(): void } | null = null
  private stopped = false
  private snapshotWriteTail: Promise<void> = Promise.resolve()
  private snapshotInFlight: Promise<void> | null = null
  private recoveryInFlight: Promise<void> | null = null
  private readonly deliveryStop = new AbortController()
  private nextDeliveryGroupLabel: string | null = null
  private recoveryRequiresLoad = false
  private loadAttempts = 0

  constructor(options: AgentSessionRuntimeEventOutboxOptions) {
    this.fileSystem = options.fileSystem ?? new NodeRuntimeEventOutboxFileSystem()
    this.deliver = options.deliver ?? { send: defaultDelivery }
    this.deliveryTimeoutMs = options.deliveryTimeoutMs ?? DEFAULT_DELIVERY_TIMEOUT_MS
    this.retryDelayMs = options.retryDelayMs ?? DEFAULT_RETRY_DELAY_MS
    this.localRetryDelayMs = options.localRetryDelayMs ?? DEFAULT_LOCAL_RETRY_DELAY_MS
    this.boundedConcurrency = Math.max(1, options.boundedConcurrency ?? DEFAULT_BOUNDED_CONCURRENCY)
    this.deliveryBatchSize = Math.max(1, options.deliveryBatchSize ?? DEFAULT_DELIVERY_BATCH_SIZE)
    this.maxRetentionEntries = Math.max(1, options.maxRetentionEntries ?? DEFAULT_MAX_RETENTION_ENTRIES)
    this.randomId =
      options.randomId ?? (() => `evt_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 10)}`)
    this.monotonicSequence = options.monotonicSequence ?? (() => ++sharedSequenceCounter)
    this.now = options.clock ?? (() => new Date())
    this.timer = options.timer ?? defaultRuntimeEventOutboxTimer
    this.filePath = options.filePath ?? RUNTIME_EVENT_OUTBOX_FILE
  }

  ready(): boolean {
    return this.healthy
  }

  snapshot(): readonly RuntimeEventRecord[] {
    const ordered = [...this.records.values()].sort((a, b) => a.sequence - b.sequence)
    return ordered.map(stripInternal)
  }

  async load(): Promise<void> {
    this.records.clear()
    this.healthy = false
    this.loaded = true
    this.loadAttempts += 1
    if (this.stopped) return
    let raw: string | null = null
    try {
      raw = await this.fileSystem.readText(this.filePath)
    } catch (error) {
      this.healthy = false
      this.recoveryRequiresLoad = true
      this.lastLoadError = error instanceof Error ? error : new Error(errorMessage(error))
      this.scheduleLocalRetry()
      return
    }
    if (raw === null) {
      this.healthy = true
      this.recoveryRequiresLoad = false
      return
    }
    const snapshot = parseSnapshot(raw)
    if (snapshot === null) {
      // Unreadable file: do NOT touch the file. The outbox stays
      // unhealthy until either the original snapshot becomes readable
      // again or another load/save path replaces it.
      this.healthy = false
      this.recoveryRequiresLoad = true
      this.lastLoadError = new Error('runtime events snapshot is unreadable')
      this.scheduleLocalRetry()
      return
    }
    this.lastLoadError = null
    for (const entry of snapshot.entries) this.records.set(entry.id, entry)
    this.enforceRetentionCap()
    this.healthy = true
    this.recoveryRequiresLoad = false
  }

  async enqueueBeforeExecution(
    record: Pick<
      RuntimeEventRecord,
      | 'id'
      | 'target'
      | 'runtimeSessionId'
      | 'runtime'
      | 'sessionTurnId'
      | 'work'
      | 'event'
      | 'acknowledgementPolicy'
      | 'producerFamily'
    >,
  ): Promise<void> {
    if (this.stopped) throw new Error('runtime-event outbox is stopped; cannot enqueue')
    this.requireLoaded('enqueueBeforeExecution')
    const sequence = this.monotonicSequence()
    const internal: InternalRecord = {
      ...record,
      sequence,
      enqueuedAt: this.now().toISOString(),
    }
    this.records.set(internal.id, internal)
    await this.enqueueSnapshotWrite((error) => {
      this.records.delete(internal.id)
      this.healthy = false
      this.lastLoadError = error instanceof Error ? error : new Error(errorMessage(error))
      this.scheduleLocalRetry()
    })
    void this.kick()
  }

  async awaitInputReceipt(recordId: string): Promise<AgentSessionRuntimeEventReceipt> {
    this.requireLoaded('awaitInputReceipt')
    const received = this.receivedInputReceipts.get(recordId)
    if (received) {
      this.receivedInputReceipts.delete(recordId)
      return received
    }
    const timedOut = this.timedOutInputReceiptErrors.get(recordId)
    if (timedOut) throw timedOut
    if (!this.records.has(recordId))
      throw new Error(`workflow input ${recordId} is no longer pending and has no matching receipt`)
    if (this.inputReceiptWaiters.has(recordId))
      throw new Error(`workflow input ${recordId} already has a receipt waiter`)

    const receipt = new Promise<AgentSessionRuntimeEventReceipt>((resolve, reject) => {
      this.inputReceiptWaiters.set(recordId, { resolve, reject })
    })
    void this.kick().catch((error) => {
      this.rejectInputReceipt(recordId, error)
    })
    return await receipt
  }

  async enqueueProducedFact(record: RuntimeEventRecord): Promise<void> {
    await this.enqueueProducedFactBatch([record])
  }

  async enqueueProducedFactBatch(records: readonly RuntimeEventRecord[]): Promise<void> {
    if (this.stopped) throw new Error('runtime-event outbox is stopped; cannot enqueue')
    this.requireLoaded('enqueueProducedFactBatch')
    if (records.length === 0) return
    const enqueuedAt = this.now().toISOString()
    for (const record of records) {
      const sequence = this.monotonicSequence()
      const internal: InternalRecord = { ...record, sequence, enqueuedAt }
      this.records.set(internal.id, internal)
    }
    this.enforceRetentionCap()
    await this.enqueueSnapshotWrite((error) => {
      this.lastLoadError = error instanceof Error ? error : new Error(errorMessage(error))
      this.healthy = false
      this.scheduleLocalRetry()
    })
    void this.kick()
  }

  // Drop the earliest streaming deltas once the in-memory record map
  // exceeds the configured retention cap. Non-delta facts (input,
  // closed, tool lifecycle, usage, model, follow-up terminals) are
  // never dropped — they carry irreplaceable workflow/session state.
  // Dropped deltas are reconstructible from later deltas and the final
  // assistant message on the server side.
  private enforceRetentionCap(): void {
    if (this.records.size <= this.maxRetentionEntries) return
    let overflow = this.records.size - this.maxRetentionEntries
    const candidates = [...this.records.values()]
      .filter((record) => STREAMING_DELTA_TYPES.has(record.event.type))
      .sort(sortBySequence)
    for (const record of candidates) {
      if (overflow <= 0) break
      this.records.delete(record.id)
      overflow -= 1
    }
    if (this.records.size > this.maxRetentionEntries) {
      log.warn('runtime-event outbox retention cap exceeded', {
        reason: `limit=${this.maxRetentionEntries} remaining=${this.records.size}`,
        session: 'outbox',
      })
    }
  }

  async kick(): Promise<void> {
    if (!this.healthy) return
    if (this.kicked) {
      await this.kicked
      return
    }
    this.kicked = this.drainAll().finally(() => {
      this.kicked = null
    })
    await this.kicked
  }

  async stop(): Promise<void> {
    this.stopped = true
    this.deliveryStop.abort()
    this.activeDeliveryGroups.clear()
    if (this.networkRetry) {
      this.timer.clearTimeout(this.networkRetry)
      this.networkRetry = null
    }
    if (this.localRetry) {
      this.timer.clearTimeout(this.localRetry)
      this.localRetry = null
    }
    if (this.snapshotInFlight) {
      try {
        await this.snapshotInFlight
      } catch {
        /* best effort */
      }
    }
    await this.snapshotWriteTail
  }

  private lastLoadError: Error | null = null

  private async drainAll(): Promise<void> {
    if (!this.healthy || this.stopped) return
    const signal = this.deliveryStop.signal
    // Each tick drains one head per managed sequence, capped by
    // `boundedConcurrency`. The tick continues only when at least one head was
    // acknowledged this round; otherwise the network-retry timer picks up the
    // next attempt. We track acknowledgement per-head (not via `records.size`,
    // which is contaminated by concurrent enqueues) so that progress is
    // judged by what the tick actually settled.
    while (!signal.aborted && this.healthy && !this.stopped) {
      const groups = collectGroups(
        [...this.records.values()].filter(
          (record) => !this.activeDeliveryGroups.has(runtimeEventSchedulingKey(record)),
        ),
      )
      if (groups.length === 0) break
      const selectedGroups = selectDeliveryGroups(groups, this.boundedConcurrency, this.nextDeliveryGroupLabel)
      this.nextDeliveryGroupLabel = selectedGroups.nextLabel
      const tick: Promise<boolean>[] = []
      for (const group of selectedGroups.groups) {
        tick.push(this.drainGroup(group.label, signal))
      }
      if (tick.length === 0) break
      const outcomes = await Promise.allSettled(tick)
      const acknowledged = outcomes.some((r) => r.status === 'fulfilled' && r.value === true)
      if (!acknowledged) break
    }
    if (!signal.aborted && this.healthy && this.records.size > 0) {
      this.scheduleNetworkRetry()
    }
  }

  private async drainGroup(label: string, signal: AbortSignal): Promise<boolean> {
    // Drains up to `deliveryBatchSize` records for one sequence key per tick.
    // Returns `true` when at least one record was acknowledged (removed from
    // `records`); `false` when the whole batch was rejected, timed out, or
    // failed transport. Batching collapses the per-ack persistSnapshot that
    // previously made streaming deltas O(n) in disk writes and heap churn.
    const batch = this.takeBatch(label, this.deliveryBatchSize)
    if (batch.length === 0) return false
    return await this.deliverBatch(batch, signal)
  }

  private takeBatch(label: string, limit: number): InternalRecord[] {
    const matching: InternalRecord[] = []
    for (const record of this.records.values()) {
      const recordLabel = runtimeEventSchedulingKey(record)
      if (recordLabel !== label) continue
      matching.push(record)
    }
    matching.sort(sortBySequence)
    const first = matching[0]
    if (!first) return []
    // Preserve wire identity while serializing the logical Workflow session;
    // input boundaries wait for the prior terminal acknowledgement.
    if (isWorkflowSessionBoundary(first)) return [first]
    const executionKey = runtimeEventDeliveryKey(first)
    const batch: InternalRecord[] = []
    for (const record of matching) {
      if (runtimeEventDeliveryKey(record) !== executionKey) break
      if (isWorkflowSessionBoundary(record)) break
      batch.push(record)
      if (batch.length >= limit) break
    }
    return batch
  }

  private async deliverBatch(batch: InternalRecord[], signal: AbortSignal): Promise<boolean> {
    if (signal.aborted || batch.length === 0) return false
    const label = runtimeEventSchedulingKey(batch[0])
    if (this.activeDeliveryGroups.has(label)) return false
    const lease: DeliveryLease = { label, batch }
    this.activeDeliveryGroups.set(label, lease)

    const controller = new AbortController()
    let resolveStopped!: () => void
    const stopped = new Promise<DeliveryRaceOutcome>((resolve) => {
      resolveStopped = () => resolve({ kind: 'stopped' })
    })
    const abortFromStop = () => {
      controller.abort(signal.reason)
      resolveStopped()
    }
    signal.addEventListener('abort', abortFromStop, { once: true })

    const timeoutError = new Error(`runtime-event delivery timeout after ${this.deliveryTimeoutMs}ms`)
    let resolveDeadline!: () => void
    const deadline = new Promise<DeliveryRaceOutcome>((resolve) => {
      resolveDeadline = () => resolve({ kind: 'timed-out' })
    })
    const timer = this.timer.setTimeout(() => {
      if (!controller.signal.aborted) controller.abort(timeoutError)
      resolveDeadline()
    }, this.deliveryTimeoutMs)
    const completion: Promise<DeliveryCompletion> = this.deliverBatchRecords(batch, controller.signal).then(
      (perRecord): DeliveryCompletion => ({ kind: 'response', perRecord }),
      (error): DeliveryCompletion => ({ kind: 'failure', error }),
    )
    const outcome = await Promise.race([completion, deadline, stopped])
    this.timer.clearTimeout(timer)
    signal.removeEventListener('abort', abortFromStop)

    if (outcome.kind === 'timed-out') {
      // Abort is advisory: the original request may still commit and reply later.
      this.rejectTimedOutInputReceipts(batch, timeoutError)
      this.settleTimedOutDelivery(lease, completion)
      return false
    }
    if (outcome.kind === 'stopped') {
      for (const record of batch) this.rejectInputReceipt(record.id, signal.reason)
      this.releaseDeliveryLease(lease)
      return false
    }

    const acknowledged = await this.settleDeliveryLease(lease, outcome)
    if (!acknowledged) this.scheduleNetworkRetry()
    return acknowledged
  }

  private settleTimedOutDelivery(lease: DeliveryLease, completion: Promise<DeliveryCompletion>): void {
    void completion
      .then(async (outcome) => {
        const acknowledged = await this.settleDeliveryLease(lease, outcome)
        if (this.stopped || !this.healthy) return
        if (acknowledged) {
          void this.kick().catch((error) => {
            log.error('late runtime-event delivery settlement failed', {
              exception: error,
              session: 'outbox',
            })
          })
          return
        }
        this.scheduleNetworkRetry()
      })
      .catch((error) => {
        log.error('late runtime-event delivery settlement failed', {
          exception: error,
          session: 'outbox',
        })
      })
  }

  private async settleDeliveryLease(lease: DeliveryLease, outcome: DeliveryCompletion): Promise<boolean> {
    if (this.stopped || this.activeDeliveryGroups.get(lease.label) !== lease) return false
    try {
      if (outcome.kind === 'failure') {
        for (const record of lease.batch) this.rejectInputReceipt(record.id, outcome.error)
        return false
      }
      return await this.settleDeliveryReceipts(lease.batch, outcome.perRecord)
    } finally {
      this.releaseDeliveryLease(lease)
    }
  }

  private async settleDeliveryReceipts(
    batch: readonly InternalRecord[],
    perRecord: AgentSessionRuntimeEventReceipt[][],
  ): Promise<boolean> {
    // Settle each record against its own policy and the receipts returned
    // for its position in the batch. Only records whose head pointer is
    // unchanged (not rolled back / replaced mid-flight) are removed.
    const removed: InternalRecord[] = []
    let anyAcknowledged = false
    for (let i = 0; i < batch.length; i += 1) {
      const record = batch[i]
      const receipts = perRecord[i] ?? []
      const receipt = matchingReceipt(record.acknowledgementPolicy, record, receipts)
      if (!receipt) {
        this.rejectInputReceipt(
          record.id,
          new Error(`workflow input ${record.id} did not receive a matching Server receipt`),
        )
        continue
      }
      if (this.records.get(record.id) !== record) continue
      this.records.delete(record.id)
      removed.push(record)
      if (requiresInputReceipt(record)) this.resolveInputReceipt(record.id, receipt)
      anyAcknowledged = true
    }
    if (!anyAcknowledged) {
      return false
    }
    if (removed.length === 0) return true
    try {
      await this.enqueueSnapshotWrite((error) => {
        for (const record of removed) this.records.set(record.id, record)
        this.healthy = false
        this.lastLoadError = error instanceof Error ? error : new Error(errorMessage(error))
        this.scheduleLocalRetry()
      })
    } catch {
      return false
    }
    return true
  }

  private rejectTimedOutInputReceipts(batch: readonly InternalRecord[], error: Error): void {
    for (const record of batch) {
      if (!requiresInputReceipt(record)) continue
      this.timedOutInputReceiptErrors.set(record.id, error)
      this.rejectInputReceipt(record.id, error)
    }
  }

  private releaseDeliveryLease(lease: DeliveryLease): void {
    if (this.activeDeliveryGroups.get(lease.label) === lease) this.activeDeliveryGroups.delete(lease.label)
  }

  private hasReadyDeliveryGroup(): boolean {
    for (const record of this.records.values()) {
      if (!this.activeDeliveryGroups.has(runtimeEventSchedulingKey(record))) return true
    }
    return false
  }

  private async deliverBatchRecords(
    batch: InternalRecord[],
    signal: AbortSignal,
  ): Promise<AgentSessionRuntimeEventReceipt[][]> {
    if (this.deliver.sendBatch) {
      return await this.deliver.sendBatch(batch, signal)
    }
    // Default fallback: deliver each record individually. Production wires
    // `sendBatch` to one server POST per batch.
    const results: AgentSessionRuntimeEventReceipt[][] = []
    for (const record of batch) {
      results.push(await this.deliver.send(record, signal))
    }
    return results
  }

  private scheduleNetworkRetry(): void {
    if (this.stopped) return
    if (this.networkRetry) return
    if (!this.healthy) return
    if (!this.hasReadyDeliveryGroup()) return
    this.networkRetry = this.timer.setTimeout(() => {
      this.networkRetry = null
      void this.kick().catch(() => undefined)
    }, this.retryDelayMs)
  }

  private scheduleLocalRetry(): void {
    if (this.stopped) return
    if (this.localRetry) return
    this.localRetry = this.timer.setTimeout(() => {
      this.localRetry = null
      void this.recover()
    }, this.localRetryDelayMs)
  }

  async recover(): Promise<void> {
    if (this.stopped) return
    if (this.recoveryInFlight) {
      await this.recoveryInFlight
      return
    }
    this.recoveryInFlight = this.recoverLocalState().finally(() => {
      this.recoveryInFlight = null
    })
    await this.recoveryInFlight
  }

  private async recoverLocalState(): Promise<void> {
    if (!this.loaded || this.recoveryRequiresLoad) {
      await this.load()
      if (this.healthy) void this.kick()
      return
    }
    if (this.healthy) {
      void this.kick()
      return
    }
    if (this.snapshotInFlight) {
      await this.snapshotInFlight.catch(() => undefined)
    }
    this.snapshotInFlight = (async () => {
      try {
        await this.enqueueSnapshotWrite()
        this.healthy = true
        this.recoveryRequiresLoad = false
        this.lastLoadError = null
        void this.kick()
      } catch (error) {
        this.lastLoadError = error instanceof Error ? error : new Error(errorMessage(error))
        this.healthy = false
        if (!this.stopped) this.scheduleLocalRetry()
      }
    })()
    try {
      await this.snapshotInFlight
    } finally {
      this.snapshotInFlight = null
    }
  }

  private async persistSnapshot(): Promise<void> {
    const body = serializeSnapshot([...this.records.values()].sort(sortBySequence))
    await this.fileSystem.writeAtomicText(this.filePath, body)
  }

  private enqueueSnapshotWrite(onFailure?: (error: unknown) => void): Promise<void> {
    const write = this.snapshotWriteTail.then(async () => {
      try {
        await this.persistSnapshot()
      } catch (error) {
        onFailure?.(error)
        throw error
      }
    })
    this.snapshotWriteTail = write.catch(() => undefined)
    return write
  }

  private requireLoaded(op: string): void {
    if (!this.loaded) throw new Error(`runtime-event outbox is not loaded; cannot ${op}`)
  }

  private resolveInputReceipt(recordId: string, receipt: AgentSessionRuntimeEventReceipt): void {
    this.timedOutInputReceiptErrors.delete(recordId)
    const waiter = this.inputReceiptWaiters.get(recordId)
    if (waiter) {
      this.inputReceiptWaiters.delete(recordId)
      waiter.resolve(receipt)
      return
    }
    this.receivedInputReceipts.set(recordId, receipt)
  }

  private rejectInputReceipt(recordId: string, error: unknown): void {
    const waiter = this.inputReceiptWaiters.get(recordId)
    if (!waiter) return
    this.inputReceiptWaiters.delete(recordId)
    waiter.reject(error instanceof Error ? error : new Error(errorMessage(error)))
  }
}

interface GroupSnapshot {
  readonly label: string
  readonly records: InternalRecord[]
}

function collectGroups(records: InternalRecord[]): GroupSnapshot[] {
  const groups = new Map<string, InternalRecord[]>()
  for (const record of records) {
    const label = runtimeEventSchedulingKey(record)
    const list = groups.get(label)
    if (list) list.push(record)
    else groups.set(label, [record])
  }
  return [...groups.entries()].map(([label, list]) => ({
    label,
    records: list.sort(sortBySequence),
  }))
}

function selectDeliveryGroups(
  groups: GroupSnapshot[],
  limit: number,
  nextLabel: string | null,
): { groups: GroupSnapshot[]; nextLabel: string | null } {
  if (groups.length === 0) return { groups: [], nextLabel: null }

  const start =
    nextLabel === null
      ? 0
      : Math.max(
          0,
          groups.findIndex((group) => group.label === nextLabel),
        )
  const count = Math.min(limit, groups.length)
  const selected = Array.from({ length: count }, (_, offset) => groups[(start + offset) % groups.length])
  return {
    groups: selected,
    nextLabel: groups[(start + count) % groups.length]?.label ?? null,
  }
}

function matchingReceipt(
  policy: RuntimeEventAcknowledgementPolicy,
  record: RuntimeEventRecord,
  receipts: AgentSessionRuntimeEventReceipt[],
): AgentSessionRuntimeEventReceipt | null {
  if (policy === 'successful-response') return receipts[0] ?? { type: record.event.type }
  const matching = receipts.find((entry) => entry.type === record.event.type)
  if (!matching) return null
  if (record.producerFamily === 'workflow-session' && record.event.type === 'session.input' && record.work?.taskRunId) {
    return matching.inputDeliveryId === record.id &&
      matching.agentSessionId === record.work.agentSessionId &&
      typeof matching.agentTurnId === 'string' &&
      matching.agentTurnId.length > 0
      ? matching
      : null
  }
  if (record.producerFamily === 'workflow-cleanup' && record.event.type === 'session.cleanup') {
    const inputDeliveryId = record.event.payload.inputDeliveryId
    const agentTurnId = record.event.payload.turnId
    return typeof inputDeliveryId === 'string' &&
      typeof agentTurnId === 'string' &&
      matching.inputDeliveryId === inputDeliveryId &&
      matching.agentSessionId === record.work?.agentSessionId &&
      matching.agentTurnId === agentTurnId
      ? matching
      : null
  }
  return matching
}

function requiresInputReceipt(record: RuntimeEventRecord): boolean {
  return (
    record.event.type === 'session.input' ||
    (record.producerFamily === 'workflow-cleanup' && record.event.type === 'session.cleanup')
  )
}

function sortBySequence(a: InternalRecord, b: InternalRecord): number {
  return a.sequence - b.sequence
}

function serializeSnapshot(entries: InternalRecord[]): string {
  const snapshot: SnapshotShape = { version: RUNTIME_EVENT_OUTBOX_VERSION, entries: entries.map(cloneInternal) }
  return JSON.stringify(snapshot, null, 2)
}

function parseSnapshot(raw: string): SnapshotShape | null {
  try {
    const value = JSON.parse(raw) as unknown
    if (
      !isPlainObject(value) ||
      value['version'] !== RUNTIME_EVENT_OUTBOX_VERSION ||
      !Array.isArray(value['entries'])
    ) {
      return null
    }
    const entries: InternalRecord[] = []
    for (const item of value['entries']) {
      const parsed = parseInternalRecord(item)
      if (!parsed) return null
      entries.push(parsed)
    }
    return { version: RUNTIME_EVENT_OUTBOX_VERSION, entries }
  } catch {
    return null
  }
}

function parseInternalRecord(value: unknown): InternalRecord | null {
  if (!isPlainObject(value)) return null
  const id = value['id']
  const target = value['target']
  const family = value['producerFamily']
  const runtimeSessionId = value['runtimeSessionId']
  const runtime = value['runtime']
  const sessionTurnId = value['sessionTurnId']
  const event = value['event']
  const policy = value['acknowledgementPolicy']
  const work = value['work'] ?? null
  const sequence = value['sequence']
  const enqueuedAt = value['enqueuedAt']
  if (
    typeof id !== 'string' ||
    !isRuntimeTarget(target) ||
    (family !== 'workflow-session' &&
      family !== 'workflow-cleanup' &&
      family !== 'session-followup' &&
      family !== 'generic-followup' &&
      family !== 'binding-reconcile') ||
    typeof runtimeSessionId !== 'string' ||
    (runtime !== undefined && runtime !== null && typeof runtime !== 'string') ||
    (sessionTurnId !== undefined && sessionTurnId !== null && typeof sessionTurnId !== 'string') ||
    !isRuntimeEvent(event) ||
    (policy !== 'matching-receipt' && policy !== 'successful-response') ||
    typeof sequence !== 'number' ||
    typeof enqueuedAt !== 'string'
  ) {
    return null
  }
  if (work !== null && !isRuntimeWorkMetadata(work)) return null
  return {
    id,
    producerFamily: family,
    target,
    runtimeSessionId,
    runtime: typeof runtime === 'string' ? runtime : null,
    sessionTurnId: typeof sessionTurnId === 'string' ? sessionTurnId : null,
    work,
    event,
    acknowledgementPolicy: policy,
    sequence,
    enqueuedAt,
  }
}

function stripInternal(record: InternalRecord): RuntimeEventRecord {
  return {
    id: record.id,
    producerFamily: record.producerFamily,
    target: record.target,
    runtimeSessionId: record.runtimeSessionId,
    runtime: record.runtime ?? null,
    sessionTurnId: record.sessionTurnId ?? null,
    work: record.work,
    event: record.event,
    acknowledgementPolicy: record.acknowledgementPolicy,
  }
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

function isRuntimeTarget(value: unknown): value is RuntimeEventTarget {
  if (!isPlainObject(value)) return false
  if (value['kind'] === 'workflow') {
    return (
      typeof value['projectId'] === 'string' &&
      typeof value['workflowRunId'] === 'string' &&
      typeof value['sessionName'] === 'string'
    )
  }
  if (value['kind'] === 'generic') {
    return typeof value['projectId'] === 'string' && typeof value['sessionId'] === 'string'
  }
  if (value['kind'] === 'session') return typeof value['sessionId'] === 'string'
  return false
}

function isRuntimeEvent(value: unknown): value is RuntimeEventEntry {
  if (!isPlainObject(value)) return false
  const type = value['type']
  const payload = value['payload']
  return typeof type === 'string' && isPlainObject(payload)
}

function isRuntimeWorkMetadata(value: unknown): value is RuntimeEventWorkMetadata {
  if (!isPlainObject(value)) return false
  return (
    typeof value['workId'] === 'string' &&
    typeof value['workType'] === 'string' &&
    (value['stage'] === null || typeof value['stage'] === 'string') &&
    (value['taskRunId'] === undefined || value['taskRunId'] === null || typeof value['taskRunId'] === 'string') &&
    (value['runnerId'] === undefined || value['runnerId'] === null || typeof value['runnerId'] === 'string') &&
    (value['agentSessionId'] === undefined ||
      value['agentSessionId'] === null ||
      typeof value['agentSessionId'] === 'string') &&
    (value['inputDeliveryId'] === undefined ||
      value['inputDeliveryId'] === null ||
      typeof value['inputDeliveryId'] === 'string') &&
    (value['agentTurnId'] === undefined || value['agentTurnId'] === null || typeof value['agentTurnId'] === 'string')
  )
}

function cloneInternal(record: InternalRecord): InternalRecord {
  return {
    id: record.id,
    producerFamily: record.producerFamily,
    target: { ...record.target },
    runtimeSessionId: record.runtimeSessionId,
    runtime: record.runtime ?? null,
    sessionTurnId: record.sessionTurnId ?? null,
    work: record.work ? { ...record.work } : null,
    event: {
      type: record.event.type,
      payload: { ...record.event.payload },
    },
    acknowledgementPolicy: record.acknowledgementPolicy,
    sequence: record.sequence,
    enqueuedAt: record.enqueuedAt,
  }
}

async function defaultDelivery(
  _record: RuntimeEventRecord,
  _signal: AbortSignal,
): Promise<AgentSessionRuntimeEventReceipt[]> {
  throw new Error('Runtime event outbox has no delivery implementation; inject one via options.deliver')
}
