/**
 * Verdict model for the Signal Summary. Pure derivation layer — no React,
 * no I/O. Input: the four metrics surfaces' DTOs. Output: a normalized
 * `Verdict` discriminated union.
 *
 * Discriminated union (D5):
 *   - `full`:           current-window value AND previous-window baseline
 *                       available; carries direction + magnitude.
 *   - `currentOnly`:    current-window value present, no previous window;
 *                       hides trend/magnitude.
 *   - `insufficient`:   no current-window samples; verdict is "数据不足".
 *
 * Components must NOT branch on SampleCount directly — they only render
 * from this Verdict union.
 */

export type VerdictDirection = 'up' | 'down' | 'flat'

export type VerdictPolarity = 'up-favorable' | 'down-favorable'

export interface VerdictBase {
  /**
   * Display-friendly sentence start (conclusion first).
   * e.g. "产出在变快", "质量需关注", "投入信号".
   */
  label: string
}

export interface FullVerdict extends VerdictBase {
  kind: 'full'
  direction: VerdictDirection
  magnitude: number
  unit: 'count' | 'percent' | 'percentagePoints' | 'currency'
  /**
   * Per-dimension polarity, encoded so components can color the arrow
   * correctly without per-component special-casing.
   */
  polarity: VerdictPolarity
}

export interface CurrentOnlyVerdict extends VerdictBase {
  kind: 'currentOnly'
}

export interface InsufficientVerdict extends VerdictBase {
  kind: 'insufficient'
}

export type Verdict = FullVerdict | CurrentOnlyVerdict | InsufficientVerdict

/**
 * Relative tolerance floor for double-valued verdicts. Below this ratio
 * `|cur - prev| / max(|prev|, eps)` we treat the values as equal (flat)
 * to avoid float-jitter arrows. Pinned here so tests can lock the
 * behavior.
 */
export const DOUBLE_RELATIVE_TOLERANCE = 1e-9

/**
 * Small absolute floor used by the denominator when computing relative
 * differences. Prevents blow-ups when the previous value is 0.
 */
export const EPSILON_FLOOR = 1e-12

/**
 * Compute the relative change `|cur - prev| / max(|prev|, EPSILON_FLOOR)`.
 * Doubles use this against `DOUBLE_RELATIVE_TOLERANCE` to decide flatness.
 */
export function relativeDelta(current: number, previous: number): number {
  const denom = Math.max(Math.abs(previous), EPSILON_FLOOR)
  return Math.abs(current - previous) / denom
}

/**
 * Decide whether two doubles are "equal within tolerance" for verdict
 * direction. Counts use integer equality; doubles use the relative floor.
 */
export function isFlatDouble(current: number, previous: number): boolean {
  if (current === previous) return true
  return relativeDelta(current, previous) < DOUBLE_RELATIVE_TOLERANCE
}

export function directionForDoubles(
  current: number,
  previous: number,
): VerdictDirection {
  if (isFlatDouble(current, previous)) return 'flat'
  return current > previous ? 'up' : 'down'
}

export function directionForCounts(current: number, previous: number): VerdictDirection {
  if (current === previous) return 'flat'
  return current > previous ? 'up' : 'down'
}

/**
 * A "trend is favorable?" helper keyed off direction + polarity.
 * Encodes the per-dimension rules from D6:
 *   - throughput: up-favorable
 *   - delivery:   down-favorable (faster)
 *   - quality:    up-favorable
 *   - investment: down-favorable (cheaper)
 */
export function isFavorable(
  direction: VerdictDirection,
  polarity: VerdictPolarity,
): boolean {
  if (direction === 'flat') return true
  return polarity === 'up-favorable'
    ? direction === 'up'
    : direction === 'down'
}