import { describe, it, expect } from 'vitest'
import { clampPercent, isContextHealthStatus } from './context-health'

describe('isContextHealthStatus', () => {
  it('accepts only server health status values', () => {
    expect(isContextHealthStatus('green')).toBe(true)
    expect(isContextHealthStatus('yellow')).toBe(true)
    expect(isContextHealthStatus('red')).toBe(true)
    expect(isContextHealthStatus('blue')).toBe(false)
    expect(isContextHealthStatus(null)).toBe(false)
    expect(isContextHealthStatus(undefined)).toBe(false)
  })
})

describe('clampPercent', () => {
  it('returns 0 for negative values', () => {
    expect(clampPercent(-10)).toBe(0)
    expect(clampPercent(-0.1)).toBe(0)
  })

  it('returns 100 for values above 100', () => {
    expect(clampPercent(150)).toBe(100)
    expect(clampPercent(100.1)).toBe(100)
  })

  it('returns the value as-is for [0, 100]', () => {
    expect(clampPercent(0)).toBe(0)
    expect(clampPercent(50)).toBe(50)
    expect(clampPercent(100)).toBe(100)
    expect(clampPercent(45.5)).toBe(45.5)
  })

  it('returns 0 for non-finite values', () => {
    expect(clampPercent(NaN)).toBe(0)
    expect(clampPercent(Infinity)).toBe(0)
    expect(clampPercent(-Infinity)).toBe(0)
  })
})
