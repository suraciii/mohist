import { describe, expect, it } from 'vitest'
import {
  DOUBLE_RELATIVE_TOLERANCE,
  directionForCounts,
  directionForDoubles,
  isFavorable,
  isFlatDouble,
  relativeDelta,
} from './verdict'

describe('verdict: relativeDelta', () => {
  it('returns 0 when both values are equal', () => {
    expect(relativeDelta(5, 5)).toBe(0)
    expect(relativeDelta(0, 0)).toBe(0)
  })

  it('returns a positive ratio for non-zero baselines', () => {
    expect(relativeDelta(6, 3)).toBeCloseTo(1, 12)
    expect(relativeDelta(4, 8)).toBeCloseTo(0.5, 12)
  })

  it('uses EPSILON_FLOOR when previous is zero (no blow-ups)', () => {
    const r = relativeDelta(1, 0)
    expect(Number.isFinite(r)).toBe(true)
    expect(r).toBeGreaterThan(0)
  })
})

describe('verdict: isFlatDouble', () => {
  it('returns true for exactly-equal doubles', () => {
    expect(isFlatDouble(1.5, 1.5)).toBe(true)
  })

  it('returns true for floats that differ only by floating-point noise', () => {
    // classic 0.2 + 0.1 case
    expect(isFlatDouble(0.2 + 0.1, 0.3)).toBe(true)
  })

  it('returns false for differences above the relative tolerance', () => {
    expect(isFlatDouble(5.2, 5.0)).toBe(false)
    expect(isFlatDouble(1.01, 1.0)).toBe(false)
  })

  it('pins DOUBLE_RELATIVE_TOLERANCE at 1e-9', () => {
    expect(DOUBLE_RELATIVE_TOLERANCE).toBe(1e-9)
  })
})

describe('verdict: directionForDoubles', () => {
  it('returns up when current > previous', () => {
    expect(directionForDoubles(5.2, 5.0)).toBe('up')
  })

  it('returns down when current < previous', () => {
    expect(directionForDoubles(4.8, 5.0)).toBe('down')
  })

  it('returns flat when current equals previous', () => {
    expect(directionForDoubles(5.0, 5.0)).toBe('flat')
  })

  it('returns flat for floats that match within tolerance', () => {
    expect(directionForDoubles(0.2 + 0.1, 0.3)).toBe('flat')
  })
})

describe('verdict: directionForCounts', () => {
  it('uses exact integer equality for 持平', () => {
    expect(directionForCounts(5, 5)).toBe('flat')
    expect(directionForCounts(5, 4)).toBe('up')
    expect(directionForCounts(4, 5)).toBe('down')
  })

  it('does not apply double tolerance to counts', () => {
    // Without integer equality, this would be 'flat' via double
    // tolerance. Counts must be strict.
    expect(directionForCounts(5, 4)).not.toBe('flat')
  })
})

describe('verdict: isFavorable', () => {
  it('up-favorable polarity: up is favorable, down is not, flat always is', () => {
    expect(isFavorable('up', 'up-favorable')).toBe(true)
    expect(isFavorable('down', 'up-favorable')).toBe(false)
    expect(isFavorable('flat', 'up-favorable')).toBe(true)
  })

  it('down-favorable polarity: down is favorable, up is not, flat always is', () => {
    expect(isFavorable('down', 'down-favorable')).toBe(true)
    expect(isFavorable('up', 'down-favorable')).toBe(false)
    expect(isFavorable('flat', 'down-favorable')).toBe(true)
  })
})