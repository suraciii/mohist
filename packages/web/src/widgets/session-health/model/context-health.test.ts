import { describe, it, expect } from 'vitest'
import {
  classifyContextHealth,
  resolveContextUsage,
  resolveContextUsagePercent,
} from './context-health'

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

  it('clamps values above 100% to red', () => {
    expect(classifyContextHealth(150)).toBe('red')
  })
})

describe('resolveContextUsagePercent', () => {
  it('returns null when snapshot is null/undefined', () => {
    expect(resolveContextUsagePercent(null)).toBeNull()
    expect(resolveContextUsagePercent(undefined)).toBeNull()
  })

  it('returns null when no data is available', () => {
    expect(resolveContextUsagePercent({})).toBeNull()
  })

  it('prefers an explicit percent when provided', () => {
    expect(resolveContextUsagePercent({
      contextWindowUsed: 500_000,
      contextWindowSize: 1_000_000,
      contextUsagePercent: 12,
    })).toBe(12)
  })

  it('clamps explicit percent values to the [0, 100] range', () => {
    expect(resolveContextUsagePercent({ contextUsagePercent: -10 })).toBe(0)
    expect(resolveContextUsagePercent({ contextUsagePercent: 150 })).toBe(100)
  })

  it('falls back to used/size when explicit percent is missing', () => {
    expect(resolveContextUsagePercent({
      contextWindowUsed: 450_000,
      contextWindowSize: 1_000_000,
    })).toBe(45)
  })

  it('does not round the derived percent (caller may round for display)', () => {
    expect(resolveContextUsagePercent({
      contextWindowUsed: 333_333,
      contextWindowSize: 1_000_000,
    })).toBeCloseTo(33.3333, 3)
    expect(resolveContextUsagePercent({
      contextWindowUsed: 999_999,
      contextWindowSize: 1_000_000,
    })).toBeCloseTo(99.9999, 3)
  })

  it('returns null when window size is zero or negative', () => {
    expect(resolveContextUsagePercent({
      contextWindowUsed: 100,
      contextWindowSize: 0,
    })).toBeNull()
    expect(resolveContextUsagePercent({
      contextWindowUsed: 100,
      contextWindowSize: -1,
    })).toBeNull()
  })

  it('returns null when used is missing', () => {
    expect(resolveContextUsagePercent({ contextWindowSize: 1_000_000 })).toBeNull()
  })

  it('ignores non-finite values', () => {
    expect(resolveContextUsagePercent({
      contextWindowUsed: NaN,
      contextWindowSize: 1_000_000,
    })).toBeNull()
    expect(resolveContextUsagePercent({
      contextWindowUsed: 100,
      contextWindowSize: NaN,
    })).toBeNull()
  })
})

describe('resolveContextUsage', () => {
  it('returns a fully-null snapshot when input is null/undefined', () => {
    expect(resolveContextUsage(null)).toEqual({
      used: null,
      size: null,
      percent: null,
      status: null,
    })
    expect(resolveContextUsage(undefined)).toEqual({
      used: null,
      size: null,
      percent: null,
      status: null,
    })
  })

  it('returns null status when no usable data exists', () => {
    expect(resolveContextUsage({}).status).toBeNull()
    expect(resolveContextUsage({ contextWindowSize: 0 }).status).toBeNull()
  })

  it('returns green status for low usage', () => {
    const result = resolveContextUsage({
      contextWindowUsed: 200_000,
      contextWindowSize: 1_000_000,
    })
    expect(result.used).toBe(200_000)
    expect(result.size).toBe(1_000_000)
    expect(result.percent).toBe(20)
    expect(result.status).toBe('green')
  })

  it('returns yellow status for moderate usage', () => {
    const result = resolveContextUsage({
      contextWindowUsed: 720_000,
      contextWindowSize: 1_000_000,
    })
    expect(result.percent).toBe(72)
    expect(result.status).toBe('yellow')
  })

  it('returns red status for high usage', () => {
    const result = resolveContextUsage({
      contextWindowUsed: 940_000,
      contextWindowSize: 1_000_000,
    })
    expect(result.percent).toBe(94)
    expect(result.status).toBe('red')
  })

  it('preserves used and size even when explicit percent is given', () => {
    const result = resolveContextUsage({
      contextWindowUsed: 0,
      contextWindowSize: 1_000_000,
      contextUsagePercent: 45,
    })
    expect(result.used).toBe(0)
    expect(result.size).toBe(1_000_000)
    expect(result.percent).toBe(45)
    expect(result.status).toBe('green')
  })
})
