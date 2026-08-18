/**
 * SDK error normalization for the OpenCode runtime.
 *
 * At the module boundary, every SDK failure maps to one of a small
 * set of Mohist `RuntimeErrorKind` values. Provider-specific detail
 * is carried only as `RuntimeDiagnostic` records, never as an output
 * field. The runtime does not introduce a global Workflow error enum.
 *
 * OpenCode is authoritative on permissions. The runtime responds once to
 * requests that OpenCode classifies as ask, and never creates a Workflow
 * Approval or changes saved OpenCode permission rules.
 */

import type { RuntimeDiagnostic, RuntimeError, RuntimeErrorKind } from './types.js'

export interface RawSdkError {
  readonly name?: string
  readonly message: string
  readonly status?: number
  readonly code?: string
  readonly service?: string
  readonly cause?: unknown
}

const DEFAULT_NON_RECOVERABLE_MESSAGE_PATTERNS: readonly RegExp[] = [
  /quota/i,
  /credit/i,
  /billing/i,
  /usage[ -]?limit(?:\s+(?:reached|exceeded))?/i,
  /payment[ -]required/i,
  /insufficient[ _-]?balance/i,
  /额度/i,
  /余额/i,
  /计费/i,
  /欠费/i,
  /使用上限/i,
  /已达到[^。]*(?:限额|上限)/i,
  /限额[^。]*(?:重置|恢复)/i,
]

const NON_RECOVERABLE_ACTION_REASONS = new Set([
  'account_rate_limit',
  'free_tier_limit',
  'quota_exhausted',
  'usage_limit',
])

const TRANSPORT_ERROR_CODES = new Set([
  'UND_ERR_HEADERS_TIMEOUT',
  'UND_ERR_BODY_TIMEOUT',
  'UND_ERR_SOCKET',
  'UND_ERR_CONNECT_TIMEOUT',
  'ECONNRESET',
  'ECONNREFUSED',
  'EPIPE',
])

export const DEFAULT_PROVIDER_ERROR_POLICY = {
  nonRecoverablePatterns: DEFAULT_NON_RECOVERABLE_MESSAGE_PATTERNS,
  consecutiveRetryThreshold: 5,
} as const

export function isNonRecoverableProviderMessage(
  message: string,
  patterns: readonly RegExp[] = DEFAULT_NON_RECOVERABLE_MESSAGE_PATTERNS,
): boolean {
  return patterns.some((pattern) => pattern.test(message))
}

export function isNonRecoverableProviderRetry(
  retry: { readonly message: string; readonly action?: { readonly reason?: string } },
  patterns: readonly RegExp[] = DEFAULT_NON_RECOVERABLE_MESSAGE_PATTERNS,
): boolean {
  const reason = retry.action?.reason?.toLowerCase()
  return (
    (reason !== undefined && NON_RECOVERABLE_ACTION_REASONS.has(reason)) ||
    isNonRecoverableProviderMessage(retry.message, patterns)
  )
}

export function normalizePermissionRequired(diagnostics: readonly RuntimeDiagnostic[] = []): RuntimeError {
  return {
    kind: 'permission-required',
    message: 'OpenCode permission request could not be answered by the headless runtime',
    diagnostics: [
      ...diagnostics,
      {
        severity: 'error',
        code: 'permission-required',
        message: 'Restore OpenCode connectivity, then retry the task',
      },
    ],
  }
}

export function normalizeInterrupted(diagnostics: readonly RuntimeDiagnostic[] = []): RuntimeError {
  return {
    kind: 'interrupted',
    message: 'OpenCode turn was interrupted before completion',
    diagnostics: [
      ...diagnostics,
      { severity: 'info', code: 'interrupted', message: 'The turn was aborted by a deadline or explicit cancel' },
    ],
  }
}

export function normalizeGenerationDrainTimeout(
  timeoutMs: number,
  diagnostics: readonly RuntimeDiagnostic[] = [],
): RuntimeError {
  return {
    kind: 'generation-drain-timeout',
    message: `OpenCode runtime generation was forcibly released after the ${timeoutMs}ms drain deadline`,
    diagnostics: [
      ...diagnostics,
      {
        severity: 'error',
        code: 'generation-drain-timeout',
        message: 'The active turn was failed because its quarantined runtime generation did not drain in time',
        details: { timeoutMs },
      },
    ],
  }
}

export function normalizeDeadlineExceeded(
  deadlineMs: number,
  diagnostics: readonly RuntimeDiagnostic[] = [],
): RuntimeError {
  const seconds = deadlineMs / 1000
  return {
    kind: 'deadline-exceeded',
    message: `OpenCode turn timed out after ${seconds}s`,
    diagnostics: [
      ...diagnostics,
      { severity: 'error', code: 'deadline-exceeded', message: `The runner deadline expired after ${seconds}s` },
    ],
  }
}

export function normalizeMissingSession(diagnostics: readonly RuntimeDiagnostic[] = []): RuntimeError {
  return {
    kind: 'missing-session',
    message:
      'No current OpenCode Runtime Session is bound to this logical AgentSession — issue a Reset to establish a fresh Runtime Session, then retry',
    diagnostics: [
      ...diagnostics,
      {
        severity: 'error',
        code: 'missing-session',
        message: 'Issue a Reset to establish a fresh Runtime Session, then retry',
      },
    ],
  }
}

export function normalizeUnavailableRuntime(diagnostics: readonly RuntimeDiagnostic[] = []): RuntimeError {
  return {
    kind: 'unavailable-runtime',
    message: 'OpenCode runtime is not available',
    diagnostics: [
      ...diagnostics,
      {
        severity: 'error',
        code: 'unavailable-runtime',
        message: 'Wait for readiness to re-pass, or investigate the readiness diagnostic for recovery steps',
      },
    ],
  }
}

export function normalizeIncompatibleRuntime(diagnostics: readonly RuntimeDiagnostic[] = []): RuntimeError {
  return {
    kind: 'incompatible-runtime',
    message: 'Installed OpenCode is incompatible with the pinned SDK surface',
    diagnostics: [
      ...diagnostics,
      {
        severity: 'error',
        code: 'incompatible-runtime',
        message: 'Update OpenCode to a version that matches the pinned @opencode-ai/sdk package',
      },
    ],
  }
}

/**
 * Failure category carried by the `unsupported-execution-configuration`
 * rejection. The value is a Mohist failure category (snake case, as
 * recorded by the Server), not a runtime-internal kind: the runner
 * projections surface it verbatim so AgentJob/Workflow failures land
 * in the EventCatalog category the capability contract specifies.
 */
export const UNSUPPORTED_EXECUTION_CONFIGURATION_CATEGORY = 'unsupported_execution_configuration'

/**
 * OpenCode is a variant-only runtime: upstream has no reasoning-effort
 * surface, so an explicit `options.reasoningEffort` is a configuration
 * failure. The effort is never appended to the model id, written to
 * the variant, or silently ignored — the turn fails before any
 * provider interaction (design D6 / issue-557).
 */
export function normalizeUnsupportedExecutionConfiguration(effort: string): RuntimeError {
  const detail = `options.reasoningEffort '${effort}' is not supported by the OpenCode runtime; it was not applied to the model, the variant, or ignored`
  return {
    kind: 'unsupported-execution-configuration',
    message:
      'OpenCode does not support a reasoning effort; remove the reasoning effort or select a runtime that supports it',
    diagnostics: [{ severity: 'error', code: UNSUPPORTED_EXECUTION_CONFIGURATION_CATEGORY, message: detail }],
  }
}

/**
 * Shared option validation for the turn and follow-up entry points:
 * returns the execution-configuration failure when the options carry
 * an explicit reasoning effort, `null` when the effort is unset
 * (absent, null, or empty — unset imposes no requirement).
 */
export function unsupportedReasoningEffortError(
  options: { readonly reasoningEffort?: unknown } | null | undefined,
): RuntimeError | null {
  const effort = options?.reasoningEffort
  if (effort === undefined || effort === null) return null
  if (typeof effort !== 'string') return normalizeInvalidInput('options.reasoningEffort must be a string when present')
  if (effort.length === 0) return null
  return normalizeUnsupportedExecutionConfiguration(effort)
}

export function normalizeInvalidInput(message: string, diagnostics: readonly RuntimeDiagnostic[] = []): RuntimeError {
  return {
    kind: 'invalid-input',
    message,
    diagnostics: [...diagnostics, { severity: 'error', code: 'invalid-input', message }],
  }
}

export function normalizeTurnFailed(
  raw: RawSdkError | string,
  diagnostics: readonly RuntimeDiagnostic[] = [],
): RuntimeError {
  const message = typeof raw === 'string' ? raw : raw.message || 'OpenCode turn failed'
  const transportCode = transportErrorCode(raw)
  return {
    kind: 'turn-failed',
    message: transportCode
      ? `OpenCode local transport failed (${transportCode}); confirm the local runtime is healthy, then retry`
      : message,
    diagnostics: [
      ...diagnostics,
      {
        severity: 'error',
        code: transportCode ? 'opencode-transport-failed' : 'turn-failed',
        message: transportCode ? `OpenCode local transport failed with ${transportCode}: ${message}` : message,
        details: typeof raw === 'string' ? undefined : { ...raw },
      },
    ],
  }
}

export function isTransportFailure(cause: unknown): boolean {
  return (
    transportErrorCode(cause) !== undefined ||
    (cause instanceof Error && /fetch failed|network error/i.test(cause.message))
  )
}

const UNCONFIRMED_CLEANUP_CODES = new Set(['abort-unconfirmed', 'abort-cleanup-timeout', 'status-cleanup-timeout'])

export function hasUnconfirmedCleanup(diagnostics: readonly RuntimeDiagnostic[] = []): boolean {
  return diagnostics.some((diagnostic) => UNCONFIRMED_CLEANUP_CODES.has(diagnostic.code))
}

function transportErrorCode(value: unknown): string | undefined {
  let current = value
  for (let depth = 0; depth < 4 && current && typeof current === 'object'; depth++) {
    const code = (current as { code?: unknown }).code
    if (typeof code === 'string' && TRANSPORT_ERROR_CODES.has(code)) return code
    current = (current as { cause?: unknown }).cause
  }
  return undefined
}

export function normalizeAbortUnconfirmed(
  message: string,
  diagnostics: readonly RuntimeDiagnostic[] = [],
  diagnosticCode: 'abort-unconfirmed' | 'abort-cleanup-timeout' | 'status-cleanup-timeout' = 'abort-unconfirmed',
): RuntimeError {
  return {
    kind: 'turn-failed',
    message: `OpenCode turn could not be confirmed stopped: ${message}`,
    diagnostics: [...diagnostics, { severity: 'error', code: diagnosticCode, message }],
  }
}

export function errorKindFor(raw: RawSdkError | string): RuntimeErrorKind {
  if (typeof raw !== 'string') {
    if (raw.status === 404 || /not[ _-]?found/i.test(raw.message)) return 'missing-session'
    if (raw.status === 403 || /permission/i.test(raw.message)) return 'permission-required'
    if (raw.status === 400 || /invalid/i.test(raw.message)) return 'invalid-input'
    if (raw.code === 'incompatible') return 'incompatible-runtime'
    if (raw.service === 'opencode.health' || raw.code === 'unavailable') return 'unavailable-runtime'
  }
  return 'turn-failed'
}
