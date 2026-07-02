import { describe, expect, it } from 'vitest'
import { computeMovingAverage } from './throughput'

describe('computeMovingAverage', () => {
  it('returns an array of the same length as the input', () => {
    const result = computeMovingAverage([1, 2, 3, 4, 5], 3)
    expect(result).toHaveLength(5)
  })

  it('computes trailing moving average with full window after warm-up', () => {
    const values = [1, 2, 3, 4, 5]
    const result = computeMovingAverage(values, 3)
    expect(result[0]).toBe(1)
    expect(result[1]).toBe((1 + 2) / 2)
    expect(result[2]).toBe((1 + 2 + 3) / 3)
    expect(result[3]).toBe((2 + 3 + 4) / 3)
    expect(result[4]).toBe((3 + 4 + 5) / 3)
  })

  it('handles partial leading window (fewer than window-1 predecessors still plots)', () => {
    const values = [10, 20, 30]
    const result = computeMovingAverage(values, 7)
    expect(result).toHaveLength(3)
    expect(result[0]).toBe(10)
    expect(result[1]).toBe((10 + 20) / 2)
    expect(result[2]).toBe((10 + 20 + 30) / 3)
  })

  it('handles all-zero input', () => {
    const values = [0, 0, 0, 0, 0]
    const result = computeMovingAverage(values, 3)
    expect(result).toEqual([0, 0, 0, 0, 0])
  })

  it('handles window larger than input length', () => {
    const values = [5, 10]
    const result = computeMovingAverage(values, 10)
    expect(result).toHaveLength(2)
    expect(result[0]).toBe(5)
    expect(result[1]).toBe((5 + 10) / 2)
  })

  it('handles single-element input', () => {
    const result = computeMovingAverage([42], 7)
    expect(result).toEqual([42])
  })

  it('handles window of 1 (identity)', () => {
    const values = [3, 7, 2, 9]
    const result = computeMovingAverage(values, 1)
    expect(result).toEqual(values)
  })

  it('handles empty input', () => {
    const result = computeMovingAverage([], 7)
    expect(result).toEqual([])
  })
})
