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
