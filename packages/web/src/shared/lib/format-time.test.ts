import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { formatElapsedTimeAgo, formatTime, formatTimeAgo, formatLogTime } from './format-time'

describe('formatTime', () => {
  it('formats a known ISO date string to locale string', () => {
    const iso = '2026-04-27T10:30:00.000Z'
    const result = formatTime(iso)
    const expected = new Date(iso).toLocaleString()
    expect(result).toBe(expected)
  })

  it('formats a different date consistently', () => {
    const iso = '2025-12-01T00:00:00.000Z'
    const result = formatTime(iso)
    expect(result).toBe(new Date(iso).toLocaleString())
  })

  it('returns a non-empty string for valid input', () => {
    expect(formatTime('2026-01-15T12:00:00Z').length).toBeGreaterThan(0)
  })
})

describe('formatTimeAgo', () => {
  let now: Date

  beforeEach(() => {
    now = new Date('2026-04-27T12:00:00.000Z')
    vi.useFakeTimers({ now })
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('returns "just now" for sub-minute difference', () => {
    const date = new Date('2026-04-27T11:59:30.000Z')
    expect(formatTimeAgo(date)).toBe('just now')
  })

  it('returns minutes for < 60 minutes', () => {
    const date = new Date('2026-04-27T11:30:00.000Z')
    expect(formatTimeAgo(date)).toBe('30m ago')
  })

  it('returns 1m ago for 1 minute ago', () => {
    const date = new Date('2026-04-27T11:59:00.000Z')
    expect(formatTimeAgo(date)).toBe('1m ago')
  })

  it('returns hours for < 24 hours', () => {
    const date = new Date('2026-04-27T08:00:00.000Z')
    expect(formatTimeAgo(date)).toBe('4h ago')
  })

  it('returns 1h ago for 1 hour ago', () => {
    const date = new Date('2026-04-27T11:00:00.000Z')
    expect(formatTimeAgo(date)).toBe('1h ago')
  })

  it('returns days for < 30 days', () => {
    const date = new Date('2026-04-25T12:00:00.000Z')
    expect(formatTimeAgo(date)).toBe('2d ago')
  })

  it('returns 1d ago for 1 day ago', () => {
    const date = new Date('2026-04-26T12:00:00.000Z')
    expect(formatTimeAgo(date)).toBe('1d ago')
  })

  it('returns locale date string for 30+ days', () => {
    const date = new Date('2026-03-20T12:00:00.000Z')
    expect(formatTimeAgo(date)).toBe(date.toLocaleDateString())
  })

  it('returns locale date string for multi-month boundary', () => {
    const date = new Date('2026-02-01T12:00:00.000Z')
    expect(formatTimeAgo(date)).toBe(date.toLocaleDateString())
  })

  it('returns locale date string for exactly 30 days ago', () => {
    const date = new Date('2026-03-28T12:00:00.000Z')
    expect(formatTimeAgo(date)).toBe(date.toLocaleDateString())
  })
})

describe('formatElapsedTimeAgo', () => {
  const now = Date.parse('2026-04-27T12:00:00.000Z')

  it('uses the supplied current time and retains elapsed day counts', () => {
    expect(formatElapsedTimeAgo('2026-04-24T12:00:00.000Z', now)).toBe('3d ago')
  })

  it('returns unknown for an invalid timestamp', () => {
    expect(formatElapsedTimeAgo('not-a-date', now)).toBe('unknown')
  })
})

describe('formatLogTime', () => {
  it('returns "--:--:--" for null', () => {
    expect(formatLogTime(null)).toBe('--:--:--')
  })

  it('formats a valid ISO time string as HH:MM:SS (24h)', () => {
    const result = formatLogTime('2026-04-27T14:05:09.123Z')
    const expected = new Date('2026-04-27T14:05:09.123Z').toLocaleTimeString('en-US', { hour12: false })
    expect(result).toBe(expected)
  })

  it('returns "Invalid Date" for unparseable input (does not throw)', () => {
    const result = formatLogTime('not-a-date')
    expect(result).toBe('Invalid Date')
  })
})
