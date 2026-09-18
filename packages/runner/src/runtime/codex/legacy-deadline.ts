import { normalizeDeadlineExceededCodex } from './errors.js'
import type { CodexDeadlineInterruptHandle } from './closeout.js'
import type { CodexClock, CodexResult, CodexTurnResult } from './types.js'

export interface LegacyDeadlineOptions {
  readonly deadlineMs: number | null
  readonly clock?: CodexClock
}

export interface LegacyDeadlineSession {
  readonly settled: { readonly promise: Promise<unknown> }
  readonly unknownItems: readonly {
    readonly severity: 'info' | 'warning' | 'error'
    readonly code: string
    readonly message: string
  }[]
  fixedDeadline: CodexResult<CodexTurnResult> | null
  resolve(value: CodexResult<CodexTurnResult>): void
}

/** Preserve the pre-closeout immediate deadline result while the closeout module confirms interruption. */
export function legacyScheduleDeadlineCloseout(
  options: LegacyDeadlineOptions,
  session: LegacyDeadlineSession,
  interrupt: CodexDeadlineInterruptHandle | null,
  clock: CodexClock,
): { dispose(): void } {
  const deadlineMs = options.deadlineMs as number
  const fixedAtDeadline = () => {
    const error = normalizeDeadlineExceededCodex(deadlineMs, [...session.unknownItems])
    const result: CodexResult<CodexTurnResult> = { ok: false, error, diagnostics: error.diagnostics }
    session.fixedDeadline = result
    session.resolve(result)
    if (interrupt) void interrupt.awaitConfirmation()
  }
  const timer = clock.setTimeout(fixedAtDeadline, deadlineMs)
  return { dispose: () => clock.clearTimeout(timer) }
}
