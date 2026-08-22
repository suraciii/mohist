import { boundedWait } from '../bounded-wait.js'
import { diagnostic } from './errors.js'
import { CANCEL_CONFIRMATION_TIMEOUT_MS, type PiClock } from './runtime-clock.js'
import type { PiSdkSession } from './sdk.js'
import type { PiDiagnostic } from './types.js'

export function watchPiStop(
  session: PiSdkSession,
  clock: PiClock,
): { readonly wait: Promise<boolean>; readonly dispose: () => void } {
  let resolveWait: (confirmed: boolean) => void = () => {}
  const wait = new Promise<boolean>((resolve) => {
    resolveWait = resolve
  })
  let settled = false
  let stopEventObserved = false
  let timeout: unknown | null = null
  let unsubscribe: (() => void) | null = null
  const complete = (confirmed: boolean) => {
    if (settled) return
    settled = true
    if (timeout !== null) clock.clearTimeout(timeout)
    unsubscribe?.()
    resolveWait(confirmed)
  }
  const removeListener = session.subscribe((event) => {
    if (isPiStopEvent(event)) {
      stopEventObserved = true
      if (!session.isStreaming) complete(true)
    }
  })
  unsubscribe = removeListener
  if (settled) {
    removeListener()
    return { wait, dispose: () => complete(false) }
  }
  timeout = clock.setTimeout(() => complete(stopEventObserved && !session.isStreaming), CANCEL_CONFIRMATION_TIMEOUT_MS)
  return { wait, dispose: () => complete(false) }
}

export async function abortAndDiagnose(
  session: PiSdkSession,
  diagnostics: PiDiagnostic[],
  mask: (text: string) => string,
): Promise<void> {
  try {
    const completed = await boundedWait(() => session.abort(), CANCEL_CONFIRMATION_TIMEOUT_MS)
    if (!completed || session.isStreaming)
      diagnostics.push(diagnostic('abort-unconfirmed', mask('Pi did not confirm that the turn stopped')))
  } catch (cause) {
    diagnostics.push(diagnostic('abort-unconfirmed', mask(message(cause))))
  }
}

function isPiStopEvent(event: unknown): boolean {
  return Boolean(event && typeof event === 'object' && (event as { type?: unknown }).type === 'agent_settled')
}

function message(cause: unknown): string {
  return cause instanceof Error ? cause.message || 'Pi operation failed' : String(cause)
}
