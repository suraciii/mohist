/**
 * Layered timeout signal: returns a fresh signal that aborts after
 * `timeoutMs` OR when the parent signal aborts (whichever comes first).
 *
 * The abort reason preserves the timeout-vs-parent distinction:
 *   - parent-abort first ⇒ child's signal carries `parent.reason`
 *   - timeout fires first ⇒ child's signal aborts with
 *     `Error("Timed out after Ns")` (matched as `/Timed out after/`)
 *
 * Single implementation shared by `core/script` (`actions/registry.ts`),
 * `mohist/opencode`, and `runCommand`'s per-command timeout so callers
 * never reimplement signal-layered timeout.
 */
export interface TimeoutSignalHandle {
  signal: AbortSignal
  dispose(): void
  timedOut(): boolean
}

export function createTimeoutSignal(parent: AbortSignal, timeoutMs: number): TimeoutSignalHandle {
  const controller = new AbortController()
  let timer: ReturnType<typeof setTimeout> | undefined
  let onAbort: (() => void) | undefined
  let timeoutFired = false

  const cleanup = () => {
    if (timer !== undefined) {
      clearTimeout(timer)
      timer = undefined
    }
    if (onAbort) {
      parent.removeEventListener("abort", onAbort)
      onAbort = undefined
    }
  }

  const abort = () => controller.abort(parent.reason)
  if (parent.aborted) {
    abort()
  } else {
    onAbort = () => {
      abort()
      cleanup()
    }
    timer = setTimeout(() => {
      timeoutFired = true
      controller.abort(new Error(`Timed out after ${timeoutMs / 1000}s`))
      cleanup()
    }, timeoutMs)
    parent.addEventListener("abort", onAbort, { once: true })
  }

  return {
    signal: controller.signal,
    dispose: cleanup,
    timedOut: () => timeoutFired,
  }
}

export function timeoutSignal(parent: AbortSignal, timeoutMs: number) {
  return createTimeoutSignal(parent, timeoutMs).signal
}
