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
 * Resolve a usage percent using the same priority as the backend: prefer
 * an explicit percentage if provided, otherwise derive it from
 * `used / size` when the window is positive. Returns `null` when no
 * meaningful usage can be computed.
 */
export function resolveContextUsagePercent(snapshot: ContextUsageSnapshot | null | undefined): number | null {
  if (!snapshot) return null
  const explicit = snapshot.contextUsagePercent
  if (explicit != null && Number.isFinite(explicit)) {
    return clampPercent(explicit)
  }
  const used = snapshot.contextWindowUsed
  const size = snapshot.contextWindowSize
  if (used == null || size == null || !Number.isFinite(used) || !Number.isFinite(size) || size <= 0) {
    return null
  }
  return clampPercent((used / size) * 100)
}

/**
 * Resolve a usage snapshot the same way as `resolveContextUsagePercent`
 * but returns the absolute token counts along with the percentage so a
 * UI can render labels such as "450K / 1M tokens (45%)". A `null`
 * percentage means there is no usable context data — the UI should hide
 * the indicator in that case.
 */
export function resolveContextUsage(snapshot: ContextUsageSnapshot | null | undefined): {
  used: number | null
  size: number | null
  percent: number | null
  status: ContextHealthStatus | null
} {
  if (!snapshot) {
    return { used: null, size: null, percent: null, status: null }
  }
  const used = snapshot.contextWindowUsed ?? null
  const size = snapshot.contextWindowSize ?? null
  const percent = resolveContextUsagePercent(snapshot)
  const status = percent == null ? null : classifyContextHealth(percent)
  return { used, size, percent, status }
}

function clampPercent(value: number): number {
  if (!Number.isFinite(value)) return 0
  if (value < 0) return 0
  if (value > 100) return 100
  return value
}
