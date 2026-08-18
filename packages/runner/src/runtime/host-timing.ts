/** Maximum time a single poll request may wait before the loop retries. */
export const POLL_TIMEOUT_MS = 10_000

/**
 * Minimum delay before the reconciliation loop re-attempts an awaitingAck
 * entry whose report transport previously failed.
 */
export const AWAITING_ACK_RETRY_INTERVAL_MS = 5_000

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

/**
 * Race a promise against a timeout. Returns `null` when the timeout wins;
 * the caller distinguishes a value-equals-null completion from a timed-out
 * race and surfaces the difference as its own error. The internal timer is
 * always cleared so a late settlement cannot leak an unref'd handle.
 */
export async function withTimeout<T>(promise: Promise<T>, timeoutMs: number): Promise<T | null> {
  if (timeoutMs <= 0) return null
  let timer: ReturnType<typeof setTimeout> | undefined
  try {
    return await Promise.race([
      promise,
      new Promise<null>((resolve) => {
        timer = setTimeout(() => resolve(null), timeoutMs)
        timer.unref?.()
      }),
    ])
  } finally {
    if (timer !== undefined) clearTimeout(timer)
  }
}
