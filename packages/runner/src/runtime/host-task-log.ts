import type { DispatchWorkItem, RunnerOptions, WorkItemResult } from '../core/types.js'
import type { ServerConnection } from '../server/connection.js'
import { WorkExecutor } from './executor.js'
import { TaskLogCollector } from './task-log.js'
import { runnerLogger } from '../system/logger.js'
import type { ManagerExecutionBoundary } from './manager-execution-boundary.js'

const log = runnerLogger.child('host')

const TASK_LOG_UPLOAD_TIMEOUT_MS = 250

/**
 * Maximum time an incremental task-log upload is allowed to take.
 * Distinct from the terminal-batch timeout because incremental batches
 * are smaller but the rail tolerates more slack. Larger
 * than the terminal timeout because we accept second-level latency for
 * the live channel.
 */
const TASK_LOG_INCREMENTAL_UPLOAD_TIMEOUT_MS = 5_000

/**
 * Wall-clock interval between incremental flush trigger fires. The
 * trigger fires regardless of whether new lines have arrived — an
 * empty drain then short-circuits without an upload.
 */
const TASK_LOG_FLUSH_INTERVAL_MS = 1_500

/**
 * Threshold on the count of new (un-drained) lines buffered past the
 * sent-seq watermark. Crossing this threshold on a write fires the
 * trigger eagerly, so a chatty command does not have to wait for the
 * interval to see its tail in the web view.
 */
const TASK_LOG_FLUSH_LINE_THRESHOLD = 200

export interface HostTaskLogDeps {
  readonly connection: ServerConnection
  readonly options: RunnerOptions
}

async function uploadTerminalTaskLog(
  deps: HostTaskLogDeps,
  ownerId: string,
  ownerKind: 'workflow' | 'agent-job',
  workId: string,
  batch: import('./task-log.js').TaskLogBatch,
  signal: AbortSignal,
): Promise<void> {
  const controller = new AbortController()
  const abort = () => controller.abort(signal.reason)
  signal.addEventListener('abort', abort, { once: true })
  let timeout: ReturnType<typeof setTimeout> | null = null
  try {
    await Promise.race([
      deps.connection.uploadTaskLog(ownerId, workId, batch, controller.signal, ownerKind, true),
      new Promise<never>((_resolve, reject) => {
        timeout = setTimeout(() => {
          controller.abort()
          reject(new Error(`task-log terminal upload timed out after ${TASK_LOG_UPLOAD_TIMEOUT_MS}ms`))
        }, TASK_LOG_UPLOAD_TIMEOUT_MS)
        timeout.unref?.()
      }),
    ])
  } catch (error) {
    log.warn('terminal task-log delivery abandoned', { work: workId, exception: error })
  } finally {
    if (timeout) clearTimeout(timeout)
    signal.removeEventListener('abort', abort)
  }
}

/**
 * Executes a single work item to completion, flushing its task log, and
 * returns the resulting {@link WorkItemResult}. Does NOT report — the
 * caller owns the report lifecycle and
 * the awaitingAck transition so a transport failure is retried rather
 * than lost. Throws on execution failure (including abort); the caller
 * synthesises a `{ status: "failed" }` result from the thrown error.
 *
 * `signal` is the run-lifetime signal; on abort the work is abandoned
 * (re-thrown) without a synthesized result — the caller checks
 * `signal.aborted` before recording a failure.
 */
export async function executeWork(
  deps: HostTaskLogDeps,
  workExecutor: WorkExecutor | null,
  work: DispatchWorkItem,
  signal: AbortSignal,
  managerExecution: ManagerExecutionBoundary | null = null,
): Promise<WorkItemResult> {
  // Owner-id mirrors `artifact-side-effects.ts:107`: agent-job
  // dispatches upload under `work.agentJobId`, workflow dispatches
  // under `work.workflowRunId`. Routing through a single uploadTaskLog
  // call keeps the task-log channel symmetric with artifact uploads.
  const ownerKind = work.ownerKind === 'agent-job' ? 'agent-job' : 'workflow'
  const ownerId = ownerKind === 'agent-job' ? (work.agentJobId ?? '') : work.workflowRunId

  /**
   * Incremental and terminal delivery are best-effort evidence channels.
   */
  const uploadTaskLogBatch = async (
    batch: import('./task-log.js').TaskLogBatch,
    timeoutMs: number,
    label: 'incremental',
  ) => {
    const uploadController = new AbortController()
    let timeout: ReturnType<typeof setTimeout> | null = null
    try {
      await Promise.race([
        deps.connection.uploadTaskLog(ownerId, work.workId, batch, uploadController.signal, ownerKind, false),
        new Promise<never>((_resolve, reject) => {
          timeout = setTimeout(() => {
            uploadController.abort()
            reject(new Error(`task-log ${label} upload timed out after ${timeoutMs}ms`))
          }, timeoutMs)
          timeout.unref?.()
        }),
      ])
    } catch (flushError) {
      log.error('task-log upload failed', { work: work.workId, path: label, exception: flushError })
    } finally {
      if (timeout) clearTimeout(timeout)
    }
  }

  /**
   * Incremental batch primitive. Drains the collector (entries with
   * `seq > watermark`), and when there is something new, uploads it
   * under the larger incremental-timeout constant. An empty drain
   * short-circuits — no network round-trip is issued.
   */
  const flushIncrementalTaskLog = async (collector: import('./task-log.js').TaskLogCollector | null) => {
    if (!collector) return
    const batch = collector.drain()
    if (batch === null) return
    await uploadTaskLogBatch(
      batch,
      deps.options.taskLogIncrementalUploadTimeoutMs ?? TASK_LOG_INCREMENTAL_UPLOAD_TIMEOUT_MS,
      'incremental',
    )
  }

  const startIncrementalFlushForCollector = (collector: import('./task-log.js').TaskLogCollector) => {
    const flushTrigger = startTaskLogFlushTrigger(
      () => flushIncrementalTaskLog(collector),
      deps.options.taskLogFlushIntervalMs ?? TASK_LOG_FLUSH_INTERVAL_MS,
      deps.options.taskLogFlushLineThreshold ?? TASK_LOG_FLUSH_LINE_THRESHOLD,
    )
    collector.setAppendListener(() => flushTrigger.noteAppend())
    return flushTrigger
  }

  if (workExecutor === null) {
    throw new Error('WorkExecutor not initialized; runner host is shutting down')
  }

  // Start the incremental flush trigger alongside executeWithLog and
  // stop it BEFORE the terminal flush so a final drain cannot race
  // the terminal batch.
  // The trigger fires on either an elapsed interval since the last
  // fire or a reached line-count threshold of NEW (un-drained) lines
  // — the latter is checked via `noteAppend`, which the collector
  // calls synchronously from inside `append`. `flushIncrementalTaskLog`
  // short-circuits an empty drain so no upload is issued when there
  // is nothing new.
  // Pre-create the collector so the trigger can be wired into its
  // `appendListener` BEFORE the executor starts emitting appends.
  // Passing `null` to `executeWithLog` would let the executor mint a
  // new collector without our listener — defeats the eager line-count
  // firing and leaves the trigger with no append notifications.
  const collector = new TaskLogCollector()
  const flushTrigger = startIncrementalFlushForCollector(collector)
  try {
    const execution = await workExecutor.executeWithLog(work, signal, collector, managerExecution)
    // Detach the listener before stopping the timer so a stale
    // tick can never re-fire against a collector that the executor
    // has handed back to us for terminal flushing.
    execution.collector.setAppendListener(null)
    // Stop the trigger before the terminal flush and wait for any
    // in-flight incremental upload to settle so terminal
    // reconciliation cannot overlap it.
    await flushTrigger.stop()
    if (signal.aborted) return execution.result
    void uploadTerminalTaskLog(deps, ownerId, ownerKind, work.workId, execution.collector.snapshot(), signal)
    return execution.result
  } catch (error) {
    if (!signal.aborted) void uploadTerminalTaskLog(deps, ownerId, ownerKind, work.workId, collector.snapshot(), signal)
    throw error
  } finally {
    collector.setAppendListener(null)
    await flushTrigger.stop()
  }
}

/**
 * Create an incremental flush trigger. The returned handle exposes
 * `stop()` to clear the interval and wait for any in-flight flush,
 * plus a `noteAppend()` method to register a newly-captured line
 * against the line-count threshold. Callers MUST await `stop()` before
 * the terminal flush so a final drain/upload cannot race the terminal
 * snapshot.
 *
 * `setInterval` is used (rather than a custom timer abstraction) so
 * the trigger is driven by the global JS timer clock and is therefore
 * deterministically controllable by `vi.useFakeTimers` (no real
 * wall-clock, per the project's testing convention).
 *
 * The trigger fires on EITHER:
 *   - an elapsed interval since the last fire (regardless of new
 *     lines — `flush` short-circuits an empty drain), OR
 *   - the line-count threshold being reached between two interval
 *     ticks. `noteAppend` is called once per captured line; when the
 *     running count since the last fire meets or exceeds the threshold,
 *     the trigger fires eagerly.
 *
 * `flush` is the single short-circuit point that skips the network
 * round-trip when the collector's `drain` is empty — the trigger
 * itself always invokes `flush` on a fire. Flushes are serialized per
 * trigger: if a timer/threshold fire happens while an upload is still
 * in flight, one follow-up flush is queued and run after the current
 * one settles.
 *
 * Exported (not just module-private) so the test suite can drive the
 * exact same code path without reimplementing the `setInterval`
 * dance; the host keeps the trigger implementation here as the
 * single source of truth.
 */
export function startTaskLogFlushTrigger(
  flush: () => Promise<void> | void,
  intervalMs: number,
  lineThreshold: number,
): { stop: () => Promise<void>; noteAppend: () => void } {
  // Defensive: a zero/negative interval would create a tight loop.
  // Clamp to a minimum positive value to keep the trigger harmless
  // under accidental misconfiguration. Tests that need finer control
  // can pass an explicit positive `intervalMs`.
  const safeInterval = Math.max(50, Math.floor(intervalMs))
  const safeThreshold = Math.max(1, Math.floor(lineThreshold))
  let pending = 0
  let inFlight: Promise<void> | null = null
  let rerunAfterInFlight = false

  const runFlush = (): Promise<void> => {
    if (inFlight) {
      rerunAfterInFlight = true
      return inFlight
    }

    try {
      const result = flush()
      inFlight = Promise.resolve(result)
        .catch((error) => {
          log.error('task-log incremental flush failed', { exception: error })
        })
        .finally(() => {
          inFlight = null
          if (rerunAfterInFlight) {
            rerunAfterInFlight = false
            void runFlush()
          }
        })
    } catch (error) {
      log.error('task-log incremental flush failed', { exception: error })
      inFlight = null
    }

    return inFlight ?? Promise.resolve()
  }

  const waitForIdle = async () => {
    while (inFlight) await inFlight
  }

  const tick = () => {
    pending = 0
    void runFlush()
  }
  const handle = setInterval(tick, safeInterval)
  handle.unref?.()
  return {
    stop: async () => {
      clearInterval(handle)
      await waitForIdle()
    },
    noteAppend: () => {
      pending += 1
      if (pending >= safeThreshold) tick()
    },
  }
}
