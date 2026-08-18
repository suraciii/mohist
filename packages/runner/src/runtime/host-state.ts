import type { DispatchWorkItem, WorkItemResult } from '../core/types.js'
import type { RuntimeRecoveryReceipt } from './recovery-receipt.js'

/**
 * Shutdown bookkeeping carried on every in-flight entry while the
 * Runner is finalising a stop. Owned by the host; the worker-pool loop and
 * the shutdown-recorder share it through the ShutdownInFlightEntry alias.
 */
export interface ShutdownWorkState {
  requested: boolean
  stopConfirmed: boolean
  operationId: string | null
  stopFailure?: string | null
}

/**
 * The runner-process reported set is PROCESS-LIFETIME state, not per-poll.
 * It tracks works the process is executing (`inFlight`) and works whose
 * result has not yet been acked (`awaitingAck`). Both survive poll
 * exceptions and connection resets: a poll that throws must not discard
 * works still executing or awaiting ack, or the next poll's report will
 * drop them and the server will re-dispatch — a rollback storm that
 * duplicates execution and eventually fails works as runner-lost.
 */
export interface InFlightEntry {
  /** The execution promise; resolves when the work settles (success or failure). */
  done: Promise<void>
  readonly work: DispatchWorkItem
  /** A settled result held only in memory must not turn the loop into a busy poll. */
  awaitingResultPersistence: boolean
  readonly controller: AbortController
  shutdown?: ShutdownWorkState
  terminalPersisted?: boolean
}

export interface AwaitingAckEntry {
  /** The result to (re-)report until the owner acks (Accepted or Stale). */
  result: WorkItemResult
  /** A receipt-shaped terminal identity when the bound Agent turn is known. */
  receipt?: RuntimeRecoveryReceipt
  /** Monotonic attempt count for diagnostics. */
  attempts: number
  /** Earliest wall-clock time for the next bounded report attempt. */
  retryAt: number | null
}
