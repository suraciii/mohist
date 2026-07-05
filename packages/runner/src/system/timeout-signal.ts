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
 * `mohist/acp-agent`, and `runCommand`'s per-command timeout so callers
 * never reimplement signal-layered timeout.
 */
export function timeoutSignal(parent: AbortSignal, timeoutMs: number) {
  const controller = new AbortController()
  const abort = () => controller.abort(parent.reason)
  if (parent.aborted) {
    abort()
  } else {
    const onAbort = () => {
      clearTimeout(timer)
      abort()
    }
    const timer = setTimeout(() => {
      controller.abort(new Error(`Timed out after ${timeoutMs / 1000}s`))
      parent.removeEventListener("abort", onAbort)
    }, timeoutMs)
    parent.addEventListener("abort", onAbort, { once: true })
  }
  return controller.signal
}