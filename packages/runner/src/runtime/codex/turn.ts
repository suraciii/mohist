/**
 * Codex Turn lifecycle (`turn/start`, event routing, completion).
 *
 * The Turn ID is volatile per-generation Runtime correlation: it
 * lives in module memory for the lifetime of one app-server
 * generation, never reaches persistence, and is discarded when the
 * child exits or the Runner is fenced. The Thread ID is the
 * persistent identity; this file carries it for every call but
 * never publishes it as Mohist identity.
 *
 * Rules enforced here:
 *
 *   - `turn/start` runs only after the binding + Input identity are
 *     durable. The Mohist SessionInput ID is carried on the request
 *     as `clientUserMessageId` for correlation only — it is never a
 *     provider idempotency key. The same Input is never resubmitted.
 *   - Item, status, and `turn/completed` events are routed by exact
 *     Thread + Turn IDs. Item events project text, reasoning
 *     summaries, command / tool activity, file changes, usage, and
 *     diagnostics through the existing Mohist transcript and
 *     activity boundary. Unknown item types are diagnostics only.
 *   - `turn/completed` for the exact active Thread + Turn IDs is
 *     the sole provider completion authority. `completed` is
 *     successful Runtime completion, `failed` is normalized
 *     `turn-failed`, and `interrupted` confirms interruption unless
 *     a previously fixed deadline or permission result owns the
 *     outcome. Thread status, item completion, silence, EOF, and a
 *     successful interrupt response cannot complete an AgentJob.
 *   - A lost `turn/start` response after submission may have
 *     occurred preserves `unknown`; the same Input is never
 *     resubmitted even with the same `clientUserMessageId`.
 *
 * The runtime owns the app-server DTO construction. Callers pass
 * only Mohist-owned shapes (`CodexTurnRequest`, `CodexTurnResult`,
 * `CodexTurnFacts`).
 */

import {
  isCodexTurnStartRequest,
  isCodexTurnInputItem,
  isCodexTurnCompletedEvent,
  isCodexThreadStatusEvent,
  isCodexItemEvent,
  isCodexServerRequest,
  type CodexTurnStartParams,
  type CodexTurnStartResult,
  type CodexTurnInputItem,
  type CodexTurnCompletedEvent,
  type CodexTurnCompletedStatus,
  type CodexItemEvent,
  type CodexThreadStatusEvent,
  type CodexJsonRpcServerRequest,
  type CodexServerRequestParams,
} from './protocol-types.js'
import {
  normalizeInterruptedCodex,
  normalizeTurnFailedCodex,
  normalizeDeadlineExceededCodex,
  normalizeUnknownCodex,
} from './errors.js'
import { redactCodexCredentialString } from './credential.js'
import { normalizeCodexNotification, staleItemDiagnostic } from './turn-events.js'
import {
  buildPermissionRejectionUnconfirmed,
  createPermissionRejection,
  scheduleCloseoutWarning,
  scheduleDeadlineInterrupt,
  type CodexCloseoutTransport,
  type CodexCloseoutWarningHandle,
  type CodexDeadlineInterruptHandle,
  type CodexPermissionRejectionHandle,
} from './closeout.js'
import type {
  CodexClock,
  CodexDiagnostic,
  CodexFilePart,
  CodexResult,
  CodexTurnFacts,
  CodexTurnResult,
} from './types.js'
import { mapCodexCanonicalReasoningEffort, type CodexResolvedTurnConfiguration } from './model-catalog.js'

// ---------------------------------------------------------------------------
// Transport
// ---------------------------------------------------------------------------

/**
 * Narrow app-server handle surface used by the Turn lifecycle. The
 * transport refuses methods outside the locked v2 subset; the
 * lifecycle never sends them. The transport is also responsible for
 * denying server-initiated requests — the runtime never
 * auto-approves.
 */
export interface CodexTurnTransport {
  send<P, R>(request: { readonly method: string; readonly params?: P; readonly id: number }): Promise<R>
  denyServerRequest(id: number | string, reason: string): void
  /**
   * Subscribe to JSON-RPC notifications, server-initiated requests,
   * and unmatched responses. The Turn lifecycle filters by exact
   * Thread + Turn IDs before acknowledging any event.
   */
  subscribe(listener: (message: unknown) => void): () => void
  hasExited?(): boolean
}

export interface CodexTurnEventObserver {
  /**
   * Called after a Thread is created or resumed and before `turn/start`.
   * The caller must persist the complete Mohist binding and SessionInput
   * identity before this callback resolves.
   */
  onSessionReady?(session: { readonly runtimeSessionId: string; readonly workDir: string }): void | Promise<void>
  /**
   * Projected Mohist event. `turnId` is the volatile Turn ID for the
   * active Turn only — callers MUST NOT persist it. `payload` is
   * already masked through the credential redaction rules so callers
   * never need to walk it again before forwarding it to the
   * transcript or activity boundary.
   */
  onEvent?(event: CodexRuntimeTurnEvent): void
  /** Diagnostic-only items that did not project into a Mohist event. */
  onDiagnostic?(diagnostic: CodexDiagnostic): void
}

export interface CodexRuntimeTurnEvent {
  readonly type: string
  readonly runtimeSessionId: string
  readonly workDir: string
  readonly turnId: string
  readonly payload: Record<string, unknown>
}

// ---------------------------------------------------------------------------
// Submit
// ---------------------------------------------------------------------------

export interface CodexTurnStartSubmission {
  readonly threadId: string
  readonly workDir: string
  /** Assembled Mohist user input — text + optional file parts. */
  readonly prompt: string
  readonly fileParts: readonly CodexFilePart[] | null
  /**
   * Mohist SessionInput ID carried on `clientUserMessageId` for
   * correlation only. Never treated as a provider idempotency key.
   */
  readonly clientUserMessageId: string
  /**
   * Resolved model / canonical reasoning effort. Already validated
   * against the catalog before this seam; the lifecycle submits the
   * exact canonical values, mapping to the native spelling here.
   */
  readonly resolved: CodexResolvedTurnConfiguration
}

export interface CodexTurnStartOutcome {
  readonly turnId: string
  readonly threadId: string
  readonly status: string
}

/**
 * Submit `turn/start`. Returns the volatile Turn ID on success.
 * Callers MUST persist `turnId` only in module memory and treat it
 * as per-generation correlation. A lost `turn/start` response
 * preserves `unknown`; the runtime never retries the same Input.
 */
export async function submitTurnStart(
  transport: CodexTurnTransport,
  submission: CodexTurnStartSubmission,
  nextRequestId: number,
): Promise<CodexResult<CodexTurnStartOutcome>> {
  const inputItems = buildTurnInputItems(submission.prompt, submission.fileParts)
  const nativeEffort = mapCodexCanonicalReasoningEffort(submission.resolved.reasoningEffort)
  const params: CodexTurnStartParams = {
    threadId: submission.threadId,
    input: inputItems,
    clientUserMessageId: submission.clientUserMessageId,
    ...(submission.resolved.model !== null ? { model: submission.resolved.model } : {}),
    ...(nativeEffort !== null ? { effort: nativeEffort, reasoningEffort: nativeEffort } : {}),
  }
  const id = assertPositiveRequestId(nextRequestId)
  if (!isCodexTurnStartRequest({ jsonrpc: '2.0', id, method: 'turn/start', params })) {
    // Defensive: the params we built must round-trip through the
    // locked predicate; a regression here is a protocol-failure
    // boundary, not a recoverable runtime failure.
    const error = normalizeTurnFailedCodex('turn/start params failed the locked v2 envelope check')
    return { ok: false, error, diagnostics: error.diagnostics }
  }
  let response: unknown
  try {
    response = await transport.send<CodexTurnStartParams, unknown>({
      id,
      method: 'turn/start',
      params,
    })
  } catch (cause) {
    const message = cause instanceof Error ? redactCodexCredentialString(cause.message) : 'unknown transport failure'
    const lost = buildLostTurnStartUnknown({
      threadId: submission.threadId,
      clientUserMessageId: submission.clientUserMessageId,
      workDir: submission.workDir,
      message: `turn/start transport failed before a response was observed: ${message}`,
    })
    return { ok: false, error: lost.error, diagnostics: lost.diagnostics }
  }
  if (transport.hasExited?.()) {
    const lost = buildLostTurnStartUnknown({
      threadId: submission.threadId,
      clientUserMessageId: submission.clientUserMessageId,
      workDir: submission.workDir,
      message: 'turn/start child exited before response was observed',
    })
    return { ok: false, error: lost.error, diagnostics: lost.diagnostics }
  }
  const result = turnStartResult(response, submission.threadId)
  if (result === null) {
    const error = normalizeTurnFailedCodex('turn/start response did not match the locked v2 subset')
    return { ok: false, error, diagnostics: error.diagnostics }
  }
  if (result.threadId !== submission.threadId) {
    const error = normalizeTurnFailedCodex(
      `turn/start returned threadId=${result.threadId}; expected the bound ${submission.threadId}`,
    )
    return { ok: false, error, diagnostics: error.diagnostics }
  }
  return {
    ok: true,
    value: { turnId: result.turnId, threadId: result.threadId, status: result.status },
    diagnostics: [],
  }
}

function buildTurnInputItems(prompt: string, fileParts: readonly CodexFilePart[] | null): CodexTurnInputItem[] {
  const items: CodexTurnInputItem[] = [{ type: 'text', text: prompt, text_elements: [] }]
  if (fileParts) {
    for (const part of fileParts) {
      items.push({ type: 'image', mime: part.mime, url: part.url, filename: part.filename })
    }
  }
  // The built array is shape-validated below by the locked predicate.
  if (!items.every(isCodexTurnInputItem)) {
    throw new Error('turn/start input items failed the locked v2 envelope check')
  }
  return items
}

function turnStartResult(response: unknown, expectedThreadId: string): CodexTurnStartResult | null {
  if (!response || typeof response !== 'object') return null
  const candidate = response as { result?: unknown }
  const result = 'result' in candidate ? candidate.result : response
  if (!result || typeof result !== 'object') return null
  const view = result as { turnId?: unknown; threadId?: unknown; status?: unknown; turn?: unknown }
  const turn = view.turn && typeof view.turn === 'object' ? (view.turn as { id?: unknown; status?: unknown }) : null
  const turnId = typeof view.turnId === 'string' ? view.turnId : turn && typeof turn.id === 'string' ? turn.id : null
  const threadId = typeof view.threadId === 'string' ? view.threadId : expectedThreadId
  const status =
    typeof view.status === 'string' ? view.status : turn && typeof turn.status === 'string' ? turn.status : null
  return turnId && status ? { turnId, threadId, status } : null
}

// ---------------------------------------------------------------------------
// Drive Turn to terminal completion
// ---------------------------------------------------------------------------

export interface CodexTurnCompletionConfig {
  readonly runtimeSessionId: string
  readonly workDir: string
  readonly turnId: string | null
  readonly threadId: string
  readonly deadlineMs: number | null
  readonly clock?: CodexClock
}

export interface CodexTurnCompletionOptions extends CodexTurnCompletionConfig {
  readonly transport: CodexTurnTransport
  readonly observer?: CodexTurnEventObserver
  readonly fixedDeadlineResult?: CodexResult<CodexTurnResult> | null
  readonly fixedPermissionResult?: CodexResult<CodexTurnResult> | null
  readonly nextRequestId: () => number
  readonly onTurnIdDiscovered?: (turnId: string) => void
}

/**
 * Drive the Turn to terminal completion through the matching
 * `turn/completed` event for the exact Thread + Turn IDs. Item,
 * status, and `turn/completed` events are routed by exact ID match.
 *
 * Returns:
 *   - `ok: true` with `CodexTurnResult.facts` when `turn/completed`
 *     carries `status: 'completed'`.
 *   - `ok: false` with `kind: 'turn-failed'` for `status: 'failed'`.
 *   - `ok: false` with `kind: 'interrupted'` for `status: 'interrupted'`
 *     unless a previously fixed deadline or permission result owns
 *     the outcome.
 *
 * The runtime never returns success or failure from a non-terminal
 * signal — Thread status events, item completion, silence, EOF, or
 * a successful `turn/interrupt` response do not complete an AgentJob.
 */
export async function driveTurnToCompletion(
  options: CodexTurnCompletionOptions,
): Promise<CodexResult<CodexTurnResult>> {
  const diagnostics: CodexDiagnostic[] = []
  const closeoutClock = options.clock ?? defaultTurnClock
  const closeoutTransport: CodexCloseoutTransport = options.transport
  const session = createTurnSession(options, diagnostics)
  const unsubscribe = options.transport.subscribe((message) => {
    routeTurnMessage(message, session, options.transport)
  })
  // Phase 1 — closeout warning. Fires once at
  // `min(deadlineMs - CODEX_CLOSEOUT_WARNING_LEAD_MS, 0)`. The
  // warning text is task-independent and locked.
  const warningHandle: CodexCloseoutWarningHandle | null =
    options.deadlineMs !== null
      ? scheduleCloseoutWarning({
          transport: closeoutTransport,
          threadId: options.threadId,
          turnId: options.turnId ?? '',
          deadlineMs: options.deadlineMs,
          clock: closeoutClock,
          nextRequestId: options.nextRequestId,
          observer: session,
        })
      : null
  // Phase 2 — deadline interrupt. Fixes the result as
  // `deadline-exceeded` at the deadline and sends `turn/interrupt`.
  const deadlineHandle: CodexDeadlineInterruptHandle | null =
    options.deadlineMs !== null
      ? scheduleDeadlineInterrupt({
          transport: closeoutTransport,
          threadId: options.threadId,
          turnId: options.turnId ?? '',
          deadlineMs: options.deadlineMs,
          clock: closeoutClock,
          nextRequestId: options.nextRequestId,
          observer: session,
        })
      : null
  // Legacy deadline scheduler kept for back-compat with the
  // pre-closeout hook; it no longer fixes the result itself (the
  // new deadline interrupt does) but preserves the early-fix
  // contract for the live lifecycle: as soon as the deadline
  // fires, the session is resolved with `deadline-exceeded`.
  const legacyHandle =
    options.deadlineMs !== null ? legacyScheduleDeadlineCloseout(options, session, deadlineHandle) : null
  // Permission / user-input rejection state machine. Headless
  // execution fails closed. The `onUnconfirmed` callback fires
  // when the budget expires without a matching terminal event;
  // the lifecycle surfaces `unknown` /
  // `interruption-unconfirmed` and leaves the AgentSession
  // binding unchanged.
  const permissionRejection: CodexPermissionRejectionHandle = createPermissionRejection({
    transport: closeoutTransport,
    threadId: options.threadId,
    turnId: options.turnId ?? '',
    clock: closeoutClock,
    nextRequestId: options.nextRequestId,
    observer: session,
    onUnconfirmed: () => {
      const unconfirmed = buildPermissionRejectionUnconfirmed({
        threadId: options.threadId,
        turnId: options.turnId ?? '',
      })
      session.fixedUnknown = unconfirmed
      session.resolve(unconfirmed)
    },
  })
  session.permissionRejection = permissionRejection
  try {
    return await session.settled.promise
  } finally {
    warningHandle?.dispose()
    deadlineHandle?.dispose()
    legacyHandle?.dispose?.()
    permissionRejection.dispose()
    unsubscribe()
  }
}

interface TurnSession {
  readonly factsState: MutableTurnFacts
  expectedTurnId: string | null
  readonly expectedThreadId: string
  readonly onTurnIdDiscovered?: (turnId: string) => void
  sawAgentMessageDelta: boolean
  readonly settled: DeferredSettled
  readonly textBuffer: TurnTextBuffer
  readonly unknownItems: CodexDiagnostic[]
  readonly observer?: CodexTurnEventObserver
  fixedDeadline: CodexResult<CodexTurnResult> | null
  fixedPermission: CodexResult<CodexTurnResult> | null
  fixedUnknown: CodexResult<CodexTurnResult> | null
  /** Permission / user-input rejection state machine. */
  permissionRejection?: CodexPermissionRejectionHandle
  resolve(value: CodexResult<CodexTurnResult>): void
  observeDiagnostic(diagnostic: CodexDiagnostic): void
  sendInterrupt(transport: CodexTurnTransport): Promise<void>
  /**
   * Snapshot the mutable session state into the immutable
   * {@link CodexTurnFacts} that crosses the module boundary.
   */
  snapshotFacts(): CodexTurnFacts
  /**
   * The closeout helpers observe diagnostics through this hook so
   * they can be surfaced through the same channel the lifecycle
   * already records.
   */
  onDiagnostic?(diagnostic: CodexDiagnostic): void
}

interface MutableTurnFacts {
  finalAssistantText: string | null
  runtimeSessionId: string
  workDir: string
}

interface DeferredSettled {
  readonly promise: Promise<CodexResult<CodexTurnResult>>
  resolve(value: CodexResult<CodexTurnResult>): void
}

function createTurnSession(options: CodexTurnCompletionOptions, diagnostics: CodexDiagnostic[]): TurnSession {
  const textBuffer: TurnTextBuffer = { finalText: '', agentMessageText: '' }
  let resolveFn: ((result: CodexResult<CodexTurnResult>) => void) | null = null
  const promise = new Promise<CodexResult<CodexTurnResult>>((resolve) => {
    resolveFn = resolve
  })
  let resolved = false
  const session: TurnSession = {
    factsState: {
      finalAssistantText: null,
      runtimeSessionId: options.runtimeSessionId,
      workDir: options.workDir,
    },
    expectedTurnId: options.turnId,
    expectedThreadId: options.threadId,
    onTurnIdDiscovered: options.onTurnIdDiscovered,
    sawAgentMessageDelta: false,
    settled: {
      promise,
      resolve(value) {
        if (resolved) return
        resolved = true
        resolveFn?.(value)
      },
    },
    textBuffer,
    unknownItems: [],
    observer: options.observer,
    fixedDeadline: options.fixedDeadlineResult ?? null,
    fixedPermission: options.fixedPermissionResult ?? null,
    fixedUnknown: null,
    resolve(value) {
      session.settled.resolve(value)
    },
    observeDiagnostic(diagnostic) {
      diagnostics.push(diagnostic)
      options.observer?.onDiagnostic?.(diagnostic)
    },
    onDiagnostic(diagnostic) {
      diagnostics.push(diagnostic)
      options.observer?.onDiagnostic?.(diagnostic)
    },
    async sendInterrupt(transport) {
      await sendTurnInterrupt(transport, options.threadId, options.turnId ?? '', options.nextRequestId)
    },
    snapshotFacts() {
      return {
        finalAssistantText: session.factsState.finalAssistantText,
        runtimeSessionId: session.factsState.runtimeSessionId,
        workDir: session.factsState.workDir,
      }
    },
  }
  return session
}

interface TurnTextBuffer {
  finalText: string
  agentMessageText: string
}

interface DeadlineHandle {
  dispose(): void
}

/**
 * Legacy deadline closeout scheduler kept for back-compat with the
 * pre-closeout hook. The actual deadline interrupt (including the
 * bounded confirmation of the interrupt RPC) now lives in
 * `./closeout.ts`; this helper remains to drive the early-fix
 * behaviour so the session is resolved with `deadline-exceeded`
 * the instant the deadline fires, regardless of whether the
 * interrupt RPC has been confirmed.
 */
function legacyScheduleDeadlineCloseout(
  options: CodexTurnCompletionOptions,
  session: TurnSession,
  interrupt: CodexDeadlineInterruptHandle | null,
): DeadlineHandle {
  const deadlineMs = options.deadlineMs as number
  const clock = options.clock ?? defaultTurnClock
  const fixedAtDeadline = () => {
    if (session.settled.promise === undefined) return
    const error = normalizeDeadlineExceededCodex(deadlineMs, [...session.unknownItems])
    const result: CodexResult<CodexTurnResult> = {
      ok: false,
      error,
      diagnostics: error.diagnostics,
    }
    session.fixedDeadline = result
    session.resolve(result)
    // The interrupt is best-effort; failure is diagnostic but does
    // not block the fixed deadline result. The bounded confirmation
    // observable on `interrupt` is reported through the deadline
    // closeout helper for diagnostics only.
    if (interrupt) {
      void interrupt.awaitConfirmation()
    }
  }
  const timer = clock.setTimeout(fixedAtDeadline, deadlineMs)
  return {
    dispose() {
      clock.clearTimeout(timer)
    },
  }
}

async function sendTurnInterrupt(
  transport: CodexTurnTransport,
  threadId: string,
  turnId: string,
  nextRequestId: () => number,
): Promise<void> {
  try {
    await transport.send<unknown, { accepted: boolean }>({
      id: nextRequestId(),
      method: 'turn/interrupt',
      params: { threadId, turnId },
    })
  } catch {
    /* best-effort; the runtime never reverses the fixed result */
  }
}

function routeTurnMessage(message: unknown, session: TurnSession, transport: CodexTurnTransport): void {
  if (!message || typeof message !== 'object') return
  // Server-initiated requests: deny with the protocol-defined
  // response, never auto-approve. The denial itself is not terminal
  // — the runtime only completes on the matching turn/completed.
  if (isCodexServerRequest(message)) {
    handleServerRequest(message as CodexJsonRpcServerRequest<CodexServerRequestParams>, session, transport)
    return
  }
  const normalized = normalizeCodexNotification(message)
  if (normalized !== null) message = normalized
  const possibleCompaction = message as { type?: unknown; threadId?: unknown; turnId?: unknown }
  if (
    session.expectedTurnId === null &&
    possibleCompaction.type === 'contextCompaction' &&
    possibleCompaction.threadId === session.expectedThreadId &&
    typeof possibleCompaction.turnId === 'string'
  ) {
    session.expectedTurnId = possibleCompaction.turnId
    session.onTurnIdDiscovered?.(possibleCompaction.turnId)
  }
  const envelope = message as { method?: unknown; type?: unknown; params?: unknown }
  // JSON-RPC notifications carry their discriminator on `method`;
  // Codex item / terminal events carry it on `type`. Both forms
  // reach this seam through the line-framed JSON-RPC consumer.
  if (typeof envelope.method === 'string') {
    if (envelope.method === 'protocol-failure') {
      const params = envelope.params && typeof envelope.params === 'object' ? envelope.params : null
      const detail =
        params && typeof (params as { message?: unknown }).message === 'string'
          ? (params as { message: string }).message
          : 'Codex app-server protocol failure ended the active Turn before completion'
      const error = normalizeUnknownCodex(redactCodexCredentialString(detail))
      session.fixedUnknown = { ok: false, error, diagnostics: error.diagnostics }
      session.resolve(session.fixedUnknown)
      return
    }
    if (envelope.method === 'turn/completed') {
      handleTurnCompleted(message as unknown as CodexTurnCompletedEvent, session)
      return
    }
    if (envelope.method === 'thread/status') {
      handleThreadStatus(message as unknown as CodexThreadStatusEvent, session)
      return
    }
    if (envelope.method === 'protocol-failure') return
    session.observeDiagnostic({
      severity: 'info',
      code: 'unknown-protocol-message',
      message: redactCodexCredentialString(
        `ignored unknown Codex protocol message ${envelope.method} for turn ${session.expectedTurnId}`,
      ),
    })
    return
  }
  if (typeof envelope.type === 'string') {
    if (envelope.type === 'turn/completed') {
      handleTurnCompleted(message as unknown as CodexTurnCompletedEvent, session)
      return
    }
    if (envelope.type === 'thread/status') {
      handleThreadStatus(message as unknown as CodexThreadStatusEvent, session)
      return
    }
    if (isCodexItemEvent(message)) {
      handleItem(message as unknown as CodexItemEvent, session)
      return
    }
    session.observeDiagnostic({
      severity: 'info',
      code: 'unknown-protocol-message',
      message: redactCodexCredentialString(
        `ignored unknown Codex protocol message ${envelope.type} for turn ${session.expectedTurnId}`,
      ),
    })
    return
  }
}

function handleServerRequest(
  request: CodexJsonRpcServerRequest<CodexServerRequestParams>,
  session: TurnSession,
  transport: CodexTurnTransport,
): void {
  const params = request.params
  const requestTurnId =
    params && typeof params === 'object' && typeof (params as { turnId?: unknown }).turnId === 'string'
      ? (params as { turnId: string }).turnId
      : null
  // A server-initiated request that does NOT carry the exact active
  // Turn ID is denied — we never answer a different turn's request
  // from this session.
  if (requestTurnId !== null && requestTurnId !== session.expectedTurnId) {
    transport.denyServerRequest(request.id, 'request addressed to a different active Turn')
    return
  }
  // Headless execution fails closed: deny, then attempt to interrupt
  // the exact active Turn. The permission-rejection state machine
  // owns the bounded confirmation budget. `permission-required` is
  // returned ONLY after the matching turn/completed event confirms
  // the Turn has reached a terminal state within the budget;
  // unconfirmed denial stays `unknown` /
  // `interruption-unconfirmed` and never creates a Workflow
  // Approval Point.
  if (session.permissionRejection) {
    const outcome = session.permissionRejection.observeServerRequest(request)
    if (outcome === 'active-turn') {
      // The state machine has already sent the protocol-defined
      // denial, recorded the diagnostic, and called
      // `turn/interrupt` on the exact active Turn. The terminal
      // event observer will resolve the session to
      // `permission-required` once the matching event arrives.
      return
    }
    if (outcome === 'different-turn') {
      return
    }
  }
  // Fallback path — the state machine was not wired (legacy
  // callers). Preserve the pre-existing behaviour so existing
  // tests that exercise this seam directly keep working.
  transport.denyServerRequest(request.id, 'Codex headless runtime denies approval / permission / user-input requests')
  session.observeDiagnostic({
    severity: 'warning',
    code: 'server-request-denied',
    message: redactCodexCredentialString(
      `denied Codex server-initiated request ${request.method} for turn ${session.expectedTurnId}; interrupting the exact active Turn`,
    ),
  })
  void session.sendInterrupt(transport)
}

function handleTurnCompleted(event: CodexTurnCompletedEvent, session: TurnSession): void {
  if (session.expectedTurnId === null) {
    session.observeDiagnostic({
      severity: 'info',
      code: 'turn-completed-before-turn-id',
      message: 'Ignored Codex turn/completed until the active Turn ID was discovered',
    })
    return
  }
  // Only the matching event for the exact active Thread + Turn IDs
  // may complete the Turn. Anything else is a stale terminal event
  // for an unrelated generation.
  if (!isCodexTurnCompletedEvent(event)) return
  if (event.threadId !== session.expectedThreadId || event.turnId !== session.expectedTurnId) {
    session.observeDiagnostic({
      severity: 'info',
      code: 'turn-completed-stale',
      message: redactCodexCredentialString(
        `ignored stale turn/completed for thread=${event.threadId} turn=${event.turnId}; expected thread=${session.expectedThreadId} turn=${session.expectedTurnId}`,
      ),
    })
    return
  }
  if (session.fixedDeadline && !session.fixedDeadline.ok) {
    // A previously fixed deadline result owns the outcome. The
    // matching interrupted event may arrive, but the Turn is
    // already resolved.
    session.resolve(session.fixedDeadline)
    return
  }
  if (session.fixedPermission && !session.fixedPermission.ok) {
    session.resolve(session.fixedPermission)
    return
  }
  if (session.fixedUnknown && !session.fixedUnknown.ok) {
    // A previously exhausted bounded confirmation budget owns
    // the outcome. The AgentSession binding is unchanged.
    session.resolve(session.fixedUnknown)
    return
  }
  // Permission-rejection state machine: a previously observed
  // server-initiated request denial owns the outcome. The state
  // machine returns `permission-required` when the matching
  // interrupted terminal event confirms the interrupt.
  if (session.permissionRejection) {
    const permissionResult = session.permissionRejection.observeTurnCompleted(event)
    if (permissionResult !== null) {
      session.fixedPermission = permissionResult
      session.resolve(permissionResult)
      return
    }
  }
  const result = translateCompletedStatus(event, session)
  session.resolve(result)
}

function translateCompletedStatus(event: CodexTurnCompletedEvent, session: TurnSession): CodexResult<CodexTurnResult> {
  const status: CodexTurnCompletedStatus = event.status
  if (status === 'completed') {
    session.factsState.finalAssistantText = reconciledFinalText(session.textBuffer)
    return {
      ok: true,
      value: { facts: session.snapshotFacts(), diagnostics: [] },
      diagnostics: [],
    }
  }
  if (status === 'failed') {
    const message = event.error?.message ?? 'Codex turn failed'
    const error = normalizeTurnFailedCodex({ message, code: event.error?.code })
    return { ok: false, error, diagnostics: error.diagnostics }
  }
  // status === 'interrupted'
  const error = normalizeInterruptedCodex([])
  return { ok: false, error, diagnostics: error.diagnostics }
}

function handleThreadStatus(event: CodexThreadStatusEvent, session: TurnSession): void {
  if (!isCodexThreadStatusEvent(event)) return
  // Thread status events are informational only — they are never
  // completion authority and never change the Turn outcome.
  if (event.threadId !== session.expectedThreadId) {
    session.observeDiagnostic({
      severity: 'info',
      code: 'thread-status-stale',
      message: redactCodexCredentialString(
        `ignored thread/status for thread=${event.threadId}; expected ${session.expectedThreadId}`,
      ),
    })
    return
  }
  if (event.turnId !== undefined && event.turnId !== session.expectedTurnId) {
    session.observeDiagnostic({
      severity: 'info',
      code: 'thread-status-stale',
      message: redactCodexCredentialString(
        `ignored thread/status for turn=${event.turnId}; expected ${session.expectedTurnId}`,
      ),
    })
    return
  }
  session.observeDiagnostic({
    severity: 'info',
    code: 'thread-status',
    message: redactCodexCredentialString(
      `Codex thread ${event.threadId} status=${event.status}${event.turnId ? ` (turn=${event.turnId})` : ''}`,
    ),
  })
}

function handleItem(event: CodexItemEvent, session: TurnSession): void {
  if (!isCodexItemEvent(event)) return
  const stale = staleItemDiagnostic(event, session.expectedThreadId, session.expectedTurnId)
  if (stale !== null) {
    session.observeDiagnostic(stale)
    return
  }
  if (event.type === 'agentMessage' && event.delta === true) session.sawAgentMessageDelta = true
  const projected = projectItemEvent(event, session)
  if (projected === null) {
    const unknown = event as { type?: unknown }
    const diagnostic: CodexDiagnostic = {
      severity: 'info',
      code: 'unknown-codex-item',
      message: redactCodexCredentialString(
        `unknown Codex item type ${typeof unknown.type === 'string' ? unknown.type : 'unknown'} on turn ${session.expectedTurnId}; kept as diagnostic only`,
      ),
    }
    session.unknownItems.push(diagnostic)
    session.observeDiagnostic(diagnostic)
    return
  }
  for (const entry of projected) {
    if (entry.diagnostic) {
      session.unknownItems.push(entry.diagnostic)
      session.observeDiagnostic(entry.diagnostic)
    }
    if (entry.event) {
      session.observer?.onEvent?.(entry.event)
    }
  }
}

interface ProjectedItem {
  readonly diagnostic?: CodexDiagnostic
  readonly event?: CodexRuntimeTurnEvent
}

function projectItemEvent(event: CodexItemEvent, session: TurnSession): ProjectedItem[] | null {
  switch (event.type) {
    case 'agentMessage':
      if (event.delta === false && session.sawAgentMessageDelta) return []
      return [textEvent('message.delta', session, { text: event.text }), agentMessageTextUpdate(event.text, session)]
    case 'reasoning':
      return [textEvent('reasoning.delta', session, { summary: event.summary })]
    case 'commandExecution':
      return [
        toolEvent(mapToolEventType(event.status), session, {
          toolName: 'command',
          command: event.command,
          status: event.status,
        }),
      ]
    case 'fileChange':
      return [
        {
          event: buildEvent('file_change.recorded', session, {
            path: event.path,
            kind: event.kind,
          }),
        },
      ]
    case 'mcpToolCall':
      return [
        toolEvent(mapToolEventType(event.status), session, {
          toolName: `mcp:${event.tool}`,
          status: event.status,
        }),
      ]
    case 'webSearch':
      return [
        toolEvent('tool_call.started', session, {
          toolName: 'webSearch',
          query: event.query,
        }),
      ]
    case 'contextCompaction':
      return [
        {
          event: buildEvent('compaction', session, {
            threadId: event.threadId,
            turnId: event.turnId,
          }),
        },
      ]
    case 'usage':
      return [
        usageEvent(session, {
          inputTokens: event.inputTokens,
          outputTokens: event.outputTokens,
        }),
      ]
    default:
      return null
  }
}

function mapToolEventType(status: string): 'tool_call.started' | 'tool_call.completed' {
  if (status === 'completed' || status === 'failed' || status === 'error') {
    return 'tool_call.completed'
  }
  return 'tool_call.started'
}

function textEvent(
  type: 'message.delta' | 'reasoning.delta',
  session: TurnSession,
  payload: Record<string, unknown>,
): ProjectedItem {
  return { event: buildEvent(type, session, payload) }
}

function agentMessageTextUpdate(text: string, session: TurnSession): ProjectedItem {
  session.textBuffer.agentMessageText = `${session.textBuffer.agentMessageText}${text}`
  // Final text reconciliation observes every emitted agent message;
  // the final assistant text is the concatenation of all observed
  // chunks in arrival order, trimmed.
  session.textBuffer.finalText = `${session.textBuffer.finalText}${text}`
  return {}
}

function toolEvent(
  type: 'tool_call.started' | 'tool_call.completed',
  session: TurnSession,
  payload: Record<string, unknown>,
): ProjectedItem {
  return { event: buildEvent(type, session, payload) }
}

function usageEvent(
  session: TurnSession,
  payload: { readonly inputTokens: number; readonly outputTokens: number },
): ProjectedItem {
  const totalTokens = payload.inputTokens + payload.outputTokens
  return {
    event: buildEvent('usage.updated', session, {
      inputTokens: payload.inputTokens,
      outputTokens: payload.outputTokens,
      totalTokens,
    }),
  }
}

function buildEvent(type: string, session: TurnSession, payload: Record<string, unknown>): CodexRuntimeTurnEvent {
  return {
    type,
    runtimeSessionId: session.factsState.runtimeSessionId,
    workDir: session.factsState.workDir,
    turnId: session.expectedTurnId ?? '',
    payload,
  }
}

function reconciledFinalText(buffer: TurnTextBuffer): string | null {
  const candidate = (buffer.finalText.length > 0 ? buffer.finalText : buffer.agentMessageText).trim()
  return candidate.length > 0 ? candidate : null
}

// ---------------------------------------------------------------------------
// Lost-response helper
// ---------------------------------------------------------------------------

export interface CodexLostTurnStartArgs {
  readonly threadId: string
  readonly clientUserMessageId: string
  readonly workDir: string
  readonly message?: string
}

/**
 * Surface `unknown` after a `turn/start` response loss. The Input is
 * never resubmitted; the helper exists so callers can carry the
 * structured unknown result through the existing error-normalization
 * boundary without re-deriving the message.
 */
export function buildLostTurnStartUnknown(
  args: CodexLostTurnStartArgs,
): Extract<CodexResult<CodexTurnResult>, { readonly ok: false }> {
  const error = normalizeUnknownCodex(
    `${args.message ?? 'turn/start response was lost after submission may have occurred'} for thread=${args.threadId}; Mohist SessionInput ID ${args.clientUserMessageId} will not be resubmitted`,
  )
  const diagnostics: CodexDiagnostic[] = [
    ...error.diagnostics,
    {
      severity: 'error',
      code: 'lost-turn-start',
      message: redactCodexCredentialString(
        `turn/start lost for thread=${args.threadId} workDir=${args.workDir}; the same Input will not be resubmitted even with the same clientUserMessageId`,
      ),
    },
  ]
  return { ok: false, error, diagnostics }
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

const defaultTurnClock: CodexClock = {
  now: () => Date.now(),
  setTimeout: (callback, delayMs) => setTimeout(callback, delayMs),
  clearTimeout: (handle) => clearTimeout(handle as ReturnType<typeof setTimeout>),
}

function assertPositiveRequestId(value: number): number {
  if (!Number.isSafeInteger(value) || value <= 0) {
    throw new Error(`request id must be a positive safe integer (received ${value})`)
  }
  return value
}
