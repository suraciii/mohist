import { runnerLogger } from '../system/logger.js'

const log = runnerLogger.child('host')

/**
 * Minimum interval between repeated "opencode runtime not ready"
 * warnings while the runner pauses claiming. The gate fires on every
 * loop tick when the runtime is unhealthy; without this throttle a
 * not-ready window would spam the log every poll. The first emission
 * always logs; the same diagnostic message is then re-logged at most
 * once per interval (or as soon as the message changes).
 */
export const READINESS_DIAGNOSTIC_RELOG_INTERVAL_MS = 30_000

/** Maximum time a single poll request may wait before the loop retries. */
export const POLL_TIMEOUT_MS = 10_000

/**
 * Minimum delay before the reconciliation loop re-attempts an awaitingAck
 * entry whose report transport previously failed.
 */
export const AWAITING_ACK_RETRY_INTERVAL_MS = 5_000

export const TASK_LOG_UPLOAD_TIMEOUT_MS = 250

/**
 * Maximum time an incremental task-log upload is allowed to take.
 * Distinct from the terminal-batch timeout because incremental batches
 * are smaller but the rail tolerates more slack. Larger
 * than the terminal timeout because we accept second-level latency for
 * the live channel.
 */
export const TASK_LOG_INCREMENTAL_UPLOAD_TIMEOUT_MS = 5_000

/**
 * Wall-clock interval between incremental flush trigger fires. The
 * trigger fires regardless of whether new lines have arrived — an
 * empty drain then short-circuits without an upload.
 */
export const TASK_LOG_FLUSH_INTERVAL_MS = 1_500

/**
 * Threshold on the count of new (un-drained) lines buffered past the
 * sent-seq watermark. Crossing this threshold on a write fires the
 * trigger eagerly, so a chatty command does not have to wait for the
 * interval to see its tail in the web view.
 */
export const TASK_LOG_FLUSH_LINE_THRESHOLD = 200

export function terminalDeliveryFailure(
  error: unknown,
): { kind: 'conflict' | 'not-found' | 'local'; status?: number; code?: string; message: string } | null {
  const candidate = error as { status?: unknown; code?: unknown; message?: unknown } | null
  const status = typeof candidate?.status === 'number' ? candidate.status : undefined
  const code = typeof candidate?.code === 'string' ? candidate.code : undefined
  const message = typeof candidate?.message === 'string' ? candidate.message : String(error)
  if (status === 409 || code === 'terminal_snapshot_conflict')
    return { kind: 'conflict', ...(status === undefined ? {} : { status }), ...(code ? { code } : {}), message }
  if (status === 404 || code === 'not_found')
    return { kind: 'not-found', ...(status === undefined ? {} : { status }), ...(code ? { code } : {}), message }
  if (status !== undefined && status >= 400 && status < 500)
    return { kind: 'local', status, ...(code ? { code } : {}), message }
  if (code === 'terminal_ack_missing')
    return { kind: 'local', ...(status === undefined ? {} : { status }), code, message }
  return null
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

export async function delay(ms: number, signal: AbortSignal) {
  if (signal.aborted) throw signal.reason
  await new Promise<void>((resolve, reject) => {
    const timer = setTimeout(() => {
      signal.removeEventListener('abort', onAbort)
      resolve()
    }, ms)
    const onAbort = () => {
      clearTimeout(timer)
      reject(signal.reason)
    }
    signal.addEventListener('abort', onAbort, { once: true })
  })
}

/**
 * Race a poll-interval timer against in-flight work promises. Unlike
 * {@link delay} wrapped in `Promise.race`, the interval timer is owned here:
 * whichever racer settles first, the timer is cleared and its promise
 * resolved, so no pending promise lingers to reject on a later abort and
 * surface as an unhandled rejection. The `signal` aborts the wait promptly
 * (resolving, since every caller re-checks `signal.aborted` afterwards).
 */
export function raceInterval(ms: number, signal: AbortSignal, racers: Promise<unknown>[]): Promise<void> {
  return new Promise((resolve) => {
    let timer: ReturnType<typeof setTimeout> | null = null
    let settled = false
    const done = () => {
      if (settled) return
      settled = true
      if (timer) clearTimeout(timer)
      signal.removeEventListener('abort', onAbort)
      resolve()
    }
    const onAbort = done
    if (signal.aborted) {
      done()
      return
    }
    timer = setTimeout(done, ms)
    timer.unref?.()
    signal.addEventListener('abort', onAbort, { once: true })
    for (const r of racers) r.then(done, done)
  })
}

export function boundedSignal(parent: AbortSignal, timeoutMs: number): { signal: AbortSignal; dispose: () => void } {
  const controller = new AbortController()
  const abortFromParent = () => controller.abort(parent.reason)
  if (parent.aborted) abortFromParent()
  else parent.addEventListener('abort', abortFromParent, { once: true })

  const timeout = setTimeout(() => controller.abort(new Error(`request timed out after ${timeoutMs}ms`)), timeoutMs)
  timeout.unref?.()

  return {
    signal: controller.signal,
    dispose: () => {
      clearTimeout(timeout)
      parent.removeEventListener('abort', abortFromParent)
    },
  }
}
