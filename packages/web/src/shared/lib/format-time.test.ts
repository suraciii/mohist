import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import {
  formatElapsedTimeAgo,
  formatSessionTime,
  formatTime,
  formatTimeAgo,
  formatLogTime,
  sessionTimeAbsoluteFormatter,
} from './format-time'

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

describe('formatSessionTime', () => {
  const dateIso = '2026-06-17T09:52:00.000Z'
  const dateMs = Date.parse(dateIso)
  const pastThreshold = dateMs + 5 * 60 * 60 * 1000
  const withinThreshold = dateMs + 30 * 60 * 1000
  const longAfter = dateMs + 5 * 24 * 60 * 60 * 1000

  function absoluteOf(ms: number): string {
    return sessionTimeAbsoluteFormatter.format(new Date(ms))
  }

  describe('terminal + past threshold (absolute primary / relative secondary)', () => {
    it('completed → 5h past threshold', () => {
      const out = formatSessionTime({ date: dateMs, statusKind: 'completed', now: pastThreshold })
      expect(out.primary).toBe(absoluteOf(dateMs))
      expect(out.secondary).toBe('5h ago')
    })

    it('failed → 5d past threshold', () => {
      const out = formatSessionTime({ date: dateMs, statusKind: 'failed', now: longAfter })
      expect(out.primary).toBe(absoluteOf(dateMs))
      expect(out.secondary).toBe('5d ago')
    })

    it('stale → 5h past threshold', () => {
      const out = formatSessionTime({ date: dateMs, statusKind: 'stale', now: pastThreshold })
      expect(out.primary).toBe(absoluteOf(dateMs))
      expect(out.secondary).toBe('5h ago')
    })
  })

  describe('terminal + within threshold (relative primary / absolute secondary)', () => {
    it('completed within 30m', () => {
      const out = formatSessionTime({ date: dateMs, statusKind: 'completed', now: withinThreshold })
      expect(out.primary).toBe('30m ago')
      expect(out.secondary).toBe(absoluteOf(dateMs))
    })

    it('failed within 30m', () => {
      const out = formatSessionTime({ date: dateMs, statusKind: 'failed', now: withinThreshold })
      expect(out.primary).toBe('30m ago')
      expect(out.secondary).toBe(absoluteOf(dateMs))
    })

    it('stale within 30m', () => {
      const out = formatSessionTime({ date: dateMs, statusKind: 'stale', now: withinThreshold })
      expect(out.primary).toBe('30m ago')
      expect(out.secondary).toBe(absoluteOf(dateMs))
    })
  })

  describe('non-terminal at any threshold (relative primary / absolute secondary)', () => {
    it('live within 30m', () => {
      const out = formatSessionTime({ date: dateMs, statusKind: 'live', now: withinThreshold })
      expect(out.primary).toBe('30m ago')
      expect(out.secondary).toBe(absoluteOf(dateMs))
    })

    it('live 5h past threshold — still relative', () => {
      const out = formatSessionTime({ date: dateMs, statusKind: 'live', now: pastThreshold })
      expect(out.primary).toBe('5h ago')
      expect(out.secondary).toBe(absoluteOf(dateMs))
    })

    it('finalizing 5d past threshold — still relative', () => {
      const out = formatSessionTime({ date: dateMs, statusKind: 'finalizing', now: longAfter })
      expect(out.primary).toBe('5d ago')
      expect(out.secondary).toBe(absoluteOf(dateMs))
    })

    it('recovering 5h past threshold — still relative', () => {
      const out = formatSessionTime({ date: dateMs, statusKind: 'recovering', now: pastThreshold })
      expect(out.primary).toBe('5h ago')
      expect(out.secondary).toBe(absoluteOf(dateMs))
    })

    it('probing 5h past threshold — relative preserved (probing invariant)', () => {
      const out = formatSessionTime({ date: dateMs, statusKind: 'probing', now: pastThreshold })
      expect(out.primary).toBe('5h ago')
      expect(out.secondary).toBe(absoluteOf(dateMs))
    })

    it('probing 5d past threshold — still relative (probing invariant)', () => {
      const out = formatSessionTime({ date: dateMs, statusKind: 'probing', now: longAfter })
      expect(out.primary).toBe('5d ago')
      expect(out.secondary).toBe(absoluteOf(dateMs))
    })
  })

  it('sub-minute differences render as "just now" for live sessions', () => {
    const justNow = formatSessionTime({
      date: dateMs,
      statusKind: 'live',
      now: dateMs + 5_000,
    })
    expect(justNow.primary).toBe('just now')
  })

  it('sub-minute differences render as "just now" for terminal sessions', () => {
    const justNow = formatSessionTime({
      date: dateMs,
      statusKind: 'completed',
      now: dateMs + 5_000,
    })
    expect(justNow.primary).toBe('just now')
  })

  describe('determinism', () => {
    it('same inputs produce identical output', () => {
      const input = { date: dateMs, statusKind: 'completed' as const, now: pastThreshold }
      expect(formatSessionTime(input)).toEqual(formatSessionTime(input))
    })

    it('ISO string and epoch ms inputs are equivalent', () => {
      const fromIso = formatSessionTime({ date: dateIso, statusKind: 'live', now: pastThreshold })
      const fromMs = formatSessionTime({ date: dateMs, statusKind: 'live', now: pastThreshold })
      expect(fromIso).toEqual(fromMs)
    })

    it('Date instance input is equivalent to epoch ms', () => {
      const fromDate = formatSessionTime({ date: new Date(dateMs), statusKind: 'live', now: pastThreshold })
      const fromMs = formatSessionTime({ date: dateMs, statusKind: 'live', now: pastThreshold })
      expect(fromDate).toEqual(fromMs)
    })
  })

  it('changing now flips the absolute/relative branch for terminal sessions across the 1-hour threshold', () => {
    const earlier = formatSessionTime({ date: dateMs, statusKind: 'completed', now: dateMs + 30 * 60 * 1000 })
    const later = formatSessionTime({ date: dateMs, statusKind: 'completed', now: dateMs + 90 * 60 * 1000 })

    expect(earlier.primary).toBe('30m ago')
    expect(later.primary).toBe(absoluteOf(dateMs))
    expect(later.primary).not.toBe(earlier.primary)
  })

  it('changing now does NOT flip the branch for a live session', () => {
    const earlier = formatSessionTime({ date: dateMs, statusKind: 'live', now: dateMs + 30 * 60 * 1000 })
    const later = formatSessionTime({ date: dateMs, statusKind: 'live', now: dateMs + 90 * 60 * 1000 })

    expect(earlier.primary).toBe('30m ago')
    expect(later.primary).toBe('1h ago')
    expect(earlier.primary).not.toBe(absoluteOf(dateMs))
  })

  describe('clock isolation', () => {
    it('does not call Date.now() internally', () => {
      const spy = vi.spyOn(Date, 'now')
      try {
        formatSessionTime({ date: dateIso, statusKind: 'completed', now: pastThreshold })
        formatSessionTime({ date: dateIso, statusKind: 'live', now: withinThreshold })
        formatSessionTime({ date: dateIso, statusKind: 'probing', now: longAfter })
        expect(spy).not.toHaveBeenCalled()
      } finally {
        spy.mockRestore()
      }
    })
  })
})
