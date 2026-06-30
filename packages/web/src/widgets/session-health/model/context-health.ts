/**
 * Compute a traffic-light health status from a context-window usage
 * percentage.
 *
 * Thresholds match the backend `ContextHealthClassifier`:
 *   - green  : < 60%
 *   - yellow : 60% – 79.99%
 *   - red    : >= 80%
 *
 * Returns `null` when usage cannot be derived (missing data, non-positive
 * window size, or non-finite values) so callers can hide the indicator
 * entirely instead of guessing.
 */
export function classifyContextHealth(usagePercent: number | null | undefined): ContextHealthStatus | null {
  if (usagePercent == null || !Number.isFinite(usagePercent)) return null
  if (usagePercent >= 80) return 'red'
  if (usagePercent >= 60) return 'yellow'
  return 'green'
}

export type ContextHealthStatus = 'green' | 'yellow' | 'red'

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
