export type ContextHealthStatus = 'green' | 'yellow' | 'red'

export function isContextHealthStatus(value: string | null | undefined): value is ContextHealthStatus {
  return value === 'green' || value === 'yellow' || value === 'red'
}

export interface ContextUsageSnapshot {
  contextWindowUsed?: number | null
  contextWindowSize?: number | null
  contextUsagePercent?: number | null
}

/**
 * Clamp a percentage value to the [0, 100] range. Non-finite values
 * return 0. This is a pure formatting utility; it does not derive or
 * recompute a percentage from raw window data.
 */
export function clampPercent(value: number): number {
  if (!Number.isFinite(value)) return 0
  if (value < 0) return 0
  if (value > 100) return 100
  return value
}
