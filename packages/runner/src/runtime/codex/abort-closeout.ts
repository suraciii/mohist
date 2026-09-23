import { redactCodexCredentialString } from './credential.js'
import { normalizeUnknownCodex } from './errors.js'
import { CODEX_DEFAULT_TIMEOUTS, type CodexClock, type CodexResult, type CodexTurnResult } from './types.js'

interface AbortTransport {
  send<P, R>(request: { readonly method: string; readonly params?: P; readonly id: number }): Promise<R>
}

export interface CodexAbortCloseoutSession {
  readonly expectedThreadId: string
  readonly expectedTurnId: string | null
  fixedUnknown: CodexResult<CodexTurnResult> | null
  resolve(value: CodexResult<CodexTurnResult>): void
}

export interface CodexAbortCloseoutOptions {
  readonly transport: AbortTransport
  readonly signal: AbortSignal
  readonly clock: CodexClock
  readonly nextRequestId: () => number
}

/**
 * Stop an AgentJob-owned Turn when its execution signal is aborted. The
 * terminal event remains the only confirmed completion authority; a lost
 * interrupt or missing terminal event fixes the outcome as unknown.
 */
export function installAbortCloseout(
  options: CodexAbortCloseoutOptions,
  session: CodexAbortCloseoutSession,
): { dispose(): void } {
  let disposed = false
  let timer: unknown = null
  let requested = false

  const resolveUnknown = (message: string, cause?: unknown): void => {
    if (disposed || session.fixedUnknown) return
    const detail = cause instanceof Error ? `: ${redactCodexCredentialString(cause.message)}` : ''
    const error = normalizeUnknownCodex(`${message}${detail}`)
    const result: CodexResult<CodexTurnResult> = { ok: false, error, diagnostics: error.diagnostics }
    session.fixedUnknown = result
    session.resolve(result)
  }

  const onAbort = (): void => {
    if (disposed || requested) return
    requested = true
    const turnId = session.expectedTurnId
    if (!turnId) {
      resolveUnknown('Codex turn abort could not identify the active Turn')
      return
    }
    timer = options.clock.setTimeout(
      () => resolveUnknown(`Codex turn interrupt was not confirmed for thread ${session.expectedThreadId}`),
      CODEX_DEFAULT_TIMEOUTS.cancelConfirmationMs,
    )
    void options.transport
      .send<unknown, unknown>({
        id: options.nextRequestId(),
        method: 'turn/interrupt',
        params: { threadId: session.expectedThreadId, turnId },
      })
      .catch((cause: unknown) => resolveUnknown('Codex turn interrupt failed before terminal confirmation', cause))
  }

  if (options.signal.aborted) onAbort()
  else options.signal.addEventListener('abort', onAbort, { once: true })

  return {
    dispose() {
      if (disposed) return
      disposed = true
      if (timer !== null) options.clock.clearTimeout(timer)
      options.signal.removeEventListener('abort', onAbort)
    },
  }
}
