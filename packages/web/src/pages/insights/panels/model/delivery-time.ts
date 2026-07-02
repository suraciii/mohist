/**
 * Trailing rolling issue-count window (fixed, not user-configurable).
 * The percentile line plots one value per delivered issue, computed over
 * the last N issues ending at and including that position.
 */
export const ROLLING_WINDOW = 10

/**
 * A per-issue sample whose `value` is the duration for the active lens
 * (lead time or cycle time). `null` means the duration is undefined
 * for the current lens (e.g. cycle time without a recorded work-start).
 */
export interface RollingPercentileSample {
  value: number | null
}

/**
 * P50 uses the conventional median: for an odd-sized sample the middle
 * value, for an even-sized sample the arithmetic mean of the two middle
 * values.
 */
export const P50_MEDIAN = 0.5

/**
 * P85 uses linear interpolation between the two closest ranks for
 * fractional indices (the conventional percentile convention).
 */
export const P85_LINEAR_INTERPOLATION = 0.85

function percentileOfSorted(sortedValues: number[], p: number): number | null {
  if (sortedValues.length === 0) return null
  if (sortedValues.length === 1) return sortedValues[0]

  if (p === P50_MEDIAN) {
    const mid = sortedValues.length / 2
    const lowerIndex = Math.floor(mid)
    if (sortedValues.length % 2 === 1) {
      return sortedValues[lowerIndex]
    }
    return (sortedValues[lowerIndex - 1] + sortedValues[lowerIndex]) / 2
  }

  const rank = p * (sortedValues.length - 1)
  const lowerIndex = Math.floor(rank)
  const upperIndex = Math.min(lowerIndex + 1, sortedValues.length - 1)
  if (lowerIndex === upperIndex) return sortedValues[lowerIndex]
  const fraction = rank - lowerIndex
  return sortedValues[lowerIndex] * (1 - fraction) + sortedValues[upperIndex] * fraction
}

/**
 * Compute a rolling-percentile series over the per-issue delivery
 * samples ordered by completion date (the caller is responsible for
 * the ordering — the surface returns points in `CompletedAt` ascending
 * order already). For each position `i` the helper takes
 * `samples[max(0, i - window + 1) .. i]`, drops `null`-valued entries
 * (undefined durations for the current lens), and emits the percentile
 * value of the remaining set, or `null` when no valid value exists
 * within the trailing window at that position.
 *
 * The output array is parallel to the input (same length): a `null`
 * entry means "no plottable percentile at this position".
 */
export function computeRollingPercentile<T extends RollingPercentileSample>(
  samples: readonly T[],
  window: number = ROLLING_WINDOW,
  p: number = P50_MEDIAN,
): (number | null)[] {
  if (samples.length === 0) return []
  if (window <= 0) return samples.map(() => null)

  const result: (number | null)[] = []

  for (let i = 0; i < samples.length; i++) {
    const start = Math.max(0, i - window + 1)
    const slice: number[] = []
    for (let j = start; j <= i; j++) {
      const v = samples[j].value
      if (v !== null) slice.push(v)
    }

    if (slice.length === 0) {
      result.push(null)
      continue
    }

    slice.sort((a, b) => a - b)
    result.push(percentileOfSorted(slice, p))
  }

  return result
}
