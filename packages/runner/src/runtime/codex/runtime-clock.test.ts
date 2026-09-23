import { describe, expect, it, vi } from 'vitest'
import {
  CODEX_CANCEL_CONFIRMATION_TIMEOUT_MS,
  CODEX_CLOSEOUT_WARNING_LEAD_MS,
  defaultClock,
  defaultCodexClock,
} from './runtime-clock.js'

/**
 * The `CodexClock` seam is the deterministic substitute for
 * `Date.now()` / `setTimeout` / `clearTimeout`. Production wires the
 * default clock; tests inject a fake. These tests pin the default
 * implementation to the global timer functions so a regression in the
 * seam cannot silently disable the deadline / closeout scheduling.
 */
describe('Codex runtime clock seam', () => {
  it('exposes one default clock shared with the consumer alias', () => {
    expect(defaultClock).toBe(defaultCodexClock)
  })

  it('reads wall-clock time through a finite numeric now()', () => {
    expect(typeof defaultCodexClock.now()).toBe('number')
    expect(Number.isFinite(defaultCodexClock.now())).toBe(true)
  })

  it('schedules a callback through the timer seam and returns a handle', () => {
    vi.useFakeTimers()
    const fired: string[] = []
    const handle = defaultCodexClock.setTimeout(() => fired.push('fired'), 100)

    expect(fired).toEqual([])
    expect(handle).toBeDefined()

    vi.advanceTimersByTime(100)
    expect(fired).toEqual(['fired'])
  })

  it('cancels a pending callback through clearTimeout', () => {
    vi.useFakeTimers()
    const fired: string[] = []
    const handle = defaultCodexClock.setTimeout(() => fired.push('fired'), 100)

    defaultCodexClock.clearTimeout(handle)
    vi.advanceTimersByTime(1_000)

    expect(fired).toEqual([])
  })

  it('pins the cancellation confirmation and closeout warning budgets', () => {
    expect(CODEX_CANCEL_CONFIRMATION_TIMEOUT_MS).toBe(5_000)
    expect(CODEX_CLOSEOUT_WARNING_LEAD_MS).toBe(5 * 60_000)
  })
})
