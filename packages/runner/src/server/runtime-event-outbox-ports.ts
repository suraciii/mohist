import { dirname } from 'node:path'
import { currentRunnerFileSystem } from '../system/filesystem.js'
import type { AgentSessionRuntimeEventReceipt } from './connection.js'

let temporaryFileSequence = 0

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

export interface AgentSessionRuntimeEventOutboxOptions {
  readonly fileSystem?: RuntimeEventOutboxFileSystem
  readonly filePath?: string
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

export const defaultRuntimeEventOutboxTimer: RuntimeEventOutboxTimer = {
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
    record: Pick<
      RuntimeEventRecord,
      'id' | 'target' | 'runtimeSessionId' | 'runtime' | 'work' | 'event' | 'acknowledgementPolicy' | 'producerFamily'
    >,
  ): Promise<void>
  awaitInputReceipt?(recordId: string): Promise<AgentSessionRuntimeEventReceipt>
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

export class NodeRuntimeEventOutboxFileSystem implements RuntimeEventOutboxFileSystem {
  async readText(path: string): Promise<string | null> {
    try {
      return await currentRunnerFileSystem().readText(path)
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'ENOENT') return null
      throw error
    }
  }

  async writeAtomicText(path: string, body: string): Promise<void> {
    const fileSystem = currentRunnerFileSystem()
    await fileSystem.ensureDir(dirname(path))
    const temporary = nextTemporaryFilePath(path)
    try {
      await fileSystem.writeText(temporary, body, { mode: 0o600 })
      await fileSystem.rename(temporary, path)
    } catch (error) {
      // A failed write can leave a partial temporary file behind (notably on
      // ENOSPC). Remove only this attempt's unique path and preserve the
      // original error for the outbox's existing recovery handling.
      await fileSystem.deleteFile(temporary).catch(() => undefined)
      throw error
    }
  }
}

export function nextTemporaryFilePath(path: string): string {
  temporaryFileSequence += 1
  return `${path}.${temporaryFileSequence}.tmp`
}
