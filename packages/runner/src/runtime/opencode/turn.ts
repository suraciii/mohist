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
 *      body via `body.model = { providerID, modelID }`. Variant is
 *      carried alongside model as a runtime-side parameter; the
 *      runtime owns the carrier (it never enters the cache key or
 *      rotates the physical Session).
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
  RuntimeTurnOptions,
  RuntimeTurnRequest,
  RuntimeTurnResult,
} from "./types.js"
import {
  DEFAULT_PROVIDER_ERROR_POLICY,
  isNonRecoverableProviderMessage,
  normalizeInterrupted,
  normalizeInvalidInput,
  normalizeMissingSession,
  normalizeTurnFailed,
  normalizeUnavailableRuntime,
} from "./errors.js"
import { parseModelIdentifier } from "./model-string.js"
import type { RuntimeEventSubscription, RuntimeGlobalEvent } from "./event-subscription.js"

export interface TurnExecutionDeps {
  readonly client: OpencodeClient
  readonly events: RuntimeEventSubscription
  readonly policy?: RuntimeProviderErrorPolicy
  /**
   * Optional hook to inspect retry / session events after they
   * arrive; used by tests to assert routing. Not used in
   * production code paths.
   */
  readonly onEvent?: (event: RuntimeGlobalEvent) => void
}

const SYSTEM_VARIANT_PREFIX = "[mohist variant:"

export async function runTurn(
  request: RuntimeTurnRequest,
  deps: TurnExecutionDeps,
  signal: AbortSignal,
): Promise<RuntimeResult<RuntimeTurnResult>> {
  const diagnostics: RuntimeDiagnostic[] = []

  const validated = validateTurnInput(request, diagnostics)
  if (validated.kind === "failure") {
    return { ok: false, error: validated.error, diagnostics: [...diagnostics, ...validated.error.diagnostics] }
  }
  const { model, variant } = validated.value

  const sessionId = await resolvePhysicalSession(request.target, model, deps, signal)
  if (typeof sessionId !== "string") {
    return sessionId
  }

  const policy = deps.policy ?? DEFAULT_PROVIDER_ERROR_POLICY
  const promptResult = await executePrompt({
    client: deps.client,
    events: deps.events,
    sessionId,
    directory: request.target.workDir,
    prompt: request.prompt,
    model,
    variant,
    policy,
    signal,
    onEvent: deps.onEvent,
  })
  if (promptResult.kind === "failure") {
    const errorWithDiagnostics: typeof promptResult.error = {
      ...promptResult.error,
      diagnostics: [...promptResult.error.diagnostics, ...promptResult.diagnostics],
    }
    return { ok: false, error: errorWithDiagnostics, diagnostics: [...diagnostics, ...promptResult.diagnostics] }
  }
  const value: RuntimeTurnResult = {
    facts: promptResult.value.facts,
    diagnostics: [...diagnostics, ...promptResult.value.diagnostics],
  }
  return { ok: true, value, diagnostics: value.diagnostics }
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
  if (target.runtimeSessionId) {
    try {
      const resolved = await deps.client.session.get({
        path: { id: target.runtimeSessionId },
        query: { directory: target.workDir },
      } as never)
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
    })
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
  readonly model: { providerID: string; modelID: string } | null
  readonly variant: string | null
  readonly policy: RuntimeProviderErrorPolicy
  readonly signal: AbortSignal
  readonly onEvent?: (event: RuntimeGlobalEvent) => void
}

type PromptSuccess = { kind: "success"; value: { facts: RuntimeTurnFacts; diagnostics: RuntimeDiagnostic[] } }
type PromptFailure = {
  kind: "failure"
  error: ReturnType<typeof normalizeInterrupted> | ReturnType<typeof normalizeTurnFailed> | ReturnType<typeof normalizeUnavailableRuntime> | ReturnType<typeof normalizeMissingSession>
  diagnostics: RuntimeDiagnostic[]
}
type PromptResult = PromptSuccess | PromptFailure

async function executePrompt(args: ExecutePromptArgs): Promise<PromptResult> {
  const retryTracker = createRetryTracker(args.policy)
  const abortPromise = deferred<"aborted">()
  const resolveAbort = (reason: "aborted") => abortPromise.resolve(reason)
  const unsubscribe = args.events.subscribe((event) => {
    retryTracker.observe(event)
    if (retryTracker.abortedDueToNonRecoverable() || retryTracker.abortedDueToThreshold()) {
      resolveAbort("aborted")
    }
    args.onEvent?.(event)
  })

  let abortHandler: (() => void) | null = null
  const onAbort = () => {
    resolveAbort("aborted")
  }
  if (args.signal.aborted) {
    onAbort()
  } else {
    args.signal.addEventListener("abort", onAbort, { once: true })
    abortHandler = () => args.signal.removeEventListener("abort", onAbort)
  }

  let promptOutcome: { ok: true; response: unknown } | { ok: false; cause: unknown }
  try {
    const promptCall = args.client.session.prompt({
      path: { id: args.sessionId },
      query: { directory: args.directory },
      body: buildPromptBody(args.prompt, args.model, args.variant),
    } as never).then(
      (response: unknown) => ({ ok: true as const, response }),
      (cause: unknown) => ({ ok: false as const, cause }),
    )
    const raced = await Promise.race([promptCall, abortPromise.promise])
    if (raced === "aborted") {
      await abortSessionSafely(args.client, args.sessionId)
      const nonRecoverable = retryTracker.abortedDueToNonRecoverable()
      const threshold = retryTracker.abortedDueToThreshold()
      if (nonRecoverable) {
        const verdict = retryTracker.nonRecoverableVerdict()!
        const error = normalizeTurnFailed({ message: verdict.message })
        return { kind: "failure", error, diagnostics: [...error.diagnostics, ...verdict.diagnostics] as RuntimeDiagnostic[] }
      }
      if (threshold) {
        const verdict = retryTracker.thresholdVerdict()!
        const error = normalizeTurnFailed({ message: verdict.message })
        return { kind: "failure", error, diagnostics: [...error.diagnostics, ...verdict.diagnostics] as RuntimeDiagnostic[] }
      }
      const abortError = normalizeInterrupted()
      return { kind: "failure", error: abortError, diagnostics: [...abortError.diagnostics] }
    }
    promptOutcome = raced as { ok: true; response: unknown } | { ok: false; cause: unknown }
  } finally {
    unsubscribe()
    abortHandler?.()
  }

  if (!promptOutcome.ok) {
    if (retryTracker.abortedDueToNonRecoverable()) {
      const verdict = retryTracker.nonRecoverableVerdict()!
      await abortSessionSafely(args.client, args.sessionId)
      const error = normalizeTurnFailed({ message: verdict.message })
      return { kind: "failure", error, diagnostics: [...error.diagnostics, ...verdict.diagnostics] as RuntimeDiagnostic[] }
    }
    if (retryTracker.abortedDueToThreshold()) {
      const verdict = retryTracker.thresholdVerdict()!
      await abortSessionSafely(args.client, args.sessionId)
      const error = normalizeTurnFailed({ message: verdict.message })
      return { kind: "failure", error, diagnostics: [...error.diagnostics, ...verdict.diagnostics] as RuntimeDiagnostic[] }
    }
    if (args.signal.aborted) {
      const error = normalizeInterrupted()
      return { kind: "failure", error, diagnostics: [...error.diagnostics] }
    }
    const error = toUnavailableOrTurnError(promptOutcome.cause, "OpenCode prompt failed")
    return { kind: "failure", error, diagnostics: [...error.diagnostics] }
  }

  const finalText = extractFinalAssistantText(promptOutcome.response)
  const facts: RuntimeTurnFacts = {
    finalAssistantText: finalText,
    runtimeSessionId: args.sessionId,
    workDir: args.directory,
  }
  return { kind: "success", value: { facts, diagnostics: [] } }
}

interface RetryTracker {
  observe(event: RuntimeGlobalEvent): void
  abortedDueToNonRecoverable(): boolean
  abortedDueToThreshold(): boolean
  nonRecoverableVerdict(): { message: string; diagnostics: RuntimeDiagnostic[] } | null
  thresholdVerdict(): { message: string; diagnostics: RuntimeDiagnostic[] } | null
}

function createRetryTracker(policy: RuntimeProviderErrorPolicy): RetryTracker {
  let lastAttempt = 0
  let lastMessage = ""
  let nonRecoverableHit = false
  let thresholdHit = false
  let nonRecoverableMessage = ""
  let thresholdMessage = ""

  const observe = (event: RuntimeGlobalEvent) => {
    if (event.type !== "session.status") return
    const props = event.payload ?? {}
    const sessionID = (props["sessionID"] as string | undefined) ?? event.sessionID
    if (sessionID && event.sessionID && sessionID !== event.sessionID) return
    const status = props["status"] as { type?: string; attempt?: number; message?: string } | undefined
    if (!status || status.type !== "retry") return
    const attempt = typeof status.attempt === "number" ? status.attempt : 0
    const message = typeof status.message === "string" ? status.message : ""
    lastAttempt = attempt
    lastMessage = message
    if (!nonRecoverableHit && isNonRecoverableProviderMessage(message, policy.nonRecoverablePatterns)) {
      nonRecoverableHit = true
      nonRecoverableMessage = message
    }
    if (!thresholdHit && attempt >= policy.consecutiveRetryThreshold) {
      thresholdHit = true
      thresholdMessage = message
    }
  }

  return {
    observe,
    abortedDueToNonRecoverable: () => nonRecoverableHit,
    abortedDueToThreshold: () => thresholdHit,
    nonRecoverableVerdict: () => nonRecoverableHit ? {
      message: nonRecoverableMessage,
      diagnostics: [{
        severity: "error",
        code: "provider-non-recoverable",
        message: `Provider error judged non-recoverable on retry attempt ${lastAttempt}: ${nonRecoverableMessage}`,
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

function buildPromptBody(
  prompt: string,
  model: { providerID: string; modelID: string } | null,
  variant: string | null,
) {
  const parts = [{ type: "text", text: prompt }]
  const system = variant ? `${SYSTEM_VARIANT_PREFIX}${variant}]` : undefined
  return {
    parts,
    ...(model ? { model: { providerID: model.providerID, modelID: model.modelID } } : {}),
    ...(system ? { system } : {}),
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

async function abortSessionSafely(client: OpencodeClient, sessionId: string): Promise<void> {
  try {
    await client.session.abort({ path: { id: sessionId } } as never)
  } catch {
    // Best-effort: the abort call exists to clear the in-flight prompt
    // so subsequent restart/reconnect doesn't see a hanging Session.
  }
}

function toUnavailableOrTurnError(cause: unknown, fallback: string) {
  if (cause instanceof Error) {
    const status = (cause as { status?: number }).status
    if (status === 404) return normalizeMissingSession()
  }
  return normalizeTurnFailed({ message: errorMessage(cause, fallback) })
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
