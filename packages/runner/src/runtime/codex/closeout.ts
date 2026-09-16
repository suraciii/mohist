/**
 * Codex two-phase closeout and permission / user-input rejection.
 *
 * The two-phase closeout protocol is the runner's deadline discipline:
 *
 *   - **Phase 1 (warning)** — five minutes before the deadline (or
 *     at execution start if the deadline is shorter than the lead),
 *     the runtime calls `turn/steer` once on the exact active Turn
 *     with a task-independent closeout warning. The warning is a
 *     best-effort affordance; a lost steer response is NOT retried.
 *     The warning never names a marker, never repeats a task-specific
 *     contract, and never echoes the original prompt — those
 *     properties are part of the closeout protocol and are
 *     regression-tested by the `closeout.test.ts` fixture.
 *
 *   - **Phase 2 (deadline interrupt)** — at the deadline, the
 *     runtime fixes the result as `deadline-exceeded`, sends
 *     `turn/interrupt`, and awaits bounded confirmation. A late
 *     `turn/completed` does not reverse the fixed deadline result.
 *
 * Server-initiated approval / permission / user-input / MCP
 * elicitation / dynamic-tool requests are answered with the
 * protocol-defined denial, the exact active Turn is interrupted, and
 * `permission-required` is returned only after the Turn reaches a
 * terminal state. Unconfirmed denial (no matching `turn/completed`
 * within the bounded confirmation budget) stays `unknown` /
 * `interruption-unconfirmed` and never creates a Workflow Approval
 * Point. The AgentSession binding is unchanged in that case.
 *
 * The helpers in this file are pure logic; the lifecycle
 * integration in `turn.ts` is responsible for wiring the result-
 * fixing callbacks into the existing `CodexTurnSession`.
 */

import {
  isCodexServerRequest,
  isCodexTurnCompletedEvent,
  isCodexTurnSteerRequest,
  isCodexTurnInputItem,
  type CodexJsonRpcServerRequest,
  type CodexServerRequestParams,
  type CodexTurnCompletedEvent,
  type CodexTurnSteerParams,
} from './protocol-types.js'
import { normalizePermissionRequiredCodex, normalizeUnknownCodex } from './errors.js'
import { redactCodexCredentialString } from './credential.js'
import type { CodexClock, CodexDiagnostic, CodexResult, CodexTurnResult } from './types.js'

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/**
 * Default lead time between the closeout warning and the deadline.
 * The warning fires at `deadlineMs - CODEX_CLOSEOUT_WARNING_LEAD_MS`
 * when the deadline is longer than the lead, otherwise at execution
 * start. The value is locked so call sites and tests can rely on it.
 */
export const CODEX_CLOSEOUT_WARNING_LEAD_MS = 5 * 60_000

/**
 * Default bounded confirmation budget for the interrupt sent at the
 * deadline or after a server-initiated request denial. A
 * confirmation that exceeds the budget is `unknown` /
 * `interruption-unconfirmed` and never creates a Workflow Approval
 * Point; the AgentSession binding is unchanged.
 */
export const CODEX_INTERRUPT_CONFIRMATION_BUDGET_MS = 5_000

/**
 * Task-independent closeout warning. NEVER names a marker, NEVER
 * repeats a task-specific contract, and NEVER echoes the original
 * prompt. The exact text is locked so the warning is recognisable
 * across Turns and across Runtimes; any change to the wording is a
 * regression of the closeout protocol and must be regression-tested
 * by `closeout.test.ts`.
 *
 * The text is constructed once at module load so a future change to
 * the source cannot silently rewrite the warning at runtime; tests
 * assert the exact value.
 */
export const CODEX_CLOSEOUT_WARNING_TEXT =
  'Mohist runner deadline approaching; wrap up the current work and return the final answer.'

// ---------------------------------------------------------------------------
// Internal transport surface
// ---------------------------------------------------------------------------

/**
 * Narrow transport surface used by the closeout helpers. The
 * helpers operate on the same transport the Turn lifecycle owns;
 * the closeout helpers never construct their own consumer.
 */
export interface CodexCloseoutTransport {
  send<P, R>(request: { readonly method: string; readonly params?: P; readonly id: number }): Promise<R>
  denyServerRequest(id: number | string, reason: string): void
  subscribe(listener: (message: unknown) => void): () => void
  hasExited?(): boolean
}

// ---------------------------------------------------------------------------
// Optional observer for diagnostics
// ---------------------------------------------------------------------------

export interface CodexCloseoutObserver {
  onDiagnostic?(diagnostic: CodexDiagnostic): void
}

// ---------------------------------------------------------------------------
// Phase 1 — closeout warning (turn/steer)
// ---------------------------------------------------------------------------

export interface CodexCloseoutWarningHandle {
  /** True after the warning has been scheduled (always true after `scheduleCloseoutWarning` returns). */
  readonly scheduled: boolean
  /** True if the warning has already fired (steer request submitted). */
  readonly fired: boolean
  /** The effective delay before the warning fires. `null` when no deadline was provided. */
  readonly effectiveDelayMs: number | null
  /** Dispose the warning timer. Idempotent. */
  dispose(): void
}

/**
 * Schedule the closeout warning on the exact active Turn. The
 * warning fires once at `min(deadlineMs - CODEX_CLOSEOUT_WARNING_LEAD_MS, 0)` —
 * i.e. when the deadline is longer than the lead, the warning fires
 * 5 minutes before the deadline; when the deadline is shorter (or
 * zero), the warning fires at execution start (immediately).
 *
 * The warning text is the locked task-independent text
 * (`CODEX_CLOSEOUT_WARNING_TEXT`); the steer request is addressed
 * to the exact active Thread + Turn IDs. A lost steer response is
 * NOT retried — the warning is a best-effort affordance.
 */
export function scheduleCloseoutWarning(deps: {
  readonly transport: CodexCloseoutTransport
  readonly threadId: string
  readonly turnId: string
  readonly deadlineMs: number | null
  readonly clock: CodexClock
  readonly nextRequestId: () => number
  readonly onWarningFired?: () => void
  readonly observer?: CodexCloseoutObserver
}): CodexCloseoutWarningHandle {
  const handle: MutableWarningHandle = {
    scheduled: true,
    fired: false,
    effectiveDelayMs: null,
    disposed: false,
    timer: null,
  }
  if (deps.deadlineMs === null) {
    return Object.freeze({
      get scheduled() {
        return handle.scheduled
      },
      get fired() {
        return handle.fired
      },
      get effectiveDelayMs() {
        return handle.effectiveDelayMs
      },
      dispose() {
        disposeHandle(handle, deps.clock)
      },
    })
  }
  const rawDelay = deps.deadlineMs - CODEX_CLOSEOUT_WARNING_LEAD_MS
  const effectiveDelayMs = rawDelay > 0 ? rawDelay : 0
  handle.effectiveDelayMs = effectiveDelayMs

  const fire = () => {
    if (handle.disposed || handle.fired) return
    handle.fired = true
    void deliverCloseoutWarning({
      transport: deps.transport,
      threadId: deps.threadId,
      turnId: deps.turnId,
      nextRequestId: deps.nextRequestId,
      observer: deps.observer,
    })
    deps.onWarningFired?.()
  }

  handle.timer = deps.clock.setTimeout(fire, effectiveDelayMs)
  return Object.freeze({
    get scheduled() {
      return handle.scheduled
    },
    get fired() {
      return handle.fired
    },
    get effectiveDelayMs() {
      return handle.effectiveDelayMs
    },
    dispose() {
      disposeHandle(handle, deps.clock)
    },
  })
}

interface MutableWarningHandle {
  scheduled: boolean
  fired: boolean
  effectiveDelayMs: number | null
  disposed: boolean
  timer: unknown
}

function disposeHandle(handle: MutableWarningHandle, clock: CodexClock): void {
  if (handle.disposed) return
  handle.disposed = true
  if (handle.timer !== null) clock.clearTimeout(handle.timer)
}

async function deliverCloseoutWarning(deps: {
  readonly transport: CodexCloseoutTransport
  readonly threadId: string
  readonly turnId: string
  readonly nextRequestId: () => number
  readonly observer?: CodexCloseoutObserver
}): Promise<void> {
  const params: CodexTurnSteerParams = {
    threadId: deps.threadId,
    turnId: deps.turnId,
    input: [{ type: 'text', text: CODEX_CLOSEOUT_WARNING_TEXT }],
  }
  // Defensive: the locked predicate must accept the params we
  // submit. A regression that drops `turn/steer` from the locked
  // method set would be caught here.
  if (
    !isCodexTurnSteerRequest({
      jsonrpc: '2.0',
      id: 0,
      method: 'turn/steer',
      params,
    })
  ) {
    const diagnostic: CodexDiagnostic = {
      severity: 'error',
      code: 'closeout-warning-malformed',
      message: redactCodexCredentialString(
        `closeout warning envelope failed the locked v2 subset check (turn=${deps.turnId})`,
      ),
    }
    deps.observer?.onDiagnostic?.(diagnostic)
    return
  }
  // The submitted input items must each pass the locked predicate;
  // this is a belt-and-braces guard against a regression in the
  // item-type predicates.
  if (!params.input.every(isCodexTurnInputItem)) {
    const diagnostic: CodexDiagnostic = {
      severity: 'error',
      code: 'closeout-warning-malformed',
      message: redactCodexCredentialString(
        `closeout warning input items failed the locked v2 subset check (turn=${deps.turnId})`,
      ),
    }
    deps.observer?.onDiagnostic?.(diagnostic)
    return
  }
  try {
    await deps.transport.send<CodexTurnSteerParams, unknown>({
      id: deps.nextRequestId(),
      method: 'turn/steer',
      params,
    })
  } catch (cause) {
    // A lost steer response is a diagnostic, not a retry. The
    // closeout warning is a best-effort affordance.
    const message = cause instanceof Error ? redactCodexCredentialString(cause.message) : 'unknown transport failure'
    const diagnostic: CodexDiagnostic = {
      severity: 'info',
      code: 'closeout-warning-lost',
      message: redactCodexCredentialString(
        `closeout warning steer was lost for turn=${deps.turnId}: ${message}; the runtime does not retry`,
      ),
    }
    deps.observer?.onDiagnostic?.(diagnostic)
  }
}

// ---------------------------------------------------------------------------
// Phase 2 — deadline interrupt with bounded confirmation
// ---------------------------------------------------------------------------

export type CodexDeadlineConfirmation =
  | { readonly status: 'confirmed' }
  | { readonly status: 'budget-exhausted' }
  | { readonly status: 'transport-error'; readonly message: string }

export interface CodexDeadlineInterruptHandle {
  readonly deadlineMs: number
  /** True after the deadline fired and the result was fixed. */
  readonly fired: boolean
  /** Await bounded confirmation of the interrupt. */
  awaitConfirmation(): Promise<CodexDeadlineConfirmation>
  /** Dispose the deadline timer. Idempotent. */
  dispose(): void
}

/**
 * Schedule the deadline interrupt. At `deadlineMs` the runtime
 * fixes the result as `deadline-exceeded`, sends `turn/interrupt`
 * to the exact active Turn, and awaits bounded confirmation. A late
 * completion does not reverse the fixed deadline result; the
 * confirmation outcome is reported for diagnostics only.
 *
 * The fix callback is invoked synchronously at the deadline so the
 * lifecycle can record the fixed deadline result. Confirmation is
 * awaited separately via `awaitConfirmation()`.
 */
export function scheduleDeadlineInterrupt(deps: {
  readonly transport: CodexCloseoutTransport
  readonly threadId: string
  readonly turnId: string
  readonly deadlineMs: number
  readonly clock: CodexClock
  readonly nextRequestId: () => number
  readonly observer?: CodexCloseoutObserver
}): CodexDeadlineInterruptHandle {
  const state: MutableDeadlineState = {
    disposed: false,
    fired: false,
    timer: null,
    confirmation: null,
    confirmationSubscribed: false,
    unsubscribe: null,
    pendingResolvers: [],
  }

  const resolvePending = () => {
    const resolvers = state.pendingResolvers
    state.pendingResolvers = []
    for (const resolve of resolvers) {
      resolve(state.confirmation ?? { status: 'budget-exhausted' })
    }
  }

  const ensureSubscribed = () => {
    if (state.confirmationSubscribed) return
    state.confirmationSubscribed = true
    const unsubscribe = deps.transport.subscribe((message) => {
      if (state.confirmation !== null) return
      if (!isCodexTurnCompletedEvent(message)) return
      const event = message as CodexTurnCompletedEvent
      if (event.threadId !== deps.threadId || event.turnId !== deps.turnId) return
      if (event.status !== 'interrupted') return
      state.confirmation = { status: 'confirmed' }
      resolvePending()
    })
    state.unsubscribe = unsubscribe
  }

  const fire = () => {
    if (state.disposed || state.fired) return
    state.fired = true
    ensureSubscribed()
    // Send the interrupt. The deadline result is fixed by the
    // lifecycle when this helper's caller marks the session as
    // resolved; here we only schedule the interrupt and the
    // confirmation waiter.
    void sendInterrupt(deps)
  }

  state.timer = deps.clock.setTimeout(fire, Math.max(0, deps.deadlineMs))

  const awaitConfirmation = (): Promise<CodexDeadlineConfirmation> => {
    if (state.confirmation !== null) return Promise.resolve(state.confirmation)
    if (!state.fired) {
      return new Promise<CodexDeadlineConfirmation>((resolve) => {
        const onFired = () => {
          if (state.confirmation !== null) {
            resolve(state.confirmation)
            return
          }
          resolve(awaitConfirmationSync())
        }
        deps.clock.setTimeout(onFired, Math.max(1, deps.deadlineMs))
      })
    }
    return awaitConfirmationSync()
  }

  const awaitConfirmationSync = (): Promise<CodexDeadlineConfirmation> => {
    ensureSubscribed()
    if (state.confirmation !== null) return Promise.resolve(state.confirmation)
    return new Promise<CodexDeadlineConfirmation>((resolve) => {
      if (state.confirmation !== null) {
        resolve(state.confirmation)
        return
      }
      const budgetTimer = deps.clock.setTimeout(() => {
        if (state.confirmation !== null) return
        state.confirmation = { status: 'budget-exhausted' }
        resolvePending()
        resolve(state.confirmation)
      }, CODEX_INTERRUPT_CONFIRMATION_BUDGET_MS)
      state.pendingResolvers.push((value) => {
        deps.clock.clearTimeout(budgetTimer)
        resolve(value)
      })
    })
  }

  return Object.freeze({
    deadlineMs: deps.deadlineMs,
    get fired() {
      return state.fired
    },
    awaitConfirmation,
    dispose() {
      if (state.disposed) return
      state.disposed = true
      if (state.timer !== null) deps.clock.clearTimeout(state.timer)
      state.unsubscribe?.()
    },
  })
}

interface MutableDeadlineState {
  disposed: boolean
  fired: boolean
  timer: unknown
  confirmation: CodexDeadlineConfirmation | null
  confirmationSubscribed: boolean
  unsubscribe: (() => void) | null
  pendingResolvers: Array<(value: CodexDeadlineConfirmation) => void>
}

async function sendInterrupt(deps: {
  readonly transport: CodexCloseoutTransport
  readonly threadId: string
  readonly turnId: string
  readonly nextRequestId: () => number
}): Promise<void> {
  try {
    await deps.transport.send<unknown, { accepted: boolean }>({
      id: deps.nextRequestId(),
      method: 'turn/interrupt',
      params: { threadId: deps.threadId, turnId: deps.turnId },
    })
  } catch {
    /* best-effort; the deadline result is fixed before this fires */
  }
}

// ---------------------------------------------------------------------------
// Permission / user-input rejection state machine
// ---------------------------------------------------------------------------

export interface CodexPermissionRejectionHandle {
  /**
   * Observe a server-initiated request. Returns:
   *   - `'active-turn'` when the request addressed the active Turn
   *     and the active Turn was interrupted;
   *   - `'different-turn'` when the request addressed a different
   *     Turn (denied but the active Turn was not interrupted);
   *   - `null` when the message is not a server-initiated request.
   */
  observeServerRequest(message: unknown): 'active-turn' | 'different-turn' | null
  /**
   * Observe the matching terminal `turn/completed` event. Returns
   * the `permission-required` result if a denial is in flight for
   * the exact active Turn; `null` otherwise.
   */
  observeTurnCompleted(event: unknown): CodexResult<CodexTurnResult> | null
  /**
   * Await bounded confirmation of the interrupt. Resolves with the
   * confirmation outcome once either the matching terminal event
   * arrives or the budget elapses. Idempotent.
   */
  awaitConfirmation(): Promise<CodexDeadlineConfirmation>
  /**
   * True when a denial is in flight and bounded confirmation is
   * pending (neither the matching terminal event nor the bounded
   * budget has resolved yet).
   */
  readonly pending: boolean
  /**
   * Dispose timers and subscriptions. Idempotent. Calling
   * `observe*` after dispose is a no-op.
   */
  dispose(): void
}

/**
 * Create the permission / user-input rejection state machine. The
 * state machine is bound to a single active Turn.
 *
 * Behaviour:
 *
 *   - Server-initiated requests addressed to the active Turn are
 *     denied with the protocol-defined denial response; the exact
 *     active Turn is interrupted.
 *   - Server-initiated requests addressed to a different Turn are
 *     denied but the active Turn is NOT interrupted — we never
 *     answer a different turn's request from this session.
 *   - When the matching `turn/completed` (status `interrupted`) for
 *     the exact active Turn arrives within the bounded
 *     confirmation budget, `observeTurnCompleted` returns
 *     `permission-required` and the lifecycle uses that result.
 *   - When the bounded confirmation budget elapses without a
 *     matching terminal event, the rejection is
 *     `interruption-unconfirmed` — `observeTimeout` returns an
 *     `unknown` result; the AgentSession binding is unchanged.
 *
 * The state machine never creates a Workflow Approval Point — there
 * is no transient approval state. The denial is final the moment
 * the protocol-defined response is written; whether the result is
 * `permission-required` or `unknown` is determined solely by the
 * bounded confirmation discipline above.
 */
export function createPermissionRejection(deps: {
  readonly transport: CodexCloseoutTransport
  readonly threadId: string
  readonly turnId: string
  readonly clock: CodexClock
  readonly confirmationBudgetMs?: number
  readonly nextRequestId: () => number
  readonly observer?: CodexCloseoutObserver
  readonly unknownItems?: readonly CodexDiagnostic[]
  /**
   * Called once when the bounded confirmation budget elapses
   * without a matching terminal event. The handler runs after
   * `state.pending` is cleared. The lifecycle uses this hook to
   * surface `unknown` / `interruption-unconfirmed` without
   * waiting for a terminal event that may never arrive.
   */
  readonly onUnconfirmed?: () => void
}): CodexPermissionRejectionHandle {
  const budgetMs =
    deps.confirmationBudgetMs !== undefined &&
    Number.isFinite(deps.confirmationBudgetMs) &&
    deps.confirmationBudgetMs >= 0
      ? Math.floor(deps.confirmationBudgetMs)
      : CODEX_INTERRUPT_CONFIRMATION_BUDGET_MS
  const state: MutablePermissionState = {
    pending: false,
    disposed: false,
    confirmed: false,
    confirmation: null,
    confirmationSubscribed: false,
    confirmationTimer: null,
    pendingRequest: null,
    unsubscribe: null,
    pendingResolvers: [],
  }

  const resolvePending = () => {
    const resolvers = state.pendingResolvers
    state.pendingResolvers = []
    for (const resolve of resolvers) {
      resolve(state.confirmation ?? { status: 'budget-exhausted' })
    }
  }

  const ensureSubscribed = () => {
    if (state.confirmationSubscribed) return
    state.confirmationSubscribed = true
    const unsubscribe = deps.transport.subscribe((message) => {
      if (!state.pending || state.confirmed) return
      if (!isCodexTurnCompletedEvent(message)) return
      const event = message as CodexTurnCompletedEvent
      if (event.threadId !== deps.threadId || event.turnId !== deps.turnId) return
      if (event.status !== 'interrupted') return
      state.confirmed = true
      state.confirmation = { status: 'confirmed' }
      // The denial is now confirmed; subsequent events for the
      // active Turn no longer retroactively re-trigger this hook.
      state.pending = false
      resolvePending()
    })
    state.unsubscribe = unsubscribe
  }

  const startConfirmation = () => {
    ensureSubscribed()
    state.pending = true
    // Start the bounded confirmation budget immediately so an
    // observed denial has a finite wait window. If no matching
    // terminal event arrives within the budget, the denial is
    // `interruption-unconfirmed` — the AgentSession binding is
    // unchanged. The lifecycle consults the budget state via
    // `observeTurnCompleted`; the helper `awaitConfirmation`
    // remains for diagnostic observability.
    if (state.confirmationTimer !== null) return
    state.confirmationTimer = deps.clock.setTimeout(() => {
      if (state.confirmation !== null) return
      state.confirmation = { status: 'budget-exhausted' }
      // Unconfirmed denial: the state machine is no longer
      // pending. Subsequent terminal events for the active Turn
      // do NOT retroactively resolve to `permission-required`;
      // the AgentSession binding is unchanged.
      state.pending = false
      resolvePending()
      deps.onUnconfirmed?.()
    }, budgetMs)
  }

  const awaitConfirmation = (): Promise<CodexDeadlineConfirmation> => {
    if (state.confirmation !== null) return Promise.resolve(state.confirmation)
    if (!state.pending) return Promise.resolve<CodexDeadlineConfirmation>({ status: 'budget-exhausted' })
    ensureSubscribed()
    return new Promise<CodexDeadlineConfirmation>((resolve) => {
      if (state.confirmation !== null) {
        resolve(state.confirmation)
        return
      }
      state.pendingResolvers.push((value) => {
        resolve(value)
      })
    })
  }

  return Object.freeze({
    observeServerRequest(message: unknown): 'active-turn' | 'different-turn' | null {
      if (state.disposed) return null
      if (!isCodexServerRequest(message)) return null
      const request = message as CodexJsonRpcServerRequest<CodexServerRequestParams>
      const params = request.params
      const requestTurnId =
        params && typeof params === 'object' && typeof (params as { turnId?: unknown }).turnId === 'string'
          ? (params as { turnId: string }).turnId
          : null
      // A server-initiated request that does NOT carry the exact
      // active Turn ID is denied — we never answer a different
      // turn's request from this session.
      if (requestTurnId !== null && requestTurnId !== deps.turnId) {
        deps.transport.denyServerRequest(request.id, 'request addressed to a different active Turn')
        return 'different-turn'
      }
      // Headless execution fails closed: deny with the protocol-
      // defined denial, then interrupt the exact active Turn.
      deps.transport.denyServerRequest(
        request.id,
        'Codex headless runtime denies approval / permission / user-input requests',
      )
      state.pendingRequest = { id: request.id, method: request.method }
      deps.observer?.onDiagnostic?.({
        severity: 'warning',
        code: 'server-request-denied',
        message: redactCodexCredentialString(
          `denied Codex server-initiated request ${request.method} for turn ${deps.turnId}; interrupting the exact active Turn`,
        ),
      })
      void sendInterruptForPermissionRejection(deps)
      startConfirmation()
      return 'active-turn'
    },
    observeTurnCompleted(event: unknown): CodexResult<CodexTurnResult> | null {
      if (state.disposed) return null
      if (!state.pending) return null
      if (!isCodexTurnCompletedEvent(event)) return null
      const typed = event as CodexTurnCompletedEvent
      if (typed.threadId !== deps.threadId || typed.turnId !== deps.turnId) return null
      if (typed.status !== 'interrupted') return null
      state.confirmed = true
      state.confirmation = { status: 'confirmed' }
      // The denial is now confirmed; subsequent terminal events
      // for the active Turn do NOT retroactively resolve to
      // `permission-required` again.
      state.pending = false
      const error = normalizePermissionRequiredCodex([
        ...(deps.unknownItems ?? []),
        ...(state.pendingRequest
          ? [
              {
                severity: 'warning' as const,
                code: 'server-request-denied',
                message: redactCodexCredentialString(
                  `Codex server-initiated request ${state.pendingRequest.method} was denied and the active Turn was interrupted; permission-required is the terminal result`,
                ),
              },
            ]
          : []),
      ])
      const result: CodexResult<CodexTurnResult> = { ok: false, error, diagnostics: error.diagnostics }
      return result
    },
    awaitConfirmation(): Promise<CodexDeadlineConfirmation> {
      return awaitConfirmation()
    },
    get pending() {
      return state.pending && !state.confirmed
    },
    dispose() {
      if (state.disposed) return
      state.disposed = true
      state.pending = false
      if (state.confirmationTimer !== null) deps.clock.clearTimeout(state.confirmationTimer)
      state.unsubscribe?.()
    },
  })
}

interface MutablePermissionState {
  pending: boolean
  disposed: boolean
  confirmed: boolean
  confirmation: CodexDeadlineConfirmation | null
  confirmationSubscribed: boolean
  confirmationTimer: unknown
  pendingRequest: { readonly id: number | string; readonly method: string } | null
  unsubscribe: (() => void) | null
  pendingResolvers: Array<(value: CodexDeadlineConfirmation) => void>
}

async function sendInterruptForPermissionRejection(deps: {
  readonly transport: CodexCloseoutTransport
  readonly threadId: string
  readonly turnId: string
  readonly nextRequestId: () => number
}): Promise<void> {
  try {
    await deps.transport.send<unknown, { accepted: boolean }>({
      id: deps.nextRequestId(),
      method: 'turn/interrupt',
      params: { threadId: deps.threadId, turnId: deps.turnId },
    })
  } catch {
    /* best-effort; unconfirmed denial stays unknown */
  }
}

/**
 * Build the unconfirmed-denial `unknown` /
 * `interruption-unconfirmed` result. The AgentSession binding is
 * unchanged because `unknown` means "effect is unconfirmed" and the
 * caller treats it as a no-op against the durable state.
 */
export function buildPermissionRejectionUnconfirmed(args: {
  readonly threadId: string
  readonly turnId: string
}): CodexResult<CodexTurnResult> {
  const error = normalizeUnknownCodex(
    `Codex server-initiated request denial was not confirmed within the bounded budget for thread=${args.threadId} turn=${args.turnId}; AgentSession binding is unchanged`,
    [
      {
        severity: 'error',
        code: 'interruption-unconfirmed',
        message: redactCodexCredentialString(
          `Codex server-initiated request denial was not confirmed for thread=${args.threadId} turn=${args.turnId}; the AgentSession binding is unchanged`,
        ),
      },
    ],
  )
  return { ok: false, error, diagnostics: error.diagnostics }
}
