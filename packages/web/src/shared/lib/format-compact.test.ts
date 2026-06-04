import { describe, it, expect } from 'vitest'
import { formatCompact, formatCost } from './format-compact'

describe('formatCompact', () => {
  it('returns empty string for null', () => {
    expect(formatCompact(null)).toBe('')
  })

  it('returns empty string for undefined', () => {
    expect(formatCompact(undefined)).toBe('')
  })

  it('returns raw number for values under 1000', () => {
    expect(formatCompact(0)).toBe('0')
    expect(formatCompact(1)).toBe('1')
    expect(formatCompact(999)).toBe('999')
  })

  it('formats thousands with k suffix', () => {
    expect(formatCompact(1000)).toBe('1.0k')
    expect(formatCompact(12400)).toBe('12.4k')
    expect(formatCompact(999999)).toBe('1000.0k')
  })

  it('formats millions with M suffix', () => {
    expect(formatCompact(1000000)).toBe('1.0M')
    expect(formatCompact(2500000)).toBe('2.5M')
  })

  it('handles negative numbers', () => {
    expect(formatCompact(-1500)).toBe('-1.5k')
  })
})

describe('formatCost', () => {
  it('returns empty string for null amount', () => {
    expect(formatCost(null, 'USD')).toBe('')
  })

  it('formats USD with $ symbol', () => {
    expect(formatCost(0.18, 'USD')).toBe('$0.18')
    expect(formatCost(1.5, 'USD')).toBe('$1.50')
  })

  it('formats EUR with € symbol', () => {
    expect(formatCost(0.18, 'EUR')).toBe('€0.18')
  })

  it('formats GBP with £ symbol', () => {
    expect(formatCost(0.18, 'GBP')).toBe('£0.18')
  })

  it('uses currency code as prefix for unknown currencies', () => {
    expect(formatCost(0.18, 'JPY')).toBe('JPY 0.18')
  })

  it('omits currency when not provided', () => {
    expect(formatCost(0.18, null)).toBe('0.18')
    expect(formatCost(0.18, undefined)).toBe('0.18')
  })
})
