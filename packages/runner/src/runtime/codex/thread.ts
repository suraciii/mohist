/**
 * Codex Thread lifecycle (`thread/start`, `thread/resume`).
 *
 * The Thread ID is the existing opaque `runtimeSessionId`; the
 * persistent identity that Mohist persists, routes, and arbitrates.
 * The Codex Turn ID lives in module memory for the lifetime of one
 * app-server generation; this file never touches it.
 *
 * Rules enforced here:
 *
 *   - `thread/start` carries the immutable working directory, the
 *     locked `approvalPolicy: 'never'` + `sandbox: 'danger-full-access'`
 *     trust settings, non-ephemeral history, the resolved model, and
 *     the canonical Reasoning Effort. No caller-supplied idempotency
 *     key is sent; a lost response is unknown and never retried.
 *   - `thread/resume` runs on the bound Runner only and uses
 *     `excludeTurns: true`. The bound Runner is the producer of the
 *     `thread_not_found` evidence; transport, timeout, authentication,
 *     permission, 5xx, and protocol mismatches stay unknown. A
 *     structured `thread_not_found` from the bound Runner is the only
 *     `definitely-missing` evidence Mohist recognizes.
 *   - Missing recovery creates a fresh Thread via `thread/start` and
 *     never reuses the previous transcript. The Thread ID returned
 *     here is the new binding identity; the previous Thread is
 *     orphaned under its own fence.
 *
 * The runtime owns the app-server DTO construction. Callers pass
 * only Mohist-owned shapes.
 */

import {
  isCodexThreadStartRequest,
  isCodexThreadStartResult,
  isCodexThreadResumeRequest,
  type CodexThreadStartParams,
  type CodexThreadStartResult,
  type CodexThreadResumeParams,
  type CodexThreadResumeResult,
  CODEX_APPROVAL_POLICY,
  CODEX_SANDBOX_POLICY,
} from './protocol-types.js'
import {
  normalizeMissingSessionCodex,
  normalizeTurnFailedCodex,
  normalizeUnknownCodex,
  normalizeCodexProviderError,
} from './errors.js'
import { redactCodexCredentialString } from './credential.js'
import type { CodexCanonicalReasoningEffort, CodexDiagnostic, CodexResult } from './types.js'
import { mapCodexCanonicalReasoningEffort } from './model-catalog.js'

/**
 * Narrow app-server handle surface used by the Thread lifecycle.
 * The runtime owns the lifecycle of this handle; the lifecycle never
 * sends a method outside the locked v2 subset, never retries a lost
 * response, and never constructs idempotency keys.
 */
export interface CodexThreadTransport {
  send<P, R>(request: { readonly method: string; readonly params?: P; readonly id: number }): Promise<R>
  hasExited?(): boolean
}

export interface CodexThreadStartInput {
  readonly workDir: string
  /**
   * Resolved model identifier. The runtime submits the exact catalog
   * id; `null` means "no model override — let the server pick the
   * session default".
   */
  readonly model: string | null
  /**
   * Canonical reasoning effort (Mohist vocabulary). Mapped to the
   * native Codex spelling immediately before submission. `null`
   * means "no effort override".
   */
  readonly reasoningEffort: CodexCanonicalReasoningEffort | null
}

export interface CodexThreadStartOutcome {
  readonly threadId: string
  readonly workDir: string
  readonly diagnostics: readonly CodexDiagnostic[]
}

/**
 * Run `thread/start` on the bound app-server. The parameters carry
 * the immutable working directory, the locked trust settings, the
 * resolved model, and the canonical Reasoning Effort — Mohist never
 * sends a caller-supplied idempotency key.
 *
 * Failure modes (normalized to existing Mohist kinds):
 *   - structured `thread_not_found` ⇒ `missing-session` (only when
 *     raised against an existing binding — see `resumeThread`).
 *   - response shape outside the locked v2 subset ⇒ `unknown` with
 *     a structured diagnostic because creation may already have taken effect.
 *   - transport / timeout / 5xx / auth / permission / protocol
 *     mismatches ⇒ `unknown` with the underlying message in redacted
 *     diagnostics; `thread/start` itself never reports `missing-session`.
 */
export async function startThread(
  transport: CodexThreadTransport,
  input: CodexThreadStartInput,
  nextRequestId: number,
): Promise<CodexResult<CodexThreadStartOutcome>> {
  const params = buildThreadStartParams(input)
  const id = assertPositiveRequestId(nextRequestId)
  let response: unknown
  try {
    response = await transport.send<CodexThreadStartParams, unknown>({
      id,
      method: 'thread/start',
      params,
    })
  } catch (cause) {
    const providerError = normalizeCodexProviderError(cause, 'thread/start')
    if (providerError) return { ok: false, error: providerError, diagnostics: providerError.diagnostics }
    const message = cause instanceof Error ? redactCodexCredentialString(cause.message) : 'unknown transport failure'
    const error = normalizeUnknownCodex(
      `thread/start transport failed before the response was observed; outcome is unknown: ${message}`,
    )
    return { ok: false, error, diagnostics: error.diagnostics }
  }
  if (transport.hasExited?.()) {
    const error = normalizeUnknownCodex(
      'thread/start response is unknown because the child exited before it was observed',
    )
    return { ok: false, error, diagnostics: error.diagnostics }
  }
  const result = threadStartResult(response)
  if (result === null) {
    const error = normalizeUnknownCodex('thread/start response is unknown because its shape could not be verified')
    return { ok: false, error, diagnostics: error.diagnostics }
  }
  if (!isCodexThreadStartRequest({ jsonrpc: '2.0', id, method: 'thread/start', params })) {
    // The params we sent were shape-validated above; this branch is
    // a defensive guard so the runtime never admits a request whose
    // outbound envelope would not be parseable by a strict consumer.
    const error = normalizeTurnFailedCodex('thread/start params failed the locked v2 envelope check')
    return { ok: false, error, diagnostics: error.diagnostics }
  }
  const diagnostics: CodexDiagnostic[] = []
  if (result.cwd !== input.workDir) {
    diagnostics.push({
      severity: 'warning',
      code: 'thread-cwd-mismatch',
      message: redactCodexCredentialString(
        `thread/start returned cwd=${result.cwd}; expected the immutable ${input.workDir}`,
      ),
    })
  }
  return {
    ok: true,
    value: { threadId: result.threadId, workDir: result.cwd, diagnostics },
    diagnostics,
  }
}

/**
 * Run `thread/resume` against the bound Runner only. The bound
 * Runner is the producer of `thread_not_found` evidence; only a
 * structured `thread_not_found` from this call on the bound Runner
 * is `definitely-missing`.
 *
 * `excludeTurns: true` is set so a resumed Thread does not silently
 * replay history into the active Turn correlation. The runtime
 * never constructs a new Turn from the resumed transcript.
 */
export async function resumeThread(
  transport: CodexThreadTransport,
  threadId: string,
  workDir: string,
  nextRequestId: number,
): Promise<CodexResult<CodexThreadStartOutcome>> {
  const params: CodexThreadResumeParams = {
    threadId,
    cwd: workDir,
    excludeTurns: true,
  }
  const id = assertPositiveRequestId(nextRequestId)
  let response: unknown
  try {
    response = await transport.send<CodexThreadResumeParams, unknown>({
      id,
      method: 'thread/resume',
      params,
    })
  } catch (cause) {
    // A real JSON-RPC transport may surface the structured error through
    // its rejection channel. Preserve the one provider-owned missing
    // signal while keeping all other transport failures unknown.
    if (isStructuredThreadNotFound({ error: cause })) {
      const error = normalizeMissingSessionCodex([
        {
          severity: 'error',
          code: 'thread-not-found',
          message: redactCodexCredentialString(
            `thread/resume on the bound Runner returned structured thread_not_found for ${threadId}`,
          ),
        },
      ])
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    const providerError = normalizeCodexProviderError(cause, 'thread/resume')
    if (providerError) return { ok: false, error: providerError, diagnostics: providerError.diagnostics }
    const message = cause instanceof Error ? redactCodexCredentialString(cause.message) : 'unknown transport failure'
    const error = normalizeUnknownCodex(`thread/resume outcome is unknown after transport failure: ${message}`)
    return { ok: false, error, diagnostics: error.diagnostics }
  }
  if (transport.hasExited?.()) {
    const error = normalizeUnknownCodex(
      'thread/resume outcome is unknown because the child exited before the response was observed',
    )
    return { ok: false, error, diagnostics: error.diagnostics }
  }
  const resume = threadResumeResult(response)
  if (resume !== null) {
    const diagnostics: CodexDiagnostic[] = []
    if (resume.cwd !== workDir) {
      diagnostics.push({
        severity: 'warning',
        code: 'thread-cwd-mismatch',
        message: redactCodexCredentialString(
          `thread/resume returned cwd=${resume.cwd}; expected the immutable ${workDir}`,
        ),
      })
    }
    if (resume.threadId !== threadId) {
      diagnostics.push({
        severity: 'warning',
        code: 'thread-id-mismatch',
        message: redactCodexCredentialString(
          `thread/resume returned threadId=${resume.threadId}; expected the bound ${threadId}`,
        ),
      })
    }
    return {
      ok: true,
      value: {
        threadId: resume.threadId,
        workDir: resume.cwd,
        diagnostics,
      },
      diagnostics,
    }
  }
  const rawEnvelope = responseEnvelopeForError(response)
  if (isStructuredThreadNotFound(rawEnvelope)) {
    const error = normalizeMissingSessionCodex([
      {
        severity: 'error',
        code: 'thread-not-found',
        message: redactCodexCredentialString(
          `thread/resume on the bound Runner returned structured thread_not_found for ${threadId}`,
        ),
      },
    ])
    return { ok: false, error, diagnostics: error.diagnostics }
  }
  const error = normalizeUnknownCodex(
    'thread/resume outcome is unknown because its response shape could not be verified',
  )
  return { ok: false, error, diagnostics: error.diagnostics }
}

/**
 * Build the `thread/start` parameter object. The locked trust
 * settings, non-ephemeral history, resolved model, and canonical
 * Reasoning Effort are applied here. The runtime submits no
 * caller-supplied idempotency key.
 */
function buildThreadStartParams(input: CodexThreadStartInput): CodexThreadStartParams {
  const nativeEffort = mapCodexCanonicalReasoningEffort(input.reasoningEffort)
  const params: CodexThreadStartParams = {
    cwd: input.workDir,
    approvalPolicy: CODEX_APPROVAL_POLICY,
    sandbox: CODEX_SANDBOX_POLICY,
    ephemeral: false,
    persistHistory: true,
    ...(input.model !== null ? { model: input.model } : {}),
    ...(nativeEffort !== null ? { reasoningEffort: nativeEffort } : {}),
  }
  return params
}

function threadStartResult(response: unknown): CodexThreadStartResult | null {
  if (isCodexThreadStartResult(response)) return response.result
  const result = responseResult(response)
  if (!result || typeof result !== 'object') return null
  const view = result as { threadId?: unknown; cwd?: unknown; thread?: unknown }
  const thread =
    view.thread && typeof view.thread === 'object' ? (view.thread as { id?: unknown; cwd?: unknown }) : null
  const threadId =
    typeof view.threadId === 'string' ? view.threadId : thread && typeof thread.id === 'string' ? thread.id : null
  const cwd = typeof view.cwd === 'string' ? view.cwd : thread && typeof thread.cwd === 'string' ? thread.cwd : null
  return threadId && cwd ? { threadId, cwd } : null
}

function threadResumeResult(response: unknown): CodexThreadResumeResult | null {
  const result = responseResult(response)
  if (!result || typeof result !== 'object') return null
  const view = result as {
    threadId?: unknown
    cwd?: unknown
    model?: unknown
    reasoningEffort?: unknown
    thread?: unknown
  }
  const thread =
    view.thread && typeof view.thread === 'object' ? (view.thread as { id?: unknown; cwd?: unknown }) : null
  const threadId =
    typeof view.threadId === 'string' ? view.threadId : thread && typeof thread.id === 'string' ? thread.id : null
  const cwd = typeof view.cwd === 'string' ? view.cwd : thread && typeof thread.cwd === 'string' ? thread.cwd : null
  if (!threadId || !cwd) return null
  if (view.model !== undefined && typeof view.model !== 'string') return null
  if (view.reasoningEffort !== undefined && typeof view.reasoningEffort !== 'string') return null
  return {
    threadId,
    cwd,
    ...(view.model !== undefined ? { model: view.model } : {}),
    ...(view.reasoningEffort !== undefined ? { reasoningEffort: view.reasoningEffort } : {}),
  }
}

function responseResult(response: unknown): unknown {
  if (!response || typeof response !== 'object') return null
  const candidate = response as { result?: unknown }
  return 'result' in candidate ? candidate.result : response
}

function responseEnvelopeForError(response: unknown): {
  readonly jsonrpc?: unknown
  readonly id?: unknown
  readonly error?: unknown
  readonly result?: unknown
} | null {
  if (!response || typeof response !== 'object') return null
  return response as { jsonrpc?: unknown; id?: unknown; error?: unknown; result?: unknown }
}

/**
 * Detect the structured `thread_not_found` evidence from the bound
 * Runner. The matcher accepts the canonical error code, the
 * standardized message wording, and the optional codex-specific
 * `data.code` payload. Transport / timeout / 5xx / auth / permission
 * / protocol-mismatch envelopes do NOT match this predicate and
 * therefore stay unknown; the upper layers continue to surface
 * `thread_not_found` as the only `definitely-missing` evidence.
 */
export function isStructuredThreadNotFound(
  envelope: { readonly jsonrpc?: unknown; readonly error?: unknown; readonly result?: unknown } | null,
): boolean {
  if (!envelope) return false
  const error = envelope.error
  if (!error || typeof error !== 'object') return false
  const view = error as { code?: unknown; message?: unknown; data?: unknown }
  if (view.code === 'thread_not_found' || view.code === 404) return true
  if (typeof view.message === 'string' && /\bthread[_\s-]?not[_\s-]?found\b/i.test(view.message)) return true
  const data = view.data
  if (data && typeof data === 'object') {
    const dataView = data as { code?: unknown }
    if (dataView.code === 'thread_not_found') return true
  }
  return false
}

function assertPositiveRequestId(value: number): number {
  if (!Number.isSafeInteger(value) || value <= 0) {
    throw new Error(`request id must be a positive safe integer (received ${value})`)
  }
  return value
}

/**
 * Convenience for tests: build a `thread/start` JSON-RPC envelope
 * with the locked parameter shape. Exported so the integration test
 * seam can assert outbound envelopes without importing the protocol
 * types directly into the runtime boundary.
 */
export function buildCodexThreadStartRequest(input: {
  readonly workDir: string
  readonly model?: string | null
  readonly reasoningEffort?: CodexCanonicalReasoningEffort | null
  readonly id: number
}): { readonly method: 'thread/start'; readonly params: CodexThreadStartParams; readonly id: number } {
  return {
    id: assertPositiveRequestId(input.id),
    method: 'thread/start',
    params: buildThreadStartParams({
      workDir: input.workDir,
      model: input.model ?? null,
      reasoningEffort: input.reasoningEffort ?? null,
    }),
  }
}

/**
 * Convenience for tests: build a `thread/resume` JSON-RPC envelope
 * with `excludeTurns: true`. Used by the integration test seam to
 * confirm the bound-Runner-only policy and the no-history-replay
 * guarantee.
 */
export function buildCodexThreadResumeRequest(input: {
  readonly threadId: string
  readonly workDir: string
  readonly id: number
}): { readonly method: 'thread/resume'; readonly params: CodexThreadResumeParams; readonly id: number } {
  return {
    id: assertPositiveRequestId(input.id),
    method: 'thread/resume',
    params: {
      threadId: input.threadId,
      cwd: input.workDir,
      excludeTurns: true,
    },
  }
}

// Internal smoke: ensure the resume helper continues to round-trip
// through the locked protocol-type predicate. This branch is a
// belt-and-braces guard so a future protocol-type refactor that
// drops `excludeTurns` cannot silently break the resume path.
if (typeof isCodexThreadResumeRequest === 'function') {
  const probeEnvelope = {
    jsonrpc: '2.0' as const,
    id: 1,
    method: 'thread/resume',
    params: { threadId: 'thr_1', cwd: '/work', excludeTurns: true },
  }
  if (!isCodexThreadResumeRequest(probeEnvelope)) {
    throw new Error('thread/resume envelope probe failed: excludeTurns is not preserved by the locked predicate')
  }
}
