import type { PiDiagnostic, PiError, PiErrorKind } from './types.js'

export function piError(kind: PiErrorKind, message: string, diagnostics: readonly PiDiagnostic[] = []): PiError {
  return { kind, message, diagnostics }
}

export function diagnostic(
  code: string,
  message: string,
  severity: PiDiagnostic['severity'] = 'error',
  details?: Record<string, unknown>,
): PiDiagnostic {
  return { severity, code, message, ...(details ? { details } : {}) }
}

export function failureDiagnostic(
  code: string,
  cause: unknown,
  mask: (text: string) => string,
  details: Record<string, unknown> = {},
): PiDiagnostic {
  return diagnostic(code, mask(errorMessage(cause)), 'error', {
    ...details,
    ...errorDetails(cause, mask, 0),
  })
}

function errorMessage(cause: unknown): string {
  if (cause && typeof cause === 'object' && 'message' in cause && typeof cause.message === 'string')
    return cause.message || 'Pi operation failed'
  return typeof cause === 'string' ? cause : 'Pi operation failed'
}

function errorDetails(cause: unknown, mask: (text: string) => string, depth: number): Record<string, unknown> {
  if (!cause || typeof cause !== 'object') return {}
  const source = cause as Record<string, unknown>
  const details: Record<string, unknown> = {}
  // SDK errors can contain request headers and credentials; only portable
  // failure fields cross the runtime boundary.
  for (const key of ['name', 'message', 'code', 'status', 'statusCode']) {
    const value = source[key]
    if (typeof value === 'string') details[key] = mask(value)
    else if (typeof value === 'number' && Number.isFinite(value)) details[key] = value
  }
  if (depth < 2 && source.cause !== undefined) {
    const nested = errorDetails(source.cause, mask, depth + 1)
    if (Object.keys(nested).length > 0) details.cause = Object.freeze(nested)
    else if (typeof source.cause === 'string') details.cause = mask(source.cause)
  }
  return details
}

export function resetDiagnostic(): PiDiagnostic {
  return diagnostic('missing-session', 'Issue a Reset to establish a fresh Pi Session, then retry')
}
