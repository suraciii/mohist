export type RunnerTransportErrorKind = 'cancelled' | 'network' | 'http' | 'protocol'

export interface RunnerTransportErrorOptions {
  operation: string
  kind: RunnerTransportErrorKind
  httpStatus?: number
  serverCode?: string
  safeMessage: string
  cause?: unknown
}

export class RunnerTransportError extends Error {
  readonly operation: string
  readonly kind: RunnerTransportErrorKind
  readonly httpStatus?: number
  readonly serverCode?: string
  readonly safeMessage: string
  readonly cause?: unknown

  constructor(options: RunnerTransportErrorOptions) {
    super(options.safeMessage)
    this.name = 'RunnerTransportError'
    this.operation = options.operation
    this.kind = options.kind
    this.httpStatus = options.httpStatus
    this.serverCode = options.serverCode
    this.safeMessage = options.safeMessage
    if (options.cause !== undefined) this.cause = options.cause
    Object.freeze(this)
  }
}

export function extractErrorMessage(payload: Record<string, unknown> | null, fallback: string): string | null {
  if (!payload) return null
  const data = readRecord(payload.data)
  if (data && typeof data.message === 'string') return data.message
  if (typeof payload.error === 'string') return payload.error
  return null
}

function readRecord(value: unknown): Record<string, unknown> | null {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : null
}
export interface RuntimeEventDeliveryErrorMetadata {
  readonly status: number
  readonly code: string | null
}

/**
 * HTTP failure returned by a runtime-event endpoint. The Server's structured
 * ApiResponse.Code is kept separate from the human-readable message so
 * delivery policy does not need to inspect exception text.
 */
export class RuntimeEventDeliveryError extends Error implements RuntimeEventDeliveryErrorMetadata {
  readonly status: number
  readonly code: string | null

  constructor(operation: string, status: number, code: string | null, responseBody: string) {
    super(`${operation} failed: ${status}${responseBody ? ` ${responseBody}` : ''}`)
    this.name = 'RuntimeEventDeliveryError'
    this.status = status
    this.code = code
  }
}
