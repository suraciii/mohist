import type { PiClock } from './runtime-clock.js'
import type { PiSdkMessage, PiSdkSession } from './sdk.js'

const SETTLE_OBSERVATION_DELAY_MS = 5_000

/** Observes only the prompt whose pre-existing messages were captured by its owner. */
export function startSettleGuard(deps: {
  readonly clock: PiClock
  readonly session: PiSdkSession
  readonly onSettle: (message: PiSdkMessage) => void
}): { readonly observe: (event: unknown) => void; readonly dispose: () => void } {
  const previousMessages = new Set(deps.session.messages)
  let timer: unknown | null = null
  let stopped = false
  const dispose = () => {
    stopped = true
    if (timer !== null) deps.clock.clearTimeout(timer)
    timer = null
  }
  const check = () => {
    if (stopped || deps.session.isStreaming) return
    const message = deps.session.messages.at(-1)
    if (
      !message ||
      previousMessages.has(message) ||
      message.role !== 'assistant' ||
      !['stop', 'length', 'error', 'aborted'].includes(message.stopReason ?? '')
    )
      return
    dispose()
    deps.onSettle(message)
  }
  const deferCheck = () => {
    if (stopped) return
    if (timer !== null) deps.clock.clearTimeout(timer)
    // Lifecycle events can precede the SDK's idle transition while its
    // awaited extension callbacks are still completing.
    timer = deps.clock.setTimeout(() => {
      timer = null
      check()
    }, SETTLE_OBSERVATION_DELAY_MS)
  }
  deferCheck()
  return {
    observe: (event) => {
      if (stopped || !event || typeof event !== 'object') return
      const type = (event as { type?: unknown }).type
      if (type === 'agent_settled') check()
      if (
        type === 'message_end' ||
        type === 'agent_end' ||
        type === 'agent_settled' ||
        type === 'auto_retry_end' ||
        type === 'compaction_end'
      )
        deferCheck()
    },
    dispose,
  }
}
