// Issue-461 T-001 / design D1-D7:
//
// `AgentSessionRuntimeEventOutbox` is the shared delivery primitive owned
// by `RunnerHost`. It replaces `FollowupFailureOutbox` and absorbs the
// direct Workflow reporter / follow-up uploads into one durable ordered
// queue while preserving the existing operation-fenced terminal
// settlement semantics for follow-up activity outcomes.
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
// Recovery model (D5):
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
//
// D6: the legacy `.mohist/runner-state/followup-failures.json` v1 file
// is imported at first load. Each entry becomes a `successful-response`
// terminal record with deterministic ID
// `legacy-followup-terminal:{operationId}`. The legacy file is renamed
// to a `.migrated` sentinel only after the new snapshot is durable,
// making the import replay-safe across crashes.

import { errorMessage } from "../core/errors.js"
import type { AgentSessionRuntimeEventReceipt } from "./connection.js"
import type { SessionTarget } from "./session-target.js"

export const RUNTIME_EVENT_OUTBOX_FILE = ".mohist/runner-state/runtime-events.json"
export const LEGACY_FOLLOWUP_FAILURE_FILE = ".mohist/runner-state/followup-failures.json"
const LEGACY_FOLLOWUP_FAILURE_VERSION = 1
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
const STREAMING_DELTA_TYPES = new Set(["reasoning.delta", "message.delta"])
let temporaryFileSequence = 0

export type RuntimeEventAcknowledgementPolicy = "matching-receipt" | "successful-response"

export interface RuntimeEventRecord {
  readonly id: string
  readonly producerFamily: "workflow-session" | "generic-followup" | "binding-reconcile"
  readonly target: RuntimeEventTarget
  readonly runtimeSessionId: string
  readonly work: RuntimeEventWorkMetadata | null
  readonly event: RuntimeEventEntry
  readonly acknowledgementPolicy: RuntimeEventAcknowledgementPolicy
}

export type RuntimeEventTarget =
  | { kind: "workflow"; projectId: string; workflowRunId: string; sessionName: string }
  | { kind: "generic"; projectId: string; sessionId: string }
  | { kind: "session"; sessionId: string }

export interface RuntimeEventWorkMetadata {
  readonly workId: string
  readonly workType: string
  readonly stage: string | null
}

export interface RuntimeEventEntry {
  readonly type: string
  readonly payload: Record<string, unknown>
}

export interface AgentSessionRuntimeEventOutboxOptions {
  readonly fileSystem?: RuntimeEventOutboxFileSystem
  readonly filePath?: string
  readonly legacyFilePath?: string | null
  readonly deliver?: RuntimeEventDelivery
  readonly deliveryTimeoutMs?: number
  readonly retryDelayMs?: number
  readonly localRetryDelayMs?: number
  readonly boundedConcurrency?: number
  readonly deliveryBatchSize?: number
  readonly maxRetentionEntries?: number
  readonly randomId?: () => string
  readonly monotonicSequence?: () => number
  readonly clock?: () => Date
  /**
   * Inject the Worker-equivalent timer so tests can drive the local
   * persistence retry timer with `vi.useFakeTimers()`. The default uses
   * `setTimeout`, which is also controllable by `vi.useFakeTimers()`.
   */
  readonly timer?: RuntimeEventOutboxTimer
}

export interface RuntimeEventOutboxTimer {
  setTimeout(handler: () => void, ms: number): { unref(): void; [Symbol.dispose]?: () => void } | null
  clearTimeout(handle: { unref(): void } | null): void
}

const defaultRuntimeEventOutboxTimer: RuntimeEventOutboxTimer = {
  setTimeout(handler, ms) {
    const handle = setTimeout(handler, ms)
    handle.unref?.()
    return handle
  },
  clearTimeout(handle) {
    if (handle === null) return
    clearTimeout(handle as unknown as ReturnType<typeof setTimeout>)
  },
}

/**
 * Narrow filesystem port used by the snapshot/import store. Tests inject
 * an in-memory implementation; production uses the Node adapter.
 */
export interface RuntimeEventOutboxFileSystem {
  readText(path: string): Promise<string | null>
  writeAtomicText(path: string, body: string): Promise<void>
  markMigrated(path: string): Promise<void>
}

export interface RuntimeEventDelivery {
  send(record: RuntimeEventRecord, signal: AbortSignal): Promise<AgentSessionRuntimeEventReceipt[]>
  /**
   * Deliver a batch of records sharing one sequence key in a single
   * server call. Default implementation loops `send`; production
   * overrides it to POST all events in one request. Returns one receipt
   * per input record, in order, so the outbox can settle each record
   * independently by its acknowledgement policy.
   */
  sendBatch?(records: readonly RuntimeEventRecord[], signal: AbortSignal): Promise<AgentSessionRuntimeEventReceipt[][]>
}

export interface AgentSessionRuntimeEventOutbox {
  ready(): boolean
  load(): Promise<void>
  recover(): Promise<void>
  enqueueBeforeExecution(
    record: Pick<RuntimeEventRecord, "id" | "target" | "runtimeSessionId" | "work" | "event" | "acknowledgementPolicy" | "producerFamily">,
  ): Promise<void>
  enqueueProducedFact(record: RuntimeEventRecord): Promise<void>
  /**
   * Enqueue a batch of produced facts sharing one target in one
   * persistSnapshot write. Used by the Workflow reporter to flush a
   * turn's worth of streaming deltas without paying one disk snapshot
   * per token. On persist failure every record of the batch is rolled
   * back and the outbox goes unhealthy — same semantics as
   * `enqueueProducedFact`, applied atomically to the whole batch.
   */
  enqueueProducedFactBatch(records: readonly RuntimeEventRecord[]): Promise<void>
  kick(): Promise<void>
  stop(): Promise<void>
  /** Snapshot the current ordered records — observable for tests. */
  snapshot(): readonly RuntimeEventRecord[]
}

interface InternalRecord extends RuntimeEventRecord {
  readonly sequence: number
  readonly enqueuedAt: string
}

interface SnapshotShape {
  version: number
  entries: InternalRecord[]
}

export class NodeRuntimeEventOutboxFileSystem implements RuntimeEventOutboxFileSystem {
  async readText(path: string): Promise<string | null> {
    try {
      const { readFile } = await import("node:fs/promises")
      return await readFile(path, "utf8")
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === "ENOENT") return null
      throw error
    }
  }

  async writeAtomicText(path: string, body: string): Promise<void> {
    const { mkdir, rename, writeFile, constants } = await import("node:fs/promises")
    const { dirname } = await import("node:path")
    await mkdir(dirname(path), { recursive: true })
    const temporary = nextTemporaryFilePath(path)
    await writeFile(temporary, body, {
      mode: constants.S_IRUSR | constants.S_IWUSR,
    })
    await rename(temporary, path)
  }

  async markMigrated(path: string): Promise<void> {
    const { rename } = await import("node:fs/promises")
    await rename(path, `${path}.migrated`)
  }
}

export function nextTemporaryFilePath(path: string): string {
  temporaryFileSequence += 1
  return `${path}.${process.pid}.${Date.now()}.${temporaryFileSequence}.tmp`
}

export function createAgentSessionRuntimeEventOutbox(
  options: AgentSessionRuntimeEventOutboxOptions = {},
): AgentSessionRuntimeEventOutbox {
  return new AgentSessionRuntimeEventOutboxImpl(options)
}

let sharedSequenceCounter = 0

class AgentSessionRuntimeEventOutboxImpl implements AgentSessionRuntimeEventOutbox {
  private readonly filePath: string
  private readonly legacyFilePath: string | null
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
    this.randomId = options.randomId ?? (() => `evt_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 10)}`)
    this.monotonicSequence = options.monotonicSequence ?? (() => ++sharedSequenceCounter)
    this.now = options.clock ?? (() => new Date())
    this.timer = options.timer ?? defaultRuntimeEventOutboxTimer
    this.filePath = options.filePath ?? RUNTIME_EVENT_OUTBOX_FILE
    this.legacyFilePath = options.legacyFilePath ?? LEGACY_FOLLOWUP_FAILURE_FILE
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
      try {
        await this.importLegacyFileIfPresent()
        this.recoveryRequiresLoad = false
      } catch (error) {
        this.healthy = false
        this.recoveryRequiresLoad = true
        this.lastLoadError = error instanceof Error ? error : new Error(errorMessage(error))
        this.scheduleLocalRetry()
        return
      }
      return
    }
    const snapshot = parseSnapshot(raw)
    if (snapshot === null) {
      // Unreadable file: do NOT touch the file. The outbox stays
      // unhealthy until either the original snapshot becomes readable
      // again or another load/save path replaces it.
      this.healthy = false
      this.recoveryRequiresLoad = true
      this.lastLoadError = new Error("runtime events snapshot is unreadable")
      this.scheduleLocalRetry()
      return
    }
    this.lastLoadError = null
    for (const entry of snapshot.entries) this.records.set(entry.id, entry)
    this.enforceRetentionCap()
    this.healthy = true
    try {
      await this.importLegacyFileIfPresent()
      this.recoveryRequiresLoad = false
    } catch (error) {
      this.healthy = false
      this.recoveryRequiresLoad = true
      this.lastLoadError = error instanceof Error ? error : new Error(errorMessage(error))
      this.scheduleLocalRetry()
      return
    }
  }

  async enqueueBeforeExecution(
    record: Pick<RuntimeEventRecord, "id" | "target" | "runtimeSessionId" | "work" | "event" | "acknowledgementPolicy" | "producerFamily">,
  ): Promise<void> {
    if (this.stopped) throw new Error("runtime-event outbox is stopped; cannot enqueue")
    this.requireLoaded("enqueueBeforeExecution")
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

  async enqueueProducedFact(record: RuntimeEventRecord): Promise<void> {
    await this.enqueueProducedFactBatch([record])
  }

  async enqueueProducedFactBatch(records: readonly RuntimeEventRecord[]): Promise<void> {
    if (this.stopped) throw new Error("runtime-event outbox is stopped; cannot enqueue")
    this.requireLoaded("enqueueProducedFactBatch")
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
      console.warn(
        `runtime-event outbox retention cap ${this.maxRetentionEntries} exceeded; ${this.records.size} records remain after dropping all streaming deltas`,
      )
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
    if (this.networkRetry) {
      this.timer.clearTimeout(this.networkRetry)
      this.networkRetry = null
    }
    if (this.localRetry) {
      this.timer.clearTimeout(this.localRetry)
      this.localRetry = null
    }
    if (this.snapshotInFlight) {
      try { await this.snapshotInFlight } catch { /* best effort */ }
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
      const groups = collectGroups([...this.records.values()])
      if (groups.length === 0) break
      const tick: Promise<boolean>[] = []
      for (const group of groups.slice(0, this.boundedConcurrency)) {
        tick.push(this.drainGroup(group.label, signal))
      }
      if (tick.length === 0) break
      const outcomes = await Promise.allSettled(tick)
      const acknowledged = outcomes.some((r) => r.status === "fulfilled" && r.value === true)
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
      const recordLabel = sequenceKeyLabel(sequenceKey(record))
      if (recordLabel !== label) continue
      matching.push(record)
      if (matching.length >= limit) break
    }
    matching.sort(sortBySequence)
    return matching
  }

  private async deliverBatch(batch: InternalRecord[], signal: AbortSignal): Promise<boolean> {
    if (signal.aborted || batch.length === 0) return false
    const controller = new AbortController()
    const abortFromStop = () => controller.abort(signal.reason)
    signal.addEventListener("abort", abortFromStop, { once: true })
    let timedOut = false
    const timer = this.timer.setTimeout(() => {
      if (!controller.signal.aborted) {
        timedOut = true
        controller.abort(new Error(`runtime-event delivery timeout after ${this.deliveryTimeoutMs}ms`))
      }
    }, this.deliveryTimeoutMs)
    let perRecord: AgentSessionRuntimeEventReceipt[][] | null = null
    let transportError: unknown = null
    try {
      perRecord = await this.deliverBatchRecords(batch, controller.signal)
    } catch (error) {
      transportError = error
    } finally {
      this.timer.clearTimeout(timer)
      signal.removeEventListener("abort", abortFromStop)
    }
    if (timedOut) {
      transportError = new Error(`runtime-event delivery timeout after ${this.deliveryTimeoutMs}ms`)
    }
    if (transportError || perRecord === null) {
      this.scheduleNetworkRetry()
      return false
    }
    // Settle each record against its own policy and the receipts returned
    // for its position in the batch. Only records whose head pointer is
    // unchanged (not rolled back / replaced mid-flight) are removed.
    const removed: InternalRecord[] = []
    let anyAcknowledged = false
    for (let i = 0; i < batch.length; i += 1) {
      const record = batch[i]
      const receipts = perRecord[i] ?? []
      if (!recordMeetsPolicy(record.acknowledgementPolicy, record, receipts)) continue
      if (this.records.get(record.id) !== record) continue
      this.records.delete(record.id)
      removed.push(record)
      anyAcknowledged = true
    }
    if (!anyAcknowledged) {
      this.scheduleNetworkRetry()
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

  private async importLegacyFileIfPresent(): Promise<void> {
    if (!this.legacyFilePath) return
    let raw: string | null
    try {
      raw = await this.fileSystem.readText(this.legacyFilePath)
    } catch (error) {
      this.lastLoadError = error instanceof Error ? error : new Error(errorMessage(error))
      throw error
    }
    if (raw === null) return
    const parsed = parseLegacyFailureOutbox(raw)
    if (parsed === null) return
    let mutated = false
    for (const entry of parsed.entries) {
      const id = `legacy-followup-terminal:${entry.operationId}`
      if (this.records.has(id)) continue
      const sequence = this.monotonicSequence()
      const type = "session.activity"
      const payload: Record<string, unknown> = {
        activity: entry.status === "failed" ? "unknown" : "idle",
        source: "followup",
        operationId: entry.operationId,
        runtimeSessionId: entry.runtimeSessionId,
        completedAt: entry.completedAt,
      }
      if (entry.error) payload.failureReason = entry.error
      const target = legacyTargetToRuntimeTarget(entry.target)
      this.records.set(id, {
        id,
        producerFamily: target.kind === "workflow" ? "workflow-session" : "generic-followup",
        target,
        runtimeSessionId: entry.runtimeSessionId,
        work: null,
        event: { type, payload },
        acknowledgementPolicy: "successful-response",
        sequence,
        enqueuedAt: this.now().toISOString(),
      })
      mutated = true
    }
    if (mutated) await this.enqueueSnapshotWrite()
    try {
      await this.fileSystem.markMigrated(this.legacyFilePath)
    } catch {
      // Already migrated or missing file — best effort.
    }
  }
}

interface GroupSnapshot {
  readonly label: string
  readonly records: InternalRecord[]
}

function collectGroups(records: InternalRecord[]): GroupSnapshot[] {
  const groups = new Map<string, InternalRecord[]>()
  for (const record of records) {
    const label = sequenceKeyLabel(sequenceKey(record))
    const list = groups.get(label)
    if (list) list.push(record)
    else groups.set(label, [record])
  }
  return [...groups.entries()].map(([label, list]) => ({
    label,
    records: list.sort(sortBySequence),
  }))
}

function sequenceKey(record: RuntimeEventRecord): SequenceKey {
  if (record.producerFamily === "workflow-session") {
    if (record.target.kind !== "workflow") throw new Error("workflow-session family requires workflow target")
    return {
      family: "workflow-session",
      projectId: record.target.projectId,
      workflowRunId: record.target.workflowRunId,
      sessionName: record.target.sessionName,
    }
  }
  if (record.producerFamily === "binding-reconcile") {
    if (record.target.kind !== "session") throw new Error("binding-reconcile family requires session target")
    return { family: "binding-reconcile", sessionId: record.target.sessionId }
  }
  if (record.target.kind !== "generic") throw new Error("generic-followup family requires generic target")
  return {
    family: "generic-followup",
    projectId: record.target.projectId,
    sessionId: record.target.sessionId,
  }
}

function sequenceKeyLabel(key: SequenceKey): string {
  if (key.family === "workflow-session") {
    return `workflow-session:${key.projectId}:${key.workflowRunId}:${key.sessionName}`
  }
  if (key.family === "binding-reconcile") return `binding-reconcile:${key.sessionId}`
  return `generic-followup:${key.projectId}:${key.sessionId}`
}

function recordMeetsPolicy(
  policy: RuntimeEventAcknowledgementPolicy,
  record: RuntimeEventRecord,
  receipts: AgentSessionRuntimeEventReceipt[],
): boolean {
  if (policy === "successful-response") return true
  return receipts.some((entry) => entry.type === record.event.type)
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
    if (!isPlainObject(value) || value["version"] !== RUNTIME_EVENT_OUTBOX_VERSION || !Array.isArray(value["entries"])) {
      return null
    }
    const entries: InternalRecord[] = []
    for (const item of value["entries"]) {
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
  const id = value["id"]
  const target = value["target"]
  const family = value["producerFamily"]
  const runtimeSessionId = value["runtimeSessionId"]
  const event = value["event"]
  const policy = value["acknowledgementPolicy"]
  const work = value["work"] ?? null
  const sequence = value["sequence"]
  const enqueuedAt = value["enqueuedAt"]
  if (typeof id !== "string" || !isRuntimeTarget(target)
    || (family !== "workflow-session" && family !== "generic-followup" && family !== "binding-reconcile")
    || typeof runtimeSessionId !== "string"
    || !isRuntimeEvent(event)
    || (policy !== "matching-receipt" && policy !== "successful-response")
    || typeof sequence !== "number"
    || typeof enqueuedAt !== "string") {
    return null
  }
  if (work !== null && !isRuntimeWorkMetadata(work)) return null
  return {
    id,
    producerFamily: family,
    target,
    runtimeSessionId,
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
    work: record.work,
    event: record.event,
    acknowledgementPolicy: record.acknowledgementPolicy,
  }
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value)
}

function isRuntimeTarget(value: unknown): value is RuntimeEventTarget {
  if (!isPlainObject(value)) return false
  if (value["kind"] === "workflow") {
    return typeof value["projectId"] === "string"
      && typeof value["workflowRunId"] === "string"
      && typeof value["sessionName"] === "string"
  }
  if (value["kind"] === "generic") {
    return typeof value["projectId"] === "string"
      && typeof value["sessionId"] === "string"
  }
  if (value["kind"] === "session") return typeof value["sessionId"] === "string"
  return false
}

function isRuntimeEvent(value: unknown): value is RuntimeEventEntry {
  if (!isPlainObject(value)) return false
  const type = value["type"]
  const payload = value["payload"]
  return typeof type === "string" && isPlainObject(payload)
}

function isRuntimeWorkMetadata(value: unknown): value is RuntimeEventWorkMetadata {
  if (!isPlainObject(value)) return false
  return typeof value["workId"] === "string"
    && typeof value["workType"] === "string"
    && (value["stage"] === null || typeof value["stage"] === "string")
}

function cloneInternal(record: InternalRecord): InternalRecord {
  return {
    id: record.id,
    producerFamily: record.producerFamily,
    target: { ...record.target },
    runtimeSessionId: record.runtimeSessionId,
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

interface LegacyFailureRecord {
  readonly operationId: string
  readonly target: SessionTarget
  readonly runtimeSessionId: string
  readonly status: "completed" | "failed"
  readonly error: string | null
  readonly completedAt: string
}

interface LegacyFailureOutboxShape {
  version: number
  entries: LegacyFailureRecord[]
}

function parseLegacyFailureOutbox(raw: string): LegacyFailureOutboxShape | null {
  try {
    const value = JSON.parse(raw) as unknown
    if (!isPlainObject(value) || value["version"] !== LEGACY_FOLLOWUP_FAILURE_VERSION || !Array.isArray(value["entries"])) {
      return null
    }
    const entries: LegacyFailureRecord[] = []
    for (const item of value["entries"]) {
      const parsed = parseLegacyRecord(item)
      if (!parsed) return null
      entries.push(parsed)
    }
    return { version: LEGACY_FOLLOWUP_FAILURE_VERSION, entries }
  } catch {
    return null
  }
}

function parseLegacyRecord(value: unknown): LegacyFailureRecord | null {
  if (!isPlainObject(value)) return null
  const operationId = value["operationId"]
  const target = value["target"]
  const runtimeSessionId = value["runtimeSessionId"]
  const status = value["status"]
  const error = value["error"]
  const completedAt = value["completedAt"]
  if (typeof operationId !== "string"
    || !isLegacySessionTarget(target)
    || typeof runtimeSessionId !== "string"
    || (status !== "completed" && status !== "failed")
    || (error !== null && typeof error !== "string")
    || typeof completedAt !== "string") {
    return null
  }
  return {
    operationId,
    target,
    runtimeSessionId,
    status,
    error,
    completedAt,
  }
}

function isLegacySessionTarget(value: unknown): value is SessionTarget {
  if (!isPlainObject(value)) return false
  if (value["kind"] === "workflow") {
    return typeof value["projectId"] === "string"
      && typeof value["workflowRunId"] === "string"
      && typeof value["sessionName"] === "string"
  }
  if (value["kind"] === "generic") {
    return typeof value["projectId"] === "string"
      && typeof value["sessionId"] === "string"
  }
  return false
}

function legacyTargetToRuntimeTarget(target: SessionTarget): RuntimeEventTarget {
  if (target.kind === "workflow") {
    return {
      kind: "workflow",
      projectId: target.projectId,
      workflowRunId: target.workflowRunId,
      sessionName: target.sessionName,
    }
  }
  return { kind: "generic", projectId: target.projectId, sessionId: target.sessionId }
}

async function defaultDelivery(_record: RuntimeEventRecord, _signal: AbortSignal): Promise<AgentSessionRuntimeEventReceipt[]> {
  throw new Error("Runtime event outbox has no delivery implementation; inject one via options.deliver")
}

interface SequenceKey {
  readonly family: "workflow-session" | "generic-followup" | "binding-reconcile"
  readonly projectId?: string
  readonly workflowRunId?: string
  readonly sessionName?: string
  readonly sessionId?: string
}
