/**
 * Default clock seam for the Codex runtime.
 *
 * Production uses `Date.now()` / `setTimeout` / `clearTimeout`. Tests
 * inject a deterministic clock through `CodexRuntimeDeps.clock`.
 */

import type { CodexClock } from './types.js'

export const defaultCodexClock: CodexClock = {
  now: () => Date.now(),
  setTimeout: (callback, delayMs) => setTimeout(callback, delayMs),
  clearTimeout: (handle) => clearTimeout(handle as ReturnType<typeof setTimeout>),
}

/** Backwards-compatible alias used by the line-framed JSON-RPC consumer. */
export const defaultClock: CodexClock = defaultCodexClock

export const CODEX_CANCEL_CONFIRMATION_TIMEOUT_MS = 5_000
export const CODEX_CLOSEOUT_WARNING_LEAD_MS = 5 * 60_000