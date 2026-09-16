import { diagnostic, failureDiagnostic } from './errors.js'
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
  clock: PiClock,
  mask: (text: string) => string,
): Promise<readonly PiDiagnostic[]> {
  let timer: unknown | null = null
  const aborted = Promise.resolve()
    .then(() => session.abort())
    .then(
      () =>
        session.isStreaming
          ? [
              diagnostic('abort-unconfirmed', 'Pi did not confirm that the turn stopped', 'error', {
                phase: 'abort',
                outcome: 'streaming',
              }),
            ]
          : [],
      (cause: unknown) => [
        failureDiagnostic('abort-unconfirmed', cause, mask, {
          phase: 'abort',
          outcome: 'rejected',
        }),
      ],
    )
  const expired = new Promise<readonly PiDiagnostic[]>((resolve) => {
    timer = clock.setTimeout(
      () =>
        resolve([
          diagnostic('abort-unconfirmed', 'Pi abort did not complete within its confirmation deadline', 'error', {
            phase: 'abort',
            outcome: 'timeout',
            timeoutMs: CANCEL_CONFIRMATION_TIMEOUT_MS,
          }),
        ]),
      CANCEL_CONFIRMATION_TIMEOUT_MS,
    )
  })
  try {
    return await Promise.race([aborted, expired])
  } finally {
    if (timer !== null) clock.clearTimeout(timer)
  }
}

function isPiStopEvent(event: unknown): boolean {
  return Boolean(event && typeof event === 'object' && (event as { type?: unknown }).type === 'agent_settled')
}
