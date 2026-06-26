import { describe, it, expect } from 'vitest'
import { formatDuration } from './format-duration'

describe('formatDuration', () => {
  it('returns empty string for null and undefined', () => {
    expect(formatDuration(null)).toBe('')
    expect(formatDuration(undefined)).toBe('')
  })

  it('renders sub-minute waits as "<1m"', () => {
    expect(formatDuration(0)).toBe('<1m')
    expect(formatDuration(30)).toBe('<1m')
    expect(formatDuration(59)).toBe('<1m')
  })

  it('renders minutes for waits between 1m and 1h', () => {
    expect(formatDuration(60)).toBe('1m')
    expect(formatDuration(90)).toBe('2m')
    expect(formatDuration(3_599)).toBe('60m')
  })

  it('renders hours for waits between 1h and 1d', () => {
    expect(formatDuration(3_600)).toBe('1.0h')
    expect(formatDuration(3_600 * 3.2)).toBe('3.2h')
    expect(formatDuration(3_600 * 10)).toBe('10h')
    expect(formatDuration(86_399)).toBe('24h')
  })

  it('renders days for waits of 1d or more', () => {
    expect(formatDuration(86_400)).toBe('1.0d')
    expect(formatDuration(86_400 * 5)).toBe('5.0d')
    expect(formatDuration(86_400 * 12.5)).toBe('13d')
    expect(formatDuration(86_400 * 10)).toBe('10d')
  })

  it('does not render negative durations', () => {
    expect(formatDuration(-60)).toBe('<1m')
    expect(formatDuration(-86_400)).toBe('<1m')
  })
})
