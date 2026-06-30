import { describe, it, expect } from 'vitest'
import { classifyContextHealth, clampPercent } from './context-health'

describe('classifyContextHealth', () => {
  it('returns null when percent is null', () => {
    expect(classifyContextHealth(null)).toBeNull()
  })

  it('returns null when percent is undefined', () => {
    expect(classifyContextHealth(undefined)).toBeNull()
  })

  it('returns null for non-finite values', () => {
    expect(classifyContextHealth(NaN)).toBeNull()
    expect(classifyContextHealth(Infinity)).toBeNull()
    expect(classifyContextHealth(-Infinity)).toBeNull()
  })

  it('returns green for usage below 60%', () => {
    expect(classifyContextHealth(0)).toBe('green')
    expect(classifyContextHealth(30)).toBe('green')
    expect(classifyContextHealth(45)).toBe('green')
    expect(classifyContextHealth(59.9)).toBe('green')
  })

  it('returns yellow for usage at 60% and above (up to 80% threshold)', () => {
    expect(classifyContextHealth(60)).toBe('yellow')
    expect(classifyContextHealth(72)).toBe('yellow')
    expect(classifyContextHealth(79.9)).toBe('yellow')
  })

  it('returns red for usage at 80% and above', () => {
    expect(classifyContextHealth(80)).toBe('red')
    expect(classifyContextHealth(85)).toBe('red')
    expect(classifyContextHealth(95)).toBe('red')
    expect(classifyContextHealth(100)).toBe('red')
  })

  it('returns red for values above 100%', () => {
    expect(classifyContextHealth(150)).toBe('red')
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
