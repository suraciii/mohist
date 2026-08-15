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

import type { OpencodeClient } from '@opencode-ai/sdk/v2'
import type {
  RuntimeDiagnostic,
  RuntimeProviderErrorPolicy,
  RuntimeResult,
  RuntimeSessionTarget,
  RuntimeTurnObserver,
  RuntimeTurnOptions,
  RuntimeTurnRequest,
  RuntimeTurnResult,
} from './types.js'
import {
  DEFAULT_PROVIDER_ERROR_POLICY,
  normalizeInterrupted,
  normalizeInvalidInput,
  normalizeMissingSession,
  normalizeTurnFailed,
  normalizeUnavailableRuntime,
} from './errors.js'
import { parseModelIdentifier } from './model-string.js'
import type { RuntimeEventSubscription, RuntimeGlobalEvent } from './event-subscription.js'
import { createTimeoutSignal } from '../../system/timeout-signal.js'
import { createRuntimeTurnEventProjector } from './event-projection.js'
import { executePrompt } from './turn-prompt.js'

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
  'You will be interrupted in approximately 5 minutes.',
  'Stop starting any new work now. Commit your current changes,',
  "leave a progress record in this task's progress channel,",
  'and end the turn.',
].join(' ')

export const WARNING_WINDOW_MS = 5 * 60 * 1000
/**
 * Abort and status are separate provider calls. Each gets its own bounded
 * cleanup window so a stuck local runtime cannot extend the turn forever.
 */
export const CLEANUP_OPERATION_TIMEOUT_MS = 5_000

export async function runTurn(
  request: RuntimeTurnRequest,
  deps: TurnExecutionDeps,
  signal: AbortSignal,
  observer?: RuntimeTurnObserver,
): Promise<RuntimeResult<RuntimeTurnResult>> {
  const diagnostics: RuntimeDiagnostic[] = []

  const validated = validateTurnInput(request, diagnostics)
  if (validated.kind === 'failure') {
    return { ok: false, error: validated.error, diagnostics: [...diagnostics, ...validated.error.diagnostics] }
  }
  const { model, variant } = validated.value

  const deadlineMs = normalizeDeadline(request.deadlineMs)
  const timeoutHandle = deadlineMs !== undefined ? createTimeoutSignal(signal, deadlineMs) : null
  const effectiveSignal = timeoutHandle ? timeoutHandle.signal : signal

  try {
    const sessionId = await resolvePhysicalSession(request.target, model, deps, effectiveSignal)
    if (typeof sessionId !== 'string') {
      return sessionId
    }
    if (observer?.onSessionReady) {
      try {
        await observer.onSessionReady({ runtimeSessionId: sessionId, workDir: request.target.workDir })
      } catch (cause) {
        const error = normalizeTurnFailed({ message: errorMessage(cause, 'Runtime Session readiness observer failed') })
        return { ok: false, error, diagnostics: [...diagnostics, ...error.diagnostics] }
      }
    }

    const eventProjector = createRuntimeTurnEventProjector(sessionId, request.target.workDir)
    const emitProjectedEvent = (event: ReturnType<typeof eventProjector.project>[number]) => {
      try {
        observer?.onEvent?.(event)
      } catch (cause) {
        diagnostics.push({
          severity: 'warning',
          code: 'turn-event-observer-failed',
          message: errorMessage(cause, 'Runtime turn event observer failed'),
        })
      }
    }
    const runtimeFailureFromEvent = (
      event: ReturnType<typeof eventProjector.project>[number],
    ): RuntimeEventFailure | null => {
      if (event.type !== 'turn.failed') return null
      const message =
        typeof event.payload['failureReason'] === 'string'
          ? event.payload['failureReason']
          : typeof event.payload['message'] === 'string'
            ? event.payload['message']
            : 'OpenCode turn failed'
      const diagnostic = {
        severity: 'error' as const,
        code: 'turn-failed',
        message,
        details: { source: event.payload['source'] },
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
        const eventSessionId =
          event.sessionID ?? (typeof event.payload?.['sessionID'] === 'string' ? event.payload['sessionID'] : undefined)
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
    if (promptResult.kind === 'failure') {
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

/**
 * Adopt a turn that was already submitted before the runner process died.
 * This path never calls session.prompt: the existing runtime execution is
 * the physical execution being recovered. OpenCode does not expose a
 * resumable prompt promise, so the binding is watched until the session is
 * idle and the terminal assistant message is read from the runtime's
 * persisted session state.
 */
export async function reattachTurn(
  request: RuntimeTurnRequest,
  deps: TurnExecutionDeps,
  signal: AbortSignal,
  observer?: RuntimeTurnObserver,
): Promise<RuntimeResult<RuntimeTurnResult>> {
  const sessionId = request.target.runtimeSessionId
  if (!sessionId) {
    const error = normalizeMissingSession()
    return { ok: false, error, diagnostics: error.diagnostics }
  }
  if (signal.aborted) {
    const error = normalizeInterrupted()
    return { ok: false, error, diagnostics: error.diagnostics }
  }

  const initial = await readSessionStatus(deps.client, request.target.workDir, sessionId)
  if (!initial.ok) return initial.error
  if (initial.status !== 'idle') {
    const waited = await waitForSessionIdle(deps, request.target.workDir, sessionId, signal, observer)
    if (waited) return waited
  } else if (signal.aborted) {
    const error = normalizeInterrupted()
    return { ok: false, error, diagnostics: error.diagnostics }
  }

  if (signal.aborted) {
    const error = normalizeInterrupted()
    return { ok: false, error, diagnostics: error.diagnostics }
  }
  try {
    const messages = await (deps.client.session as unknown as {
      messages: (parameters: { sessionID: string; limit?: number }, options?: { throwOnError?: boolean }) => Promise<unknown>
    }).messages({ sessionID: sessionId, limit: 100 }, { throwOnError: true })
    const finalAssistantText = extractReattachedAssistantText(messages)
    return {
      ok: true,
      value: {
        facts: { finalAssistantText, runtimeSessionId: sessionId, workDir: request.target.workDir },
        diagnostics: [],
      },
      diagnostics: [],
    }
  } catch (cause) {
    const error = normalizeTurnFailed({ message: errorMessage(cause, 'Failed to read the reattached Runtime Session result') })
    return { ok: false, error, diagnostics: error.diagnostics }
  }
}

async function waitForSessionIdle(
  deps: TurnExecutionDeps,
  directory: string,
  sessionId: string,
  signal: AbortSignal,
  observer?: RuntimeTurnObserver,
): Promise<RuntimeResult<RuntimeTurnResult> | null> {
  return await new Promise<RuntimeResult<RuntimeTurnResult> | null>((resolve) => {
    let settled = false
    let timer: ReturnType<typeof setInterval> | null = null
    let unsubscribe = () => {}
    const finish = (result: RuntimeResult<RuntimeTurnResult> | null) => {
      if (settled) return
      settled = true
      if (timer) clearInterval(timer)
      unsubscribe()
      signal.removeEventListener('abort', onAbort)
      resolve(result)
    }
    const check = async () => {
      if (settled) return
      const status = await readSessionStatus(deps.client, directory, sessionId)
      if (!status.ok) {
        finish(status.error)
        return
      }
      if (status.status === 'idle') finish(null)
    }
    const onAbort = () => {
      const error = normalizeInterrupted()
      finish({ ok: false, error, diagnostics: error.diagnostics })
    }
    if (signal.aborted) {
      onAbort()
      return
    }
    signal.addEventListener('abort', onAbort, { once: true })
    unsubscribe = deps.events.subscribe((event) => {
      if (event.sessionID !== undefined && event.sessionID !== sessionId) return
      if (event.type === 'session.idle') void check()
      if (event.type === 'session.error' || event.type === 'session.next.step.failed') {
        const error = normalizeTurnFailed({ message: 'The reattached Runtime Session reported a terminal failure' })
        finish({ ok: false, error, diagnostics: error.diagnostics })
      }
      // Reattached work does not replay observations into the AgentSession;
      // the original event producer owns those facts. The observer is kept in
      // the signature for the same runtime boundary as ordinary turns.
      void observer
    })
    timer = setInterval(() => void check(), 250)
    timer.unref?.()
    void check()
  })
}

async function readSessionStatus(
  client: OpencodeClient,
  directory: string,
  sessionId: string,
): Promise<{ ok: true; status: string } | { ok: false; error: RuntimeResult<RuntimeTurnResult> }> {
  try {
    const response = await client.session.status({ directory }, { throwOnError: true })
    const statuses = response.data
    if (!statuses || typeof statuses !== 'object') throw new Error('session.status returned no status map')
    const status = (statuses as Record<string, ProviderRetryStatus>)[sessionId]
    return { ok: true, status: status?.type ?? 'idle' }
  } catch (cause) {
    const error = toUnavailableOrTurnError(cause, 'Failed to read reattached Runtime Session status')
    return { ok: false, error: { ok: false, error, diagnostics: error.diagnostics } }
  }
}

function extractReattachedAssistantText(response: unknown): string | null {
  const responseRecord = recordValue(response)
  const rawData = responseRecord?.['data']
  if (Array.isArray(rawData)) return finalAssistantTextFromMessages(rawData)
  const data = recordValue(rawData)
  if (!data) return null
  const messages = data['messages']
  if (Array.isArray(messages)) return finalAssistantTextFromMessages(messages)
  const info = recordValue(data['info'])
  const parts = data['parts']
  if (info && Array.isArray(parts)) return textFromParts(parts)
  return null
}

function finalAssistantTextFromMessages(messages: readonly unknown[]): string | null {
  for (const message of [...messages].reverse()) {
    const record = recordValue(message)
    if (!record || record['type'] !== 'assistant') continue
    const text = textFromParts(record['content'])
    if (text) return text
  }
  return null
}

function textFromParts(parts: unknown): string | null {
  if (!Array.isArray(parts)) return null
  const text = parts
    .map((part) => {
      const record = recordValue(part)
      if (!record) return ''
      if (typeof record['text'] === 'string') return record['text']
      if (record['type'] === 'text' && typeof record['content'] === 'string') return record['content']
      return ''
    })
    .join('')
    .trim()
  return text || null
}

function recordValue(value: unknown): Record<string, unknown> | null {
  return value !== null && typeof value === 'object' && !Array.isArray(value) ? (value as Record<string, unknown>) : null
}

function normalizeDeadline(value: number | null | undefined): number | undefined {
  if (value === undefined || value === null) return undefined
  if (typeof value !== 'number' || !Number.isFinite(value) || value <= 0) return undefined
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

type ValidationOk = {
  kind: 'ok'
  value: { model: { providerID: string; modelID: string } | null; variant: string | null }
}
type ValidationFailure = { kind: 'failure'; error: ReturnType<typeof normalizeInvalidInput> }
type ValidationResult = ValidationOk | ValidationFailure

function validateTurnInput(request: RuntimeTurnRequest, diagnostics: RuntimeDiagnostic[]): ValidationResult {
  const options: RuntimeTurnOptions | undefined | null = request.options ?? undefined
  if (options?.unknownKeys && options.unknownKeys.length > 0) {
    diagnostics.push({
      severity: 'info',
      code: 'options-unknown-keys',
      message: `Ignored unknown option keys: ${options.unknownKeys.join(', ')}`,
      details: { keys: options.unknownKeys },
    })
  }
  let model: { providerID: string; modelID: string } | null = null
  if (options?.model !== undefined && options.model !== null) {
    if (typeof options.model !== 'object') {
      return {
        kind: 'failure',
        error: normalizeInvalidInput('options.model must be an object with providerID and modelID when present'),
      }
    }
    model = options.model
  }
  let variant: string | null = null
  if (options?.variant !== undefined && options.variant !== null) {
    if (typeof options.variant !== 'string') {
      return { kind: 'failure', error: normalizeInvalidInput('options.variant must be a string when present') }
    }
    variant = options.variant
  }
  if (model === null && options?.model !== undefined && options.model !== null) {
    return { kind: 'failure', error: normalizeInvalidInput('options.model must not be null when present') }
  }
  return { kind: 'ok', value: { model, variant } }
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
      const resolved = await deps.client.session.get(
        {
          sessionID: target.runtimeSessionId,
          directory: target.workDir,
        },
        { throwOnError: true },
      )
      const resolvedData = (resolved as { data?: { id?: string } } | undefined)?.data
      if (!resolvedData || resolvedData.id !== target.runtimeSessionId) {
        const error = normalizeMissingSession()
        return { ok: false, error, diagnostics: error.diagnostics }
      }
      return target.runtimeSessionId
    } catch (cause) {
      const error = toUnavailableOrTurnError(cause, 'Failed to restore persisted Runtime Session')
      return { ok: false, error, diagnostics: error.diagnostics }
    }
  }
  try {
    const created = await deps.client.session.create(
      {
        directory: target.workDir,
        ...(model ? { model: { providerID: model.providerID, id: model.modelID } } : {}),
      },
      { throwOnError: true },
    )
    const data = (created as { data?: { id?: string } } | undefined)?.data
    if (!data || typeof data.id !== 'string') {
      const error = normalizeTurnFailed({ message: 'session.create returned no id' })
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    return data.id
  } catch (cause) {
    const error = toUnavailableOrTurnError(cause, 'Failed to create physical Session')
    return { ok: false, error, diagnostics: error.diagnostics }
  }
}

export type RuntimeEventFailure =
  | ReturnType<typeof normalizeTurnFailed>
  | ReturnType<typeof normalizeUnavailableRuntime>
export interface ProviderRetryStatus {
  readonly type?: string
  readonly attempt?: number
  readonly message?: string
  readonly action?: { readonly reason?: string }
}

export async function abortAndConfirmSession(
  client: OpencodeClient,
  sessionId: string,
  directory: string,
): Promise<
  | { ok: true }
  | {
      ok: false
      code: 'abort-unconfirmed' | 'abort-cleanup-timeout' | 'status-cleanup-timeout'
      message: string
      missingSession?: boolean
    }
> {
  let aborted: Awaited<ReturnType<OpencodeClient['session']['abort']>>
  try {
    aborted = await withCleanupTimeout(
      () => client.session.abort({ sessionID: sessionId, directory }, { throwOnError: true }),
      'abort',
    )
  } catch (cause) {
    const timedOut = isCleanupTimeout(cause, 'abort')
    const missingSession = (cause as { status?: number } | undefined)?.status === 404
    return {
      ok: false,
      code: timedOut ? 'abort-cleanup-timeout' : 'abort-unconfirmed',
      ...(missingSession ? { missingSession: true } : {}),
      message: timedOut
        ? `OpenCode session.abort cleanup timed out after ${CLEANUP_OPERATION_TIMEOUT_MS}ms`
        : `OpenCode session.abort failed to confirm the turn was stopped: ${errorMessage(cause, 'unknown abort error')}`,
    }
  }

  if (aborted.data !== true) {
    return {
      ok: false,
      code: 'abort-unconfirmed',
      message: 'OpenCode session.abort did not confirm the turn was stopped',
    }
  }

  try {
    const statusResponse = await withCleanupTimeout(
      () => client.session.status({ directory }, { throwOnError: true }),
      'status',
    )
    const statuses = statusResponse.data
    if (!statuses || typeof statuses !== 'object') {
      return {
        ok: false,
        code: 'abort-unconfirmed',
        message: 'OpenCode session.status returned no status map after abort',
      }
    }
    const status = (statuses as Record<string, ProviderRetryStatus>)[sessionId]
    if (status !== undefined && status.type !== 'idle') {
      return {
        ok: false,
        code: 'abort-unconfirmed',
        message: `OpenCode Session remained ${status.type ?? 'active'} after abort`,
      }
    }
    return { ok: true }
  } catch (cause) {
    const timedOut = isCleanupTimeout(cause, 'status')
    return {
      ok: false,
      code: timedOut ? 'status-cleanup-timeout' : 'abort-unconfirmed',
      message: timedOut
        ? `OpenCode session.status cleanup timed out after ${CLEANUP_OPERATION_TIMEOUT_MS}ms`
        : `OpenCode session.status failed after abort: ${errorMessage(cause, 'unknown status error')}`,
    }
  }
}

async function withCleanupTimeout<T>(operation: () => Promise<T>, operationName: 'abort' | 'status'): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined
  try {
    return await Promise.race([
      operation(),
      new Promise<never>((_resolve, reject) => {
        timer = setTimeout(
          () => reject(new Error(`session.${operationName} cleanup timeout`)),
          CLEANUP_OPERATION_TIMEOUT_MS,
        )
      }),
    ])
  } finally {
    if (timer !== undefined) clearTimeout(timer)
  }
}

function isCleanupTimeout(cause: unknown, operationName: 'abort' | 'status'): boolean {
  return cause instanceof Error && cause.message === `session.${operationName} cleanup timeout`
}

export function toUnavailableOrTurnError(cause: unknown, fallback: string) {
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
      ...(typeof error.status === 'number' ? { status: error.status } : {}),
      ...(typeof error.code === 'string' ? { code: error.code } : {}),
      ...(error.cause === undefined ? {} : { cause: error.cause }),
    }
  }
  return { message: errorMessage(cause, fallback) }
}

export function errorMessage(cause: unknown, fallback: string): string {
  if (cause instanceof Error) return cause.message || fallback
  return String(cause) || fallback
}

export { parseModelIdentifier }
