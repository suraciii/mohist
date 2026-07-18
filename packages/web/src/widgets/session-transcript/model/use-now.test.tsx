import { act, renderHook } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useNow } from './use-now'

describe('useNow', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-01-01T00:00:00.000Z'))
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('seeds the first value from Date.now() and bumps it once per interval', () => {
    const { result } = renderHook(() => useNow({ intervalMs: 1000 }))

    expect(result.current).toBe(new Date('2026-01-01T00:00:00.000Z').getTime())

    act(() => {
      vi.advanceTimersByTime(1000)
    })
    expect(result.current).toBe(new Date('2026-01-01T00:00:01.000Z').getTime())

    act(() => {
      vi.advanceTimersByTime(2000)
    })
    expect(result.current).toBe(new Date('2026-01-01T00:00:03.000Z').getTime())
  })

  it('does not bump until at least one intervalMs has elapsed', () => {
    const { result } = renderHook(() => useNow({ intervalMs: 1000 }))

    const initial = result.current!

    act(() => {
      vi.advanceTimersByTime(999)
    })
    expect(result.current).toBe(initial)

    act(() => {
      vi.advanceTimersByTime(1)
    })
    expect(result.current).toBe(initial + 1000)
  })

  it('returns the injected now value verbatim and never starts an interval', () => {
    const fixed = new Date('2026-06-01T12:00:00.000Z').getTime()

    const { result } = renderHook(() => useNow({ intervalMs: 1000, now: fixed }))

    expect(result.current).toBe(fixed)

    act(() => {
      vi.advanceTimersByTime(60_000)
    })

    expect(result.current).toBe(fixed)
  })

  it('treats a 0 injection as a real now (no interval, no bump)', () => {
    const { result } = renderHook(() => useNow({ intervalMs: 1000, now: 0 }))

    expect(result.current).toBe(0)

    act(() => {
      vi.advanceTimersByTime(10_000)
    })

    expect(result.current).toBe(0)
  })

  it('restarts the interval when intervalMs changes while remaining enabled', () => {
    const { result, rerender } = renderHook(
      ({ intervalMs }: { intervalMs: number }) => useNow({ intervalMs }),
      { initialProps: { intervalMs: 1000 } },
    )

    const initial = result.current!

    rerender({ intervalMs: 500 })

    act(() => {
      vi.advanceTimersByTime(500)
    })
    expect(result.current).toBe(initial + 500)

    act(() => {
      vi.advanceTimersByTime(500)
    })
    expect(result.current).toBe(initial + 1000)
  })

  it('respects custom intervalMs such as 250ms', () => {
    const { result } = renderHook(() => useNow({ intervalMs: 250 }))

    const initial = result.current!

    act(() => {
      vi.advanceTimersByTime(250)
    })
    expect(result.current).toBe(initial + 250)

    act(() => {
      vi.advanceTimersByTime(750)
    })
    expect(result.current).toBe(initial + 1000)
  })

  it('returns undefined and never starts an interval when enabled is false', () => {
    const { result } = renderHook(() => useNow({ intervalMs: 1000, enabled: false }))

    expect(result.current).toBeUndefined()

    act(() => {
      vi.advanceTimersByTime(10_000)
    })

    expect(result.current).toBeUndefined()
  })

  it('toggles between disabled and enabled based on the enabled flag', () => {
    const { result, rerender } = renderHook(
      ({ enabled }: { enabled: boolean }) => useNow({ intervalMs: 1000, enabled }),
      { initialProps: { enabled: false } },
    )

    expect(result.current).toBeUndefined()

    rerender({ enabled: true })

    const firstEnabled = result.current!
    expect(firstEnabled).toBe(new Date('2026-01-01T00:00:00.000Z').getTime())

    act(() => {
      vi.advanceTimersByTime(1000)
    })
    expect(result.current).toBe(firstEnabled + 1000)

    rerender({ enabled: false })

    expect(result.current).toBeUndefined()

    act(() => {
      vi.advanceTimersByTime(10_000)
    })

    expect(result.current).toBeUndefined()
  })

  it('still respects an injected now value when enabled is false', () => {
    const fixed = new Date('2026-06-01T12:00:00.000Z').getTime()

    const { result } = renderHook(() => useNow({ intervalMs: 1000, now: fixed, enabled: false }))

    expect(result.current).toBe(fixed)
  })

  it('stops ticking after unmount', () => {
    const { result, unmount } = renderHook(() => useNow({ intervalMs: 1000 }))

    const initial = result.current!

    act(() => {
      vi.advanceTimersByTime(1000)
    })
    expect(result.current).toBe(initial + 1000)

    unmount()

    act(() => {
      vi.advanceTimersByTime(10_000)
    })

    expect(result.current).toBe(initial + 1000)
  })
})
