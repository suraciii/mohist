import { describe, expect, it } from 'vitest'
import { formatDuration, formatElapsed } from './format-duration'

describe('formatDuration', () => {
  it('returns 0s for non-finite or negative values', () => {
    expect(formatDuration(Number.NaN)).toBe('0s')
    expect(formatDuration(-1)).toBe('0s')
    expect(formatDuration(Number.POSITIVE_INFINITY)).toBe('0s')
  })

  it('renders sub-second durations in milliseconds', () => {
    expect(formatDuration(0)).toBe('0ms')
    expect(formatDuration(250)).toBe('250ms')
    expect(formatDuration(999)).toBe('999ms')
  })

  it('renders sub-minute durations in seconds with one decimal', () => {
    expect(formatDuration(1000)).toBe('1.0s')
    expect(formatDuration(1500)).toBe('1.5s')
    expect(formatDuration(59_999)).toBe('60.0s')
  })

  it('renders sub-hour durations as minutes and zero-padded seconds', () => {
    expect(formatDuration(60_000)).toBe('1m 00s')
    expect(formatDuration(75_000)).toBe('1m 15s')
    expect(formatDuration(125_000)).toBe('2m 05s')
  })

  it('renders hour-spanning durations as hours and minutes', () => {
    expect(formatDuration(3_600_000)).toBe('1h 00m')
    expect(formatDuration(3_900_000)).toBe('1h 05m')
  })
})

describe('formatElapsed', () => {
  it('returns null while completedAt is missing', () => {
    expect(formatElapsed('2024-01-01T00:00:00Z', null)).toBeNull()
    expect(formatElapsed('2024-01-01T00:00:00Z', undefined)).toBeNull()
  })

  it('returns null while startedAt is missing', () => {
    expect(formatElapsed(null, '2024-01-01T00:01:00Z')).toBeNull()
    expect(formatElapsed(undefined, '2024-01-01T00:01:00Z')).toBeNull()
  })

  it('returns null when timestamps are unparseable', () => {
    expect(formatElapsed('not-a-date', 'also-not')).toBeNull()
  })

  it('returns null for negative (out-of-order) durations instead of -s', () => {
    expect(formatElapsed('2024-01-01T00:01:00Z', '2024-01-01T00:00:00Z')).toBeNull()
  })

  it('formats a positive duration between two timestamps', () => {
    expect(formatElapsed('2024-01-01T00:00:00Z', '2024-01-01T00:00:01.500Z')).toBe('1.5s')
    expect(formatElapsed('2024-01-01T00:00:00Z', '2024-01-01T00:00:02.000Z')).toBe('2.0s')
    expect(formatElapsed('2024-01-01T00:00:00Z', '2024-01-01T00:01:15.000Z')).toBe('1m 15s')
  })
})
