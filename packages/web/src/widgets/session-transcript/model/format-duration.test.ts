import { describe, expect, it } from 'vitest'
import { formatDuration, formatElapsed, formatElapsedNow } from './format-duration'

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

describe('formatElapsedNow', () => {
  const startMs = new Date('2024-01-01T00:00:00.000Z').getTime()

  it('returns null while startedAt is missing', () => {
    expect(formatElapsedNow(null, startMs + 1500)).toBeNull()
    expect(formatElapsedNow(undefined, startMs + 1500)).toBeNull()
  })

  it('returns null when startedAt is unparseable', () => {
    expect(formatElapsedNow('not-a-date', startMs + 1500)).toBeNull()
  })

  it('returns null when nowMs is not a finite number', () => {
    expect(formatElapsedNow('2024-01-01T00:00:00Z', Number.NaN)).toBeNull()
    expect(formatElapsedNow('2024-01-01T00:00:00Z', Number.POSITIVE_INFINITY)).toBeNull()
  })

  it('returns null for negative (out-of-order) durations', () => {
    expect(formatElapsedNow('2024-01-01T00:01:00Z', startMs)).toBeNull()
  })

  it('formats sub-second durations in milliseconds', () => {
    expect(formatElapsedNow('2024-01-01T00:00:00.000Z', startMs + 0)).toBe('0ms')
    expect(formatElapsedNow('2024-01-01T00:00:00.000Z', startMs + 250)).toBe('250ms')
    expect(formatElapsedNow('2024-01-01T00:00:00.000Z', startMs + 999)).toBe('999ms')
  })

  it('formats sub-minute durations in seconds with one decimal', () => {
    expect(formatElapsedNow('2024-01-01T00:00:00.000Z', startMs + 1000)).toBe('1.0s')
    expect(formatElapsedNow('2024-01-01T00:00:00.000Z', startMs + 1500)).toBe('1.5s')
    expect(formatElapsedNow('2024-01-01T00:00:00.000Z', startMs + 4700)).toBe('4.7s')
    expect(formatElapsedNow('2024-01-01T00:00:00.000Z', startMs + 59_999)).toBe('60.0s')
  })

  it('formats sub-hour durations as minutes and zero-padded seconds', () => {
    expect(formatElapsedNow('2024-01-01T00:00:00.000Z', startMs + 60_000)).toBe('1m 00s')
    expect(formatElapsedNow('2024-01-01T00:00:00.000Z', startMs + 75_000)).toBe('1m 15s')
    expect(formatElapsedNow('2024-01-01T00:00:00.000Z', startMs + 123_000)).toBe('2m 03s')
  })

  it('matches formatDuration given the same positive delta', () => {
    const diffs = [0, 1, 999, 1000, 1500, 59_999, 60_000, 75_000, 3_600_000, 3_900_000]
    for (const diff of diffs) {
      expect(formatElapsedNow('2024-01-01T00:00:00.000Z', startMs + diff)).toBe(formatDuration(diff))
    }
  })
})
