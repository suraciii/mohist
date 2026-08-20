import { performance } from 'node:perf_hooks'

export interface TimeSource {
  readonly now: () => number
}

// Process-monotonic time keeps duration accounting independent of wall-clock
// adjustments. Duration code receives this through TimeSource rather than
// reading time itself.
export const nativeTimeSource: TimeSource = {
  now: () => performance.now(),
}
