/**
 * SDK error normalization for the OpenCode runtime.
 *
 * At the module boundary, every SDK failure maps to one of a small
 * set of Mohist `RuntimeErrorKind` values. Provider-specific detail
 * is carried only as `RuntimeDiagnostic` records, never as an output
 * field. The runtime does not introduce a global Workflow error enum
 * (see design D8 / `specs/opencode-runtime/spec.md`).
 *
 * The runtime is authoritative on permissions: it never auto-approves
 * and never creates a Workflow Approval.
 */

import type { RuntimeDiagnostic, RuntimeError, RuntimeErrorKind } from "./types.js"

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
  "account_rate_limit",
  "free_tier_limit",
  "quota_exhausted",
  "usage_limit",
])

export const DEFAULT_PROVIDER_ERROR_POLICY = {
  nonRecoverablePatterns: DEFAULT_NON_RECOVERABLE_MESSAGE_PATTERNS,
  consecutiveRetryThreshold: 5,
} as const

export function isNonRecoverableProviderMessage(message: string, patterns: readonly RegExp[] = DEFAULT_NON_RECOVERABLE_MESSAGE_PATTERNS): boolean {
  return patterns.some((pattern) => pattern.test(message))
}

export function isNonRecoverableProviderRetry(
  retry: { readonly message: string; readonly action?: { readonly reason?: string } },
  patterns: readonly RegExp[] = DEFAULT_NON_RECOVERABLE_MESSAGE_PATTERNS,
): boolean {
  const reason = retry.action?.reason?.toLowerCase()
  return (reason !== undefined && NON_RECOVERABLE_ACTION_REASONS.has(reason))
    || isNonRecoverableProviderMessage(retry.message, patterns)
}

export function normalizePermissionRequired(diagnostics: readonly RuntimeDiagnostic[] = []): RuntimeError {
  return {
    kind: "permission-required",
    message: "OpenCode interactive permission request cannot be satisfied in a headless Workflow turn",
    diagnostics: [
      ...diagnostics,
      {
        severity: "error",
        code: "permission-required",
        message: "Approve the request in the OpenCode configuration or grant the permission out-of-band, then retry",
      },
    ],
  }
}

export function normalizeInterrupted(diagnostics: readonly RuntimeDiagnostic[] = []): RuntimeError {
  return {
    kind: "interrupted",
    message: "OpenCode turn was interrupted before completion",
    diagnostics: [
      ...diagnostics,
      { severity: "info", code: "interrupted", message: "The turn was aborted by a deadline or explicit cancel" },
    ],
  }
}

export function normalizeMissingSession(diagnostics: readonly RuntimeDiagnostic[] = []): RuntimeError {
  return {
    kind: "missing-session",
    message: "No current OpenCode Runtime Session is bound to this logical AgentSession — issue a Reset to establish a fresh Runtime Session, then retry",
    diagnostics: [
      ...diagnostics,
      {
        severity: "error",
        code: "missing-session",
        message: "Issue a Reset to establish a fresh Runtime Session, then retry",
      },
    ],
  }
}

export function normalizeUnavailableRuntime(diagnostics: readonly RuntimeDiagnostic[] = []): RuntimeError {
  return {
    kind: "unavailable-runtime",
    message: "OpenCode runtime is not available",
    diagnostics: [
      ...diagnostics,
      {
        severity: "error",
        code: "unavailable-runtime",
        message: "Wait for readiness to re-pass, or investigate the readiness diagnostic for recovery steps",
      },
    ],
  }
}

export function normalizeIncompatibleRuntime(diagnostics: readonly RuntimeDiagnostic[] = []): RuntimeError {
  return {
    kind: "incompatible-runtime",
    message: "Installed OpenCode is incompatible with the pinned SDK surface",
    diagnostics: [
      ...diagnostics,
      {
        severity: "error",
        code: "incompatible-runtime",
        message: "Update OpenCode to a version that matches the pinned @opencode-ai/sdk package",
      },
    ],
  }
}

export function normalizeInvalidInput(message: string, diagnostics: readonly RuntimeDiagnostic[] = []): RuntimeError {
  return {
    kind: "invalid-input",
    message,
    diagnostics: [
      ...diagnostics,
      { severity: "error", code: "invalid-input", message },
    ],
  }
}

export function normalizeTurnFailed(raw: RawSdkError | string, diagnostics: readonly RuntimeDiagnostic[] = []): RuntimeError {
  const message = typeof raw === "string" ? raw : raw.message || "OpenCode turn failed"
  return {
    kind: "turn-failed",
    message: "OpenCode turn failed",
    diagnostics: [
      ...diagnostics,
      { severity: "error", code: "turn-failed", message, details: typeof raw === "string" ? undefined : { ...raw } },
    ],
  }
}

export function normalizeAbortUnconfirmed(message: string, diagnostics: readonly RuntimeDiagnostic[] = []): RuntimeError {
  return {
    kind: "turn-failed",
    message: "OpenCode turn could not be confirmed stopped",
    diagnostics: [
      ...diagnostics,
      { severity: "error", code: "abort-unconfirmed", message },
    ],
  }
}

export function errorKindFor(raw: RawSdkError | string): RuntimeErrorKind {
  if (typeof raw !== "string") {
    if (raw.status === 404 || /not[ _-]?found/i.test(raw.message)) return "missing-session"
    if (raw.status === 403 || /permission/i.test(raw.message)) return "permission-required"
    if (raw.status === 400 || /invalid/i.test(raw.message)) return "invalid-input"
    if (raw.code === "incompatible") return "incompatible-runtime"
    if (raw.service === "opencode.health" || raw.code === "unavailable") return "unavailable-runtime"
  }
  return "turn-failed"
}
