import { parseJsonSafely } from './transcript-tool-utils'

export function asRecord(value: unknown): Record<string, unknown> | null {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : null
}

export function asPayloadRecord(value: unknown): Record<string, unknown> | null {
  if (typeof value === 'string') return parseJsonSafely(value)
  return asRecord(value)
}

export function getNumber(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined
}

export function getString(value: unknown): string | undefined {
  return typeof value === 'string' && value ? value : undefined
}

export function truncatePreview(value: string, maxLength: number = 1000): string {
  return value.length > maxLength ? `${value.slice(0, maxLength)}...` : value
}
