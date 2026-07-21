import type { PiDiagnostic, PiError, PiErrorKind } from "./types.js"

export function piError(kind: PiErrorKind, message: string, diagnostics: readonly PiDiagnostic[] = []): PiError {
  return { kind, message, diagnostics }
}

export function diagnostic(code: string, message: string, severity: PiDiagnostic["severity"] = "error", details?: Record<string, unknown>): PiDiagnostic {
  return { severity, code, message, ...(details ? { details } : {}) }
}

export function resetDiagnostic(): PiDiagnostic {
  return diagnostic("missing-session", "Issue a Reset to establish a fresh Pi Session, then retry")
}
