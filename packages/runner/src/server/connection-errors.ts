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
