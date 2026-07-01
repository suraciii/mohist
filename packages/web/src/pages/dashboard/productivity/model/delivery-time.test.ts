import { describe, expect, it } from 'vitest'
import {
  computeRollingPercentile,
  P50_MEDIAN,
  P85_LINEAR_INTERPOLATION,
  ROLLING_WINDOW,
} from './delivery-time'

function sample(values: (number | null)[]) {
  return values.map((value) => ({ value }))
}

describe('ROLLING_WINDOW', () => {
  it('is a fixed non-user-configurable constant', () => {
    expect(ROLLING_WINDOW).toBe(10)
  })
})

describe('computeRollingPercentile', () => {
  it('returns an empty array for an empty input', () => {
    expect(computeRollingPercentile([])).toEqual([])
    expect(computeRollingPercentile([], 10, P85_LINEAR_INTERPOLATION)).toEqual([])
  })

  it('returns an array of the same length as the input', () => {
    const result = computeRollingPercentile(sample([1, 2, 3, 4, 5]))
    expect(result).toHaveLength(5)
  })

  it('plots from the first valid sample (partial leading window)', () => {
    const result = computeRollingPercentile(
      sample([10, 20, 30]),
      7,
      P50_MEDIAN,
    )
    expect(result).toEqual([10, 15, 20])
  })

  it('uses conventional median for even samples (P50 average of the two middles)', () => {
    const result = computeRollingPercentile(
      sample([1, 2, 3, 4]),
      10,
      P50_MEDIAN,
    )
    expect(result).toEqual([1, 1.5, 2, 2.5])
  })

  it('uses conventional median for odd samples (P50 picks the middle)', () => {
    const result = computeRollingPercentile(
      sample([10, 20, 30, 40, 50]),
      10,
      P50_MEDIAN,
    )
    expect(result[0]).toBe(10)
    expect(result[1]).toBe(15)
    expect(result[2]).toBe(20)
    expect(result[3]).toBe(25)
    expect(result[4]).toBe(30)
  })

  it('uses linear interpolation for P85 on an even sample', () => {
    const sortedValues = [1, 2, 3, 4]
    const rank = 0.85 * (sortedValues.length - 1)
    const lowerIndex = Math.floor(rank)
    const upperIndex = lowerIndex + 1
    const fraction = rank - lowerIndex
    const expectedAt3 = sortedValues[lowerIndex] * (1 - fraction) + sortedValues[upperIndex] * fraction

    const result = computeRollingPercentile(
      sample([1, 2, 3, 4]),
      10,
      P85_LINEAR_INTERPOLATION,
    )
    expect(result[3]).toBeCloseTo(expectedAt3, 10)
    expect(result[3]).toBeCloseTo(3.55, 5)
  })

  it('uses linear interpolation for P85 on an odd sample (fractional upper rank)', () => {
    const result = computeRollingPercentile(
      sample([10, 20, 30, 40, 50]),
      10,
      P85_LINEAR_INTERPOLATION,
    )
    const rank = 0.85 * 4
    const lowerIndex = Math.floor(rank)
    const upperIndex = lowerIndex + 1
    const fraction = rank - lowerIndex
    const expected = [10, 20, 30, 40, 50][lowerIndex] * (1 - fraction) + [10, 20, 30, 40, 50][upperIndex] * fraction
    expect(result[4]).toBeCloseTo(expected, 10)
  })

  it('rolling window of N=10 discards samples outside the trailing window once full', () => {
    const eleven = sample([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 1000])
    const result = computeRollingPercentile(eleven, 10, P50_MEDIAN)
    expect(result[0]).toBe(1)
    expect(result[1]).toBe(1.5)
    expect(result[9]).toBe(5.5)
    expect(result[10]).not.toBe(55)
  })

  it('does not emit a percentile when every sample in the trailing window is null (cycle lens)', () => {
    const allNull = sample([null, null, null])
    const result = computeRollingPercentile(allNull, 5, P50_MEDIAN)
    expect(result).toEqual([null, null, null])
  })

  it('excludes null entries per lens from the percentile computation (cycle lens)', () => {
    const samples = sample([3, null, 1])
    const result = computeRollingPercentile(samples, 5, P50_MEDIAN)
    expect(result[0]).toBe(3)
    expect(result[1]).toBe(3)
    expect(result[2]).toBe(2)
  })

  it('mixes well-defined and undefined durations across the trailing window', () => {
    const samples = sample([1, null, 2, null, 3, 4, null])
    const result = computeRollingPercentile(samples, 5, P50_MEDIAN)
    expect(result).toHaveLength(7)
    expect(result[0]).toBe(1)
    expect(result[1]).toBe(1)
    expect(result[2]).toBe(1.5)
    expect(result[3]).toBe(1.5)
    expect(result[4]).toBe(2)
    expect(result[5]).toBe(3)
    expect(result[6]).toBe(3)
  })

  it('carries the prior valid sample when the current sample is null within the trailing window', () => {
    const samples = sample([1, null])
    const result = computeRollingPercentile(samples, 5, P50_MEDIAN)
    expect(result).toEqual([1, 1])
  })

  it('window=1 acts as identity (single-sample "percentile")', () => {
    const samples = sample([5, 10, 20])
    const result = computeRollingPercentile(samples, 1, P50_MEDIAN)
    expect(result).toEqual([5, 10, 20])
  })

  it('window=0 returns an all-null series', () => {
    const samples = sample([1, 2, 3])
    const result = computeRollingPercentile(samples, 0, P50_MEDIAN)
    expect(result).toEqual([null, null, null])
  })

  it('two-value P85 interpolation is deterministic (snapshot of the algorithm)', () => {
    const samples = sample([10, 20])
    const result = computeRollingPercentile(samples, 10, P85_LINEAR_INTERPOLATION)
    expect(result[0]).toBe(10)
    expect(result[1]).toBeCloseTo(18.5, 10)
  })

  it('even-sample P85 interpolation is deterministic across re-runs (no Date.now/Math.random)', () => {
    const samples = sample([2, 4, 6, 8, 10, 12])
    const first = computeRollingPercentile(samples, 10, P85_LINEAR_INTERPOLATION)
    const second = computeRollingPercentile(samples, 10, P85_LINEAR_INTERPOLATION)
    expect(first).toEqual(second)
    const rank = 0.85 * (6 - 1)
    const lower = Math.floor(rank)
    const fraction = rank - lower
    const expectedLast = [2, 4, 6, 8, 10, 12][lower] * (1 - fraction) + [2, 4, 6, 8, 10, 12][lower + 1] * fraction
    expect(first[5]).toBeCloseTo(expectedLast, 10)
  })

  it('P50 default in the helper matches P50_MEDIAN', () => {
    const samples = sample([1, 2, 3, 4, 5])
    const explicit = computeRollingPercentile(samples, 3, P50_MEDIAN)
    const defaulted = computeRollingPercentile(samples, 3)
    expect(defaulted).toEqual(explicit)
  })

  it('rolling window of N=10 over a 12-sample series drops the first two samples once full', () => {
    const samples = sample([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12])
    const result = computeRollingPercentile(samples, 10, P50_MEDIAN)
    expect(result[9]).toBe(5.5)
    expect(result[10]).toBe(6.5)
    expect(result[11]).toBe(7.5)
  })

  it('rolling window of N=10 over a 12-sample series varies as the window fills', () => {
    const samples = sample([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12])
    const result = computeRollingPercentile(samples, 10, P50_MEDIAN)
    expect(result[0]).toBe(1)
    expect(result[8]).toBe(5)
    expect(result[9]).toBe(5.5)
    expect(result[11]).not.toBe(result[0])
  })

  it('does not mutate the input', () => {
    const samples = sample([3, 1, 2])
    const snapshot = samples.map(s => s.value)
    computeRollingPercentile(samples, 5, P50_MEDIAN)
    expect(samples.map(s => s.value)).toEqual(snapshot)
  })

  it('treats genuine zero values (0) differently from null undefined durations', () => {
    const samples = sample([0, 5, 10])
    const result = computeRollingPercentile(samples, 10, P50_MEDIAN)
    expect(result[0]).toBe(0)
    expect(result[1]).toBe(2.5)
    expect(result[2]).toBe(5)
  })
})
