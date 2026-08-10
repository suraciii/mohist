/**
 * Turn execution for the OpenCode runtime.
 *
 * The runtime turns Workflow Inline Agent work over the native
 * `@opencode-ai/sdk/v2` call surface. It owns:
 *
 *   1. Input validation (`parseModelIdentifier`, type checks on
 *      `options.variant`, tolerance of unknown `options` keys with
 *      a diagnostic).
 *   2. Physical-Session resolution via `client.session.create()` —
 *      `target.runtimeSessionId === null` means "create"; a
 *      non-null id is reused as-is (no rotation on model/variant
 *      change, no rotation on directory change either; directory
 *      change is the caller's responsibility to enforce via a new
 *      binding).
 *   3. Per-turn model/variant application — passed on the prompt
 *      body via `body.model = { providerID, modelID }` and
 *      `body.variant`. Neither enters the cache key or rotates the
 *      physical Session.
 *   4. Awaiting `client.session.prompt()` for completion — this is
 *      the sole completion authority; idle events and SSE silence
 *      do not complete the turn, and `client.v2.session.wait()` is
 *      never called.
 *   5. Provider-error failure policy — only when judged
 *      non-recoverable: a `session.status` retry event whose
 *      `message` matches a non-recoverable pattern (first
 *      occurrence), or a recoverable error retrying until
 *      `attempt` reaches the consecutive-retry threshold without
 *      turn completion. Detection reads retry events only, never
 *      log files. On a non-recoverable judgement the runtime calls
 *      `client.session.abort()` and returns `turn-failed` with the
 *      provider message as diagnostics.
 *   6. Executor-owned deadline backstop — when the caller's abort
 *      signal fires before the prompt response arrives the runtime
 *      calls `client.session.abort()` and returns `interrupted`.
 *   7. Single in-flight work prompt per logical Session — the
 *      runtime carries an in-flight set so a concurrent work
 *      prompt on the same logical key is rejected without rotating
 *      the physical Session.
 *
 * The runtime never auto-replays an uncertain prompt submission;
 * crash-window duplicates are an accepted limitation, and the
 * runtime never adds a deterministic Prompt ID.
 */

import type { OpencodeClient } from "@opencode-ai/sdk/v2"
import type {
  RuntimeDiagnostic,
  RuntimeProviderErrorPolicy,
  RuntimeResult,
  RuntimeSessionTarget,
  RuntimeTurnFacts,
  RuntimeTurnObserver,
  RuntimeTurnOptions,
  RuntimeTurnRequest,
  RuntimeTurnResult,
  RuntimeFilePart,
} from "./types.js"
import {
  DEFAULT_PROVIDER_ERROR_POLICY,
  isNonRecoverableProviderRetry,
  normalizeAbortUnconfirmed,
  normalizeDeadlineExceeded,
  isTransportFailure,
  normalizeInterrupted,
  normalizeInvalidInput,
  normalizeMissingSession,
  normalizePermissionRequired,
  normalizeTurnFailed,
  normalizeUnavailableRuntime,
} from "./errors.js"
import { parseModelIdentifier } from "./model-string.js"
import type { RuntimeEventSubscription, RuntimeGlobalEvent } from "./event-subscription.js"
import { createTimeoutSignal } from "../../system/timeout-signal.js"
import { createRuntimeTurnEventProjector } from "./event-projection.js"

export interface TurnExecutionDeps {
  readonly client: OpencodeClient
  readonly events: RuntimeEventSubscription
  readonly policy?: RuntimeProviderErrorPolicy
  readonly markDirectoryUsed?: () => void
  readonly trackPendingOperation?: (promise: Promise<unknown>) => Promise<void>
  /**
   * Optional hook to inspect retry / session events after they
   * arrive; used by tests to assert routing. Not used in
   * production code paths.
   */
  readonly onEvent?: (event: RuntimeGlobalEvent) => RuntimeEventFailure | null
}

/**
 * Wrap-up warning text. Task-agnostic, runtime-owned; injected via
 * `client.session.promptAsync()` exactly once per Prompt execution,
 * `WARNING_WINDOW_MS` before the declared deadline (or at turn start
 * when the deadline is shorter than the warning window). Must name
 * no marker, file, or task identifier — the per-task wrap-up
 * contract lives in each task's own prompt. Pinned by a spec
 * scenario; do not edit without updating the spec.
 */
export const DEADLINE_WARNING_TEXT = [
  "You will be interrupted in approximately 5 minutes.",
  "Stop starting any new work now. Commit your current changes,",
  "leave a progress record in this task's progress channel,",
  "and end the turn.",
].join(" ")

export const WARNING_WINDOW_MS = 5 * 60 * 1000

export async function runTurn(
  request: RuntimeTurnRequest,
  deps: TurnExecutionDeps,
  signal: AbortSignal,
  observer?: RuntimeTurnObserver,
): Promise<RuntimeResult<RuntimeTurnResult>> {
  const diagnostics: RuntimeDiagnostic[] = []

  const validated = validateTurnInput(request, diagnostics)
  if (validated.kind === "failure") {
    return { ok: false, error: validated.error, diagnostics: [...diagnostics, ...validated.error.diagnostics] }
  }
  const { model, variant } = validated.value

  const deadlineMs = normalizeDeadline(request.deadlineMs)
  const timeoutHandle = deadlineMs !== undefined ? createTimeoutSignal(signal, deadlineMs) : null
  const effectiveSignal = timeoutHandle ? timeoutHandle.signal : signal

  try {
    const sessionId = await resolvePhysicalSession(request.target, model, deps, effectiveSignal)
    if (typeof sessionId !== "string") {
      return sessionId
    }
    if (observer?.onSessionReady) {
      try {
        await observer.onSessionReady({ runtimeSessionId: sessionId, workDir: request.target.workDir })
      } catch (cause) {
        const error = normalizeTurnFailed({ message: errorMessage(cause, "Runtime Session readiness observer failed") })
        return { ok: false, error, diagnostics: [...diagnostics, ...error.diagnostics] }
      }
    }

    const eventProjector = createRuntimeTurnEventProjector(sessionId, request.target.workDir)
    const emitProjectedEvent = (event: ReturnType<typeof eventProjector.project>[number]) => {
      try {
        observer?.onEvent?.(event)
      } catch (cause) {
        diagnostics.push({
          severity: "warning",
          code: "turn-event-observer-failed",
          message: errorMessage(cause, "Runtime turn event observer failed"),
        })
      }
    }
    const runtimeFailureFromEvent = (event: ReturnType<typeof eventProjector.project>[number]): RuntimeEventFailure | null => {
      if (event.type !== "turn.failed") return null
      const message = typeof event.payload["failureReason"] === "string"
        ? event.payload["failureReason"]
        : typeof event.payload["message"] === "string"
          ? event.payload["message"]
          : "OpenCode turn failed"
      const diagnostic = {
        severity: "error" as const,
        code: "turn-failed",
        message,
        details: { source: event.payload["source"] },
      }
      return normalizeTurnFailed({ message }, [diagnostic])
    }

    const policy = deps.policy ?? DEFAULT_PROVIDER_ERROR_POLICY
    const promptResult = await executePrompt({
      client: deps.client,
      events: deps.events,
      sessionId,
      directory: request.target.workDir,
      prompt: request.prompt,
      fileParts: request.fileParts ?? null,
      model,
      variant,
      policy,
      signal: effectiveSignal,
      deadlineMs,
      deadlineExpired: () => timeoutHandle?.timedOut() === true,
      trackPendingOperation: deps.trackPendingOperation,
      onEvent: (event) => {
        deps.onEvent?.(event)
        const eventSessionId = event.sessionID
          ?? (typeof event.payload?.["sessionID"] === "string" ? event.payload["sessionID"] : undefined)
        if (eventSessionId !== sessionId) return
        let failure: RuntimeEventFailure | null = null
        for (const projected of eventProjector.project(event)) {
          const projectedFailure = runtimeFailureFromEvent(projected)
          if (projectedFailure) {
            failure ??= projectedFailure
            continue
          }
          emitProjectedEvent(projected)
        }
        return failure
      },
    })
    if (promptResult.kind === "failure") {
      const errorWithDiagnostics: typeof promptResult.error = {
        ...promptResult.error,
        diagnostics: [...promptResult.error.diagnostics, ...promptResult.diagnostics],
      }
      return { ok: false, error: errorWithDiagnostics, diagnostics: [...diagnostics, ...promptResult.diagnostics] }
    }
    for (const projected of eventProjector.reconcile(promptResult.value.response)) emitProjectedEvent(projected)
    const value: RuntimeTurnResult = {
      facts: promptResult.value.facts,
      diagnostics: [...diagnostics, ...promptResult.value.diagnostics],
    }
    return { ok: true, value, diagnostics: value.diagnostics }
  } finally {
    timeoutHandle?.dispose()
  }
}

function normalizeDeadline(value: number | null | undefined): number | undefined {
  if (value === undefined || value === null) return undefined
  if (typeof value !== "number" || !Number.isFinite(value) || value <= 0) return undefined
  return value
}

export function bindTurnInFlightTracker(): {
  isBusy(sessionId: string): boolean
  start(sessionId: string): boolean
  end(sessionId: string): void
} {
  const busy = new Set<string>()
  return {
    isBusy(sessionId) {
      return busy.has(sessionId)
    },
    start(sessionId) {
      if (busy.has(sessionId)) return false
      busy.add(sessionId)
      return true
    },
    end(sessionId) {
      busy.delete(sessionId)
    },
  }
}

type ValidationOk = { kind: "ok"; value: { model: { providerID: string; modelID: string } | null; variant: string | null } }
type ValidationFailure = { kind: "failure"; error: ReturnType<typeof normalizeInvalidInput> }
type ValidationResult = ValidationOk | ValidationFailure

function validateTurnInput(request: RuntimeTurnRequest, diagnostics: RuntimeDiagnostic[]): ValidationResult {
  const options: RuntimeTurnOptions | undefined | null = request.options ?? undefined
  if (options?.unknownKeys && options.unknownKeys.length > 0) {
    diagnostics.push({
      severity: "info",
      code: "options-unknown-keys",
      message: `Ignored unknown option keys: ${options.unknownKeys.join(", ")}`,
      details: { keys: options.unknownKeys },
    })
  }
  let model: { providerID: string; modelID: string } | null = null
  if (options?.model !== undefined && options.model !== null) {
    if (typeof options.model !== "object") {
      return { kind: "failure", error: normalizeInvalidInput("options.model must be an object with providerID and modelID when present") }
    }
    model = options.model
  }
  let variant: string | null = null
  if (options?.variant !== undefined && options.variant !== null) {
    if (typeof options.variant !== "string") {
      return { kind: "failure", error: normalizeInvalidInput("options.variant must be a string when present") }
    }
    variant = options.variant
  }
  if (model === null && options?.model !== undefined && options.model !== null) {
    return { kind: "failure", error: normalizeInvalidInput("options.model must not be null when present") }
  }
  return { kind: "ok", value: { model, variant } }
}

async function resolvePhysicalSession(
  target: RuntimeSessionTarget,
  model: { providerID: string; modelID: string } | null,
  deps: TurnExecutionDeps,
  signal: AbortSignal,
): Promise<string | RuntimeResult<RuntimeTurnResult>> {
  if (signal.aborted) {
    const error = normalizeInterrupted()
    return { ok: false, error, diagnostics: error.diagnostics }
  }
  deps.markDirectoryUsed?.()
  if (target.runtimeSessionId) {
    try {
      const resolved = await deps.client.session.get({
        sessionID: target.runtimeSessionId,
        directory: target.workDir,
      }, { throwOnError: true })
      const resolvedData = (resolved as { data?: { id?: string } } | undefined)?.data
      if (!resolvedData || resolvedData.id !== target.runtimeSessionId) {
        const error = normalizeMissingSession()
        return { ok: false, error, diagnostics: error.diagnostics }
      }
      return target.runtimeSessionId
    } catch (cause) {
      const error = toUnavailableOrTurnError(cause, "Failed to restore persisted Runtime Session")
      return { ok: false, error, diagnostics: error.diagnostics }
    }
  }
  try {
    const created = await deps.client.session.create({
      directory: target.workDir,
      ...(model ? { model: { providerID: model.providerID, id: model.modelID } } : {}),
    }, { throwOnError: true })
    const data = (created as { data?: { id?: string } } | undefined)?.data
    if (!data || typeof data.id !== "string") {
      const error = normalizeTurnFailed({ message: "session.create returned no id" })
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    return data.id
  } catch (cause) {
    const error = toUnavailableOrTurnError(cause, "Failed to create physical Session")
    return { ok: false, error, diagnostics: error.diagnostics }
  }
}

interface ExecutePromptArgs {
  readonly client: OpencodeClient
  readonly events: RuntimeEventSubscription
  readonly sessionId: string
  readonly directory: string
  readonly prompt: string
  readonly fileParts: readonly RuntimeFilePart[] | null
  readonly model: { providerID: string; modelID: string } | null
  readonly variant: string | null
  readonly policy: RuntimeProviderErrorPolicy
  readonly signal: AbortSignal
  readonly deadlineMs?: number
  readonly deadlineExpired: () => boolean
  readonly trackPendingOperation?: (promise: Promise<unknown>) => Promise<void>
  readonly onEvent?: (event: RuntimeGlobalEvent) => void
}

type PromptSuccess = { kind: "success"; value: { facts: RuntimeTurnFacts; diagnostics: RuntimeDiagnostic[]; response: unknown } }
type PromptFailure = {
  kind: "failure"
  error: ReturnType<typeof normalizeInterrupted> | ReturnType<typeof normalizeDeadlineExceeded> | ReturnType<typeof normalizeTurnFailed> | ReturnType<typeof normalizeUnavailableRuntime> | ReturnType<typeof normalizeMissingSession> | ReturnType<typeof normalizePermissionRequired> | ReturnType<typeof normalizeAbortUnconfirmed>
  diagnostics: RuntimeDiagnostic[]
}
type PromptResult = PromptSuccess | PromptFailure

type RuntimeEventFailure = ReturnType<typeof normalizeTurnFailed> | ReturnType<typeof normalizeUnavailableRuntime>
type AbortReason = "provider" | "reconciliation-failed" | "permission-reply-failed" | "runtime-failure" | "deadline" | "signal"

interface ProviderRetryStatus {
  readonly type?: string
  readonly attempt?: number
  readonly message?: string
  readonly action?: { readonly reason?: string }
}

async function executePrompt(args: ExecutePromptArgs): Promise<PromptResult> {
  const retryTracker = createRetryTracker(args.policy, args.sessionId, args.directory)
  const abortPromise = deferred<AbortReason>()
  let requestedAbort: AbortReason | null = null
  let reconciliationFailure: RuntimeDiagnostic | null = null
  let permissionFailure: RuntimeDiagnostic | null = null
  let runtimeFailure: RuntimeEventFailure | null = null
  let reconciliationInFlight: Promise<void> | null = null
  const repliedPermissionIds = new Set<string>()
  const resolveAbort = (reason: AbortReason) => {
    if (requestedAbort !== null) return
    requestedAbort = reason
    abortPromise.resolve(reason)
  }
  const resolveRuntimeFailure = (failure: RuntimeEventFailure) => {
    if (runtimeFailure !== null) return
    runtimeFailure = failure
    resolveAbort("runtime-failure")
  }
  const resolveProviderAbort = () => {
    if (retryTracker.abortedDueToNonRecoverable() || retryTracker.abortedDueToThreshold()) {
      resolveAbort("provider")
    }
  }
  const reconcileRetryStatus = async () => {
    const response = await args.client.session.status(
      { directory: args.directory },
      { throwOnError: true },
    )
    const statuses = response.data
    if (!statuses || typeof statuses !== "object") {
      throw new Error("session.status returned no status map")
    }
    retryTracker.observeStatus((statuses as Record<string, ProviderRetryStatus>)[args.sessionId])
    resolveProviderAbort()
  }
  const startReconciliation = () => {
    if (reconciliationInFlight !== null) return
    const pending = reconcileRetryStatus().catch((cause) => {
      reconciliationFailure = providerStatusDiagnostic(cause)
      resolveAbort("reconciliation-failed")
    })
    reconciliationInFlight = pending
    args.trackPendingOperation?.(pending)
    void pending.finally(() => {
      if (reconciliationInFlight === pending) reconciliationInFlight = null
    })
  }
  const respondToPermissionRequest = (event: RuntimeGlobalEvent) => {
    const requestId = permissionRequestIdFor(event, args.sessionId, args.directory)
    if (requestId === null || repliedPermissionIds.has(requestId)) return
    repliedPermissionIds.add(requestId)
    const pendingReply = args.client.permission.reply({
      requestID: requestId,
      directory: args.directory,
      reply: "once",
    }, { throwOnError: true }).then((response) => {
      if (response.data !== true) {
        throw new Error("OpenCode permission.reply did not confirm the permission was handled")
      }
    }).catch((cause) => {
      permissionFailure = {
        severity: "error",
        code: "permission-reply-failed",
        message: `Failed to reply to OpenCode permission request: ${errorMessage(cause, "unknown permission error")}`,
      }
      resolveAbort("permission-reply-failed")
    })
    args.trackPendingOperation?.(pendingReply)
  }
  const unsubscribe = args.events.subscribe((event) => {
    if (event.type === "server.connected") startReconciliation()
    respondToPermissionRequest(event)
    retryTracker.observe(event)
    resolveProviderAbort()
    const failure = args.onEvent?.(event)
    if (failure) resolveRuntimeFailure(failure)
  })

  let abortHandler: (() => void) | null = null
  const onAbort = () => {
    resolveAbort(args.deadlineExpired() ? "deadline" : "signal")
  }
  if (args.signal.aborted) {
    onAbort()
  } else {
    args.signal.addEventListener("abort", onAbort, { once: true })
    abortHandler = () => args.signal.removeEventListener("abort", onAbort)
  }

  const promptDiagnostics: RuntimeDiagnostic[] = []
  const cancelWarning = scheduleDeadlineWarning(args, promptDiagnostics)

  let promptOutcome: { ok: true; response: unknown } | { ok: false; cause: unknown }
  try {
    const initialStatus = reconcileRetryStatus().then(
      () => ({ ok: true as const }),
      (cause: unknown) => ({ ok: false as const, cause }),
    )
    args.trackPendingOperation?.(initialStatus)
    const initialRace = await Promise.race([initialStatus, abortPromise.promise])
    if (typeof initialRace === "string") {
      return finishAbortedTurn(args, retryTracker, initialRace, reconciliationFailure, permissionFailure, promptDiagnostics, runtimeFailure)
    }
    if (!initialRace.ok) {
      const diagnostic = providerStatusDiagnostic(initialRace.cause)
      const error = normalizeUnavailableRuntime([diagnostic])
      return { kind: "failure", error, diagnostics: [...error.diagnostics, ...promptDiagnostics] }
    }
    if (requestedAbort !== null) {
      return finishAbortedTurn(args, retryTracker, requestedAbort, reconciliationFailure, permissionFailure, promptDiagnostics, runtimeFailure)
    }

    const promptCall = args.client.session.prompt({
      sessionID: args.sessionId,
      directory: args.directory,
      ...buildPromptBody(args.prompt, args.fileParts, args.model, args.variant),
    }, { throwOnError: true }).then(
      (response: unknown) => ({ ok: true as const, response }),
      (cause: unknown) => ({ ok: false as const, cause }),
    )
    args.trackPendingOperation?.(promptCall)
    const raced = await Promise.race([promptCall, abortPromise.promise])
    if (typeof raced === "string") {
      return finishAbortedTurn(args, retryTracker, raced, reconciliationFailure, permissionFailure, promptDiagnostics, runtimeFailure)
    }
    promptOutcome = raced as { ok: true; response: unknown } | { ok: false; cause: unknown }
  } finally {
    cancelWarning()
    unsubscribe()
    abortHandler?.()
  }

  if (runtimeFailure !== null) {
    return finishAbortedTurn(args, retryTracker, "runtime-failure", reconciliationFailure, permissionFailure, promptDiagnostics, runtimeFailure)
  }

  if (!promptOutcome.ok) {
    if (retryTracker.abortedDueToNonRecoverable() || retryTracker.abortedDueToThreshold()) {
      return finishAbortedTurn(args, retryTracker, "provider", reconciliationFailure, permissionFailure, promptDiagnostics, runtimeFailure)
    }
    if (reconciliationFailure !== null) {
      return finishAbortedTurn(args, retryTracker, "reconciliation-failed", reconciliationFailure, permissionFailure, promptDiagnostics, runtimeFailure)
    }
    if (args.signal.aborted) {
      return finishAbortedTurn(args, retryTracker, args.deadlineExpired() ? "deadline" : "signal", reconciliationFailure, permissionFailure, promptDiagnostics, runtimeFailure)
    }
    if (isTransportFailure(promptOutcome.cause)) {
      return finishTransportFailure(args, promptOutcome.cause, promptDiagnostics)
    }
    const error = toUnavailableOrTurnError(promptOutcome.cause, "OpenCode prompt failed")
    return { kind: "failure", error, diagnostics: [...error.diagnostics, ...promptDiagnostics] }
  }

  const finalText = extractFinalAssistantText(promptOutcome.response)
  const facts: RuntimeTurnFacts = {
    finalAssistantText: finalText,
    runtimeSessionId: args.sessionId,
    workDir: args.directory,
  }
  return { kind: "success", value: { facts, diagnostics: [...promptDiagnostics], response: promptOutcome.response } }
}

async function finishTransportFailure(
  args: ExecutePromptArgs,
  cause: unknown,
  promptDiagnostics: RuntimeDiagnostic[],
): Promise<PromptFailure> {
  const transportDiagnostic: RuntimeDiagnostic = {
    severity: "error",
    code: "opencode-transport-failed",
    message: `OpenCode local transport failed: ${errorMessage(cause, "unknown transport error")}`,
  }
  const abortResult = await abortAndConfirmSession(args.client, args.sessionId, args.directory)
  if (!abortResult.ok) {
    const error = normalizeAbortUnconfirmed(abortResult.message, [...promptDiagnostics, transportDiagnostic])
    return { kind: "failure", error, diagnostics: [...error.diagnostics] }
  }
  const error = toUnavailableOrTurnError(cause, "OpenCode local transport failed")
  return { kind: "failure", error, diagnostics: [...error.diagnostics, ...promptDiagnostics, transportDiagnostic] }
}

interface RetryTracker {
  observe(event: RuntimeGlobalEvent): void
  observeStatus(status: ProviderRetryStatus | undefined): void
  abortedDueToNonRecoverable(): boolean
  abortedDueToThreshold(): boolean
  nonRecoverableVerdict(): { message: string; diagnostics: RuntimeDiagnostic[] } | null
  thresholdVerdict(): { message: string; diagnostics: RuntimeDiagnostic[] } | null
}

function permissionRequestIdFor(
  event: RuntimeGlobalEvent,
  expectedSessionId: string,
  expectedDirectory: string,
): string | null {
  if (event.type !== "permission.asked") return null
  if (event.sessionID !== expectedSessionId) return null
  if (event.directory !== undefined && event.directory !== expectedDirectory) return null
  const requestId = event.payload?.["id"]
  return typeof requestId === "string" && requestId.length > 0 ? requestId : null
}

function createRetryTracker(
  policy: RuntimeProviderErrorPolicy,
  expectedSessionId: string,
  expectedDirectory: string,
): RetryTracker {
  let lastAttempt = 0
  let nonRecoverableHit = false
  let thresholdHit = false
  let nonRecoverableMessage = ""
  let thresholdMessage = ""
  let nonRecoverableActionReason: string | undefined

  const observeStatus = (status: ProviderRetryStatus | undefined) => {
    if (!status || status.type !== "retry") return
    const attempt = typeof status.attempt === "number" ? status.attempt : 0
    const message = typeof status.message === "string" ? status.message : ""
    lastAttempt = attempt
    if (!nonRecoverableHit && isNonRecoverableProviderRetry({ message, action: status.action }, policy.nonRecoverablePatterns)) {
      nonRecoverableHit = true
      nonRecoverableMessage = message
      nonRecoverableActionReason = status.action?.reason
    }
    if (!thresholdHit && attempt >= policy.consecutiveRetryThreshold) {
      thresholdHit = true
      thresholdMessage = message
    }
  }

  const observe = (event: RuntimeGlobalEvent) => {
    if (event.type !== "session.status") return
    const props = event.payload ?? {}
    const sessionID = (props["sessionID"] as string | undefined) ?? event.sessionID
    if (sessionID !== expectedSessionId) return
    if (event.directory !== undefined && event.directory !== expectedDirectory) return
    observeStatus(props["status"] as ProviderRetryStatus | undefined)
  }

  return {
    observe,
    observeStatus,
    abortedDueToNonRecoverable: () => nonRecoverableHit,
    abortedDueToThreshold: () => thresholdHit,
    nonRecoverableVerdict: () => nonRecoverableHit ? {
      message: nonRecoverableMessage,
      diagnostics: [{
        severity: "error",
        code: "provider-quota-exhausted",
        message: `Provider error judged non-recoverable on retry attempt ${lastAttempt}: ${nonRecoverableMessage}`,
        ...(nonRecoverableActionReason ? { details: { actionReason: nonRecoverableActionReason } } : {}),
      }],
    } : null,
    thresholdVerdict: () => thresholdHit ? {
      message: thresholdMessage,
      diagnostics: [{
        severity: "error",
        code: "provider-retry-threshold",
        message: `Provider error retry attempt reached ${lastAttempt} (>= ${policy.consecutiveRetryThreshold}) without completion: ${thresholdMessage}`,
      }],
    } : null,
  }
}

async function finishAbortedTurn(
  args: ExecutePromptArgs,
  retryTracker: RetryTracker,
  reason: AbortReason,
  reconciliationFailure: RuntimeDiagnostic | null,
  permissionFailure: RuntimeDiagnostic | null,
  promptDiagnostics: RuntimeDiagnostic[],
  runtimeFailure: RuntimeEventFailure | null,
): Promise<PromptFailure> {
  const providerVerdict = retryTracker.nonRecoverableVerdict() ?? retryTracker.thresholdVerdict()
  const contextDiagnostics = [
    ...(providerVerdict?.diagnostics ?? []),
    ...(reconciliationFailure ? [reconciliationFailure] : []),
    ...(permissionFailure ? [permissionFailure] : []),
    ...promptDiagnostics,
  ]
  const abortResult = await abortAndConfirmSession(args.client, args.sessionId, args.directory)
  if (reason === "deadline") {
    const diagnostics = abortResult.ok
      ? contextDiagnostics
      : [...contextDiagnostics, { severity: "error" as const, code: "abort-unconfirmed", message: abortResult.message }]
    const error = normalizeDeadlineExceeded(args.deadlineMs ?? 0, diagnostics)
    return { kind: "failure", error, diagnostics: [...error.diagnostics] }
  }
  if (reason === "runtime-failure" && runtimeFailure) {
    if (!abortResult.ok) {
      const error = {
        ...runtimeFailure,
        diagnostics: [...runtimeFailure.diagnostics, { severity: "error" as const, code: "abort-unconfirmed", message: abortResult.message }],
      }
      return { kind: "failure", error, diagnostics: [...error.diagnostics] }
    }
    return { kind: "failure", error: runtimeFailure, diagnostics: [...runtimeFailure.diagnostics] }
  }
  if (!abortResult.ok) {
    const error = normalizeAbortUnconfirmed(abortResult.message, contextDiagnostics)
    return { kind: "failure", error, diagnostics: [...error.diagnostics] }
  }

  const nonRecoverable = retryTracker.nonRecoverableVerdict()
  if (nonRecoverable) {
    const error = normalizeTurnFailed(
      { message: nonRecoverable.message },
      [...nonRecoverable.diagnostics, ...promptDiagnostics],
    )
    return { kind: "failure", error, diagnostics: [...error.diagnostics] }
  }

  const threshold = retryTracker.thresholdVerdict()
  if (threshold) {
    const error = normalizeTurnFailed(
      { message: threshold.message },
      [...threshold.diagnostics, ...promptDiagnostics],
    )
    return { kind: "failure", error, diagnostics: [...error.diagnostics] }
  }

  if (reason === "reconciliation-failed" && reconciliationFailure) {
    const error = normalizeUnavailableRuntime([reconciliationFailure, ...promptDiagnostics])
    return { kind: "failure", error, diagnostics: [...error.diagnostics] }
  }

  if (reason === "permission-reply-failed" && permissionFailure) {
    const error = normalizePermissionRequired([permissionFailure, ...promptDiagnostics])
    return { kind: "failure", error, diagnostics: [...error.diagnostics] }
  }

  const error = normalizeInterrupted(promptDiagnostics)
  return { kind: "failure", error, diagnostics: [...error.diagnostics] }
}

function providerStatusDiagnostic(cause: unknown): RuntimeDiagnostic {
  return {
    severity: "error",
    code: "status-reconciliation-failed",
    message: `Failed to reconcile OpenCode Session status: ${errorMessage(cause, "unknown status error")}`,
  }
}

function buildPromptBody(
  prompt: string,
  fileParts: readonly RuntimeFilePart[] | null,
  model: { providerID: string; modelID: string } | null,
  variant: string | null,
) {
  const parts: Array<{ type: "text"; text: string } | { type: "file"; mime: string; filename: string; url: string }> = [
    { type: "text", text: prompt },
  ]
  if (fileParts) {
    for (const part of fileParts) {
      parts.push({ type: "file", mime: part.mime, filename: part.filename, url: part.url })
    }
  }
  return {
    parts,
    ...(model ? { model: { providerID: model.providerID, modelID: model.modelID } } : {}),
    ...(variant ? { variant } : {}),
  }
}

function extractFinalAssistantText(response: unknown): string | null {
  if (!response || typeof response !== "object") return null
  const data = (response as { data?: unknown }).data
  if (!data || typeof data !== "object") return null
  const parts = (data as { parts?: unknown }).parts
  if (!Array.isArray(parts)) return null
  const chunks: string[] = []
  for (const part of parts) {
    if (!part || typeof part !== "object") continue
    const text = (part as { text?: unknown }).text
    if (typeof text === "string") chunks.push(text)
  }
  const joined = chunks.join("").trim()
  return joined.length > 0 ? joined : null
}

async function abortAndConfirmSession(
  client: OpencodeClient,
  sessionId: string,
  directory: string,
): Promise<{ ok: true } | { ok: false; message: string }> {
  try {
    const aborted = await client.session.abort(
      { sessionID: sessionId, directory },
      { throwOnError: true },
    )
    if (aborted.data !== true) {
      return { ok: false, message: "OpenCode session.abort did not confirm the turn was stopped" }
    }

    const statusResponse = await client.session.status(
      { directory },
      { throwOnError: true },
    )
    const statuses = statusResponse.data
    if (!statuses || typeof statuses !== "object") {
      return { ok: false, message: "OpenCode session.status returned no status map after abort" }
    }
    const status = (statuses as Record<string, ProviderRetryStatus>)[sessionId]
    if (status !== undefined && status.type !== "idle") {
      return { ok: false, message: `OpenCode Session remained ${status.type ?? "active"} after abort` }
    }
    return { ok: true }
  } catch (cause) {
    return {
      ok: false,
      message: `OpenCode turn abort or status confirmation failed: ${errorMessage(cause, "unknown abort error")}`,
    }
  }
}

/**
 * Schedule the deadline wrap-up warning for the duration of a single
 * Prompt execution. The returned cancel function clears the pending
 * timer — caller MUST invoke it once the prompt has settled so a
 * turn that completes before the warning is due never injects it.
 *
 * The injection itself is fire-and-forget: rejection from
 * `client.session.promptAsync()` is swallowed into a single info-level
 * `deadline-warning-injection-failed` diagnostic; the runtime never
 * fails or retries the turn on the basis of the warning.
 *
 * Returns a no-op cancel function when `args.deadlineMs` is undefined
 * — keeps the call site uniform.
 */
function scheduleDeadlineWarning(args: ExecutePromptArgs, diagnostics: RuntimeDiagnostic[]): () => void {
  if (args.deadlineMs === undefined) return () => {}
  const client = args.client
  const delay = args.deadlineMs > WARNING_WINDOW_MS ? args.deadlineMs - WARNING_WINDOW_MS : 0
  let fired = false
  const timer = setTimeout(() => {
    fired = true
    const pendingWarning = injectDeadlineWarning(client, args, diagnostics)
    args.trackPendingOperation?.(pendingWarning)
  }, delay)
  return () => {
    if (!fired) clearTimeout(timer)
  }
}

async function injectDeadlineWarning(
  client: OpencodeClient,
  args: ExecutePromptArgs,
  diagnostics: RuntimeDiagnostic[],
): Promise<void> {
  try {
    await client.session.promptAsync({
      sessionID: args.sessionId,
      directory: args.directory,
      parts: [{ type: "text", text: DEADLINE_WARNING_TEXT }],
    }, { throwOnError: true })
  } catch (cause) {
    diagnostics.push({
      severity: "info",
      code: "deadline-warning-injection-failed",
      message: `Failed to inject deadline wrap-up warning; the turn continues without it: ${
        cause instanceof Error ? cause.message : String(cause)
      }`,
    })
  }
}

function toUnavailableOrTurnError(cause: unknown, fallback: string) {
  if (cause instanceof Error) {
    const status = (cause as { status?: number }).status
    if (status === 404) return normalizeMissingSession()
  }
  return normalizeTurnFailed(toRawSdkError(cause, fallback))
}

function toRawSdkError(cause: unknown, fallback: string) {
  if (cause instanceof Error) {
    const error = cause as Error & { status?: number; code?: string; cause?: unknown }
    return {
      message: error.message || fallback,
      ...(typeof error.status === "number" ? { status: error.status } : {}),
      ...(typeof error.code === "string" ? { code: error.code } : {}),
      ...(error.cause === undefined ? {} : { cause: error.cause }),
    }
  }
  return { message: errorMessage(cause, fallback) }
}

function errorMessage(cause: unknown, fallback: string): string {
  if (cause instanceof Error) return cause.message || fallback
  return String(cause) || fallback
}

interface DeferredPromise<T> {
  promise: Promise<T>
  resolve(value: T): void
}

function deferred<T>(): DeferredPromise<T> {
  let resolveFn: ((value: T) => void) | null = null
  const promise = new Promise<T>((resolve) => {
    resolveFn = resolve
  })
  return { promise, resolve: (value: T) => resolveFn!(value) }
}

export { parseModelIdentifier }
