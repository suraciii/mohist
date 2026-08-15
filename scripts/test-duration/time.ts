import { performance } from 'node:perf_hooks'

export interface TimeSource {
  readonly now: () => number
}

export interface CalendarSource {
  readonly now: () => Date
}

// Process-monotonic time keeps duration accounting independent of wall-clock
// adjustments. Duration code receives this through TimeSource rather than
// reading time itself.
export const nativeTimeSource: TimeSource = {
  now: () => performance.now(),
}

// Calendar policy is deliberately separate from monotonic duration accounting.
// This composition adapter is the only production binding to the system date.
export const nativeCalendarSource: CalendarSource = {
  now: () => new Date(),
}
