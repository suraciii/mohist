// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { act, renderHook } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { LogEntry, LogTailResult } from './api'

const mocks = vi.hoisted(() => ({
  getLogTail: vi.fn<(cursor?: number, limit?: number, maxBytes?: number) => Promise<LogTailResult>>(),
}))

vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./api')>()
  return {
    ...actual,
    getLogTail: mocks.getLogTail,
  }
})

const { useLogs } = await import('./useLogs')

function makeEntry(overrides: Partial<LogEntry> = {}): LogEntry {
  return {
    level: 'INFO',
    time: '2026-07-04T08:00:00.000Z',
    service: 'Mohist.Server',
    message: 'hello',
    raw: '{"level":"INFO","message":"hello"}',
    ...overrides,
  }
}

function ok(overrides: Partial<LogTailResult> = {}): LogTailResult {
  return {
    lines: [],
    cursor: null,
    nextCursor: null,
    source: 'server.log',
    truncated: false,
    reset: false,
    unavailable: false,
    expectedLocation: null,
    reason: null,
    ...overrides,
  }
}

function unavailableResponse(reason: string, expectedLocation: string): LogTailResult {
  return {
    lines: [],
    cursor: null,
    nextCursor: null,
    source: null,
    truncated: false,
    reset: false,
    unavailable: true,
    expectedLocation,
    reason,
  }
}

beforeEach(() => {
  mocks.getLogTail.mockReset()
})

afterEach(() => {
  vi.useRealTimers()
})

async function flushMicrotasks() {
  await act(async () => {
    await Promise.resolve()
    await Promise.resolve()
  })
}

describe('useLogs: structured element preservation', () => {
  it('passes result.lines through as the entry list without re-parsing JSON', async () => {
    const serverEntry = makeEntry({
      level: 'WARN',
      time: '2026-07-04T09:00:00.000Z',
      service: 'Mohist.Server.Workflow',
      message: 'structured',
      raw: '{"level":"WARN","message":"structured"}',
    })
    mocks.getLogTail.mockResolvedValueOnce(
      ok({ lines: [serverEntry], reset: true, source: 'server.log' }),
    )

    const { result } = renderHook(() => useLogs())

    await flushMicrotasks()

    expect(result.current.entries).toHaveLength(1)
    expect(result.current.entries[0]).toEqual(serverEntry)
    expect(result.current.entries[0].level).toBe('WARN')
    expect(result.current.entries[0].time).toBe('2026-07-04T09:00:00.000Z')
    expect(result.current.entries[0].service).toBe('Mohist.Server.Workflow')
    expect(result.current.entries[0].message).toBe('structured')
  })

  it('preserves a non-JSON degraded element (message equals raw, structured fields null)', async () => {
    const degraded: LogEntry = {
      level: null,
      time: null,
      service: null,
      message: 'not-json-line',
      raw: 'not-json-line',
    }
    mocks.getLogTail.mockResolvedValueOnce(
      ok({ lines: [degraded], reset: true }),
    )

    const { result } = renderHook(() => useLogs())

    await flushMicrotasks()

    expect(result.current.entries).toHaveLength(1)
    expect(result.current.entries[0]).toEqual(degraded)
    expect(result.current.entries[0].level).toBeNull()
    expect(result.current.entries[0].time).toBeNull()
    expect(result.current.entries[0].service).toBeNull()
    expect(result.current.entries[0].message).toBe('not-json-line')
  })
})

describe('useLogs: reset replace-vs-append', () => {
  it('replaces the entry view when the server reports reset=true', async () => {
    mocks.getLogTail
      .mockResolvedValueOnce(
        ok({
          lines: [makeEntry({ message: 'first-batch', raw: 'first-batch' })],
          reset: true,
          source: 'server.log',
          cursor: 100,
          nextCursor: 100,
        }),
      )
      .mockResolvedValueOnce(
        ok({
          lines: [makeEntry({ message: 'replacement', raw: 'replacement' })],
          reset: true,
          source: 'server.log',
          cursor: 200,
          nextCursor: 200,
        }),
      )

    const { result } = renderHook(() => useLogs())

    await flushMicrotasks()

    expect(result.current.entries).toHaveLength(1)
    expect(result.current.entries[0].message).toBe('first-batch')

    await act(async () => {
      await result.current.refresh()
    })

    expect(result.current.entries).toHaveLength(1)
    expect(result.current.entries[0].message).toBe('replacement')
    expect(mocks.getLogTail.mock.calls[1][0]).toBe(100)
  })

  it('appends new entries when reset=false and trims to MAX_ENTRIES (2000)', async () => {
    const batch1 = Array.from({ length: 1500 }, (_, i) =>
      makeEntry({ message: `b1-${i}`, raw: `b1-${i}` }),
    )
    const batch2 = Array.from({ length: 1500 }, (_, i) =>
      makeEntry({ message: `b2-${i}`, raw: `b2-${i}` }),
    )

    mocks.getLogTail
      .mockResolvedValueOnce(ok({ lines: batch1, reset: true, nextCursor: 1000, cursor: 1000, source: 'server.log' }))
      .mockResolvedValueOnce(ok({ lines: batch2, reset: false, nextCursor: 2000, source: 'server.log' }))

    const { result } = renderHook(() => useLogs())

    await flushMicrotasks()
    expect(result.current.entries).toHaveLength(1500)
    expect(result.current.nextCursor).toBe(1000)

    // refresh re-fires using the stored nextCursor; server returns reset=false
    // and the new batch is appended, then trimmed to MAX_ENTRIES.
    await act(async () => {
      await result.current.refresh()
    })

    expect(result.current.entries).toHaveLength(2000)
    expect(result.current.entries[0].message).toBe('b1-1000')
    expect(result.current.entries[499].message).toBe('b1-1499')
    expect(result.current.entries[500].message).toBe('b2-0')
    expect(result.current.entries[1999].message).toBe('b2-1499')
    expect(result.current.nextCursor).toBe(2000)
  })

  it('passes the stored nextCursor back on the next poll', async () => {
    mocks.getLogTail
      .mockResolvedValueOnce(
        ok({
          lines: [makeEntry({ message: 'first', raw: 'first' })],
          reset: true,
          nextCursor: 12345,
          cursor: 12345,
          source: 'server.log',
        }),
      )
      .mockResolvedValueOnce(
        ok({
          lines: [makeEntry({ message: 'second', raw: 'second' })],
          reset: false,
          nextCursor: 22222,
          source: 'server.log',
        }),
      )

    const { result } = renderHook(() => useLogs())

    await flushMicrotasks()
    expect(result.current.entries).toHaveLength(1)
    expect(result.current.nextCursor).toBe(12345)

    await act(async () => {
      await result.current.refresh()
    })

    expect(result.current.entries).toHaveLength(2)
    expect(result.current.nextCursor).toBe(22222)

    const calls = mocks.getLogTail.mock.calls
    expect(calls[0][0]).toBeUndefined()
    expect(calls[1][0]).toBe(12345)
  })
})

describe('useLogs: unavailable passthrough', () => {
  it('exposes source, unavailable, expectedLocation, and reason from the response', async () => {
    mocks.getLogTail.mockResolvedValueOnce(
      unavailableResponse(
        'Log directory does not exist at /home/me/.mohist/logs.',
        '/home/me/.mohist/logs/server.log',
      ),
    )

    const { result } = renderHook(() => useLogs())

    await flushMicrotasks()

    expect(result.current.unavailable).toBe(true)
    expect(result.current.expectedLocation).toBe('/home/me/.mohist/logs/server.log')
    expect(result.current.reason).toBe('Log directory does not exist at /home/me/.mohist/logs.')
    expect(result.current.source).toBeNull()
    expect(result.current.entries).toEqual([])
  })

  it('exposes the real source identity when the server reports available', async () => {
    mocks.getLogTail.mockResolvedValueOnce(
      ok({ lines: [makeEntry()], reset: true, source: 'server.log' }),
    )

    const { result } = renderHook(() => useLogs())

    await flushMicrotasks()

    expect(result.current.source).toBe('server.log')
    expect(result.current.unavailable).toBe(false)
  })

  it('clears the active view and cursor when an available source becomes unavailable', async () => {
    mocks.getLogTail
      .mockResolvedValueOnce(
        ok({
          lines: [makeEntry({ message: 'old-source', raw: 'old-source' })],
          reset: true,
          cursor: 100,
          nextCursor: 100,
          source: 'server.log',
        }),
      )
      .mockResolvedValueOnce(
        unavailableResponse(
          'Log file server.log is missing.',
          '/home/me/.mohist/logs/server.log',
        ),
      )

    const { result } = renderHook(() => useLogs())

    await flushMicrotasks()
    expect(result.current.entries.map((entry) => entry.message)).toEqual(['old-source'])
    expect(result.current.nextCursor).toBe(100)
    expect(result.current.source).toBe('server.log')

    await act(async () => {
      await result.current.refresh()
    })

    expect(result.current.unavailable).toBe(true)
    expect(result.current.entries).toEqual([])
    expect(result.current.cursor).toBeNull()
    expect(result.current.nextCursor).toBeNull()
    expect(result.current.source).toBeNull()
    expect(result.current.reset).toBe(true)
    expect(result.current.expectedLocation).toBe('/home/me/.mohist/logs/server.log')
    expect(mocks.getLogTail.mock.calls[1][0]).toBe(100)
  })
})

describe('useLogs: auto-follow polling', () => {
  it('re-polls with the stored nextCursor after POLL_INTERVAL elapses (fake timers, no wall clock)', async () => {
    vi.useFakeTimers()
    mocks.getLogTail
      .mockResolvedValueOnce(
        ok({
          lines: [makeEntry({ message: 'first', raw: 'first' })],
          reset: true,
          nextCursor: 4242,
          cursor: 4242,
          source: 'server.log',
        }),
      )
      .mockResolvedValueOnce(
        ok({
          lines: [makeEntry({ message: 'second', raw: 'second' })],
          reset: false,
          nextCursor: 9999,
          source: 'server.log',
        }),
      )

    const { result } = renderHook(() => useLogs())

    // Flush microtasks so the initial fetch resolves.
    await act(async () => {
      await Promise.resolve()
      await Promise.resolve()
    })

    expect(result.current.entries).toHaveLength(1)
    expect(result.current.nextCursor).toBe(4242)

    await act(async () => {
      vi.advanceTimersByTime(3000)
    })

    expect(result.current.entries).toHaveLength(2)
    expect(result.current.nextCursor).toBe(9999)

    const calls = mocks.getLogTail.mock.calls
    expect(calls[0][0]).toBeUndefined()
    expect(calls[1][0]).toBe(4242)
  })

  it('keeps polling from the EOF cursor and appends only lines added after EOF', async () => {
    vi.useFakeTimers()
    mocks.getLogTail
      .mockResolvedValueOnce(
        ok({
          lines: [makeEntry({ message: 'initial', raw: 'initial' })],
          reset: true,
          truncated: false,
          cursor: 64,
          nextCursor: 64,
          source: 'server.log',
        }),
      )
      .mockResolvedValueOnce(
        ok({
          lines: [],
          reset: false,
          truncated: false,
          cursor: 64,
          nextCursor: 64,
          source: 'server.log',
        }),
      )
      .mockResolvedValueOnce(
        ok({
          lines: [makeEntry({ level: 'WARN', message: 'appended', raw: 'appended' })],
          reset: false,
          truncated: false,
          cursor: 128,
          nextCursor: 128,
          source: 'server.log',
        }),
      )

    const { result } = renderHook(() => useLogs())

    await flushMicrotasks()
    expect(result.current.entries.map((entry) => entry.message)).toEqual(['initial'])
    expect(result.current.nextCursor).toBe(64)

    await act(async () => {
      vi.advanceTimersByTime(3000)
      await Promise.resolve()
    })

    expect(result.current.entries.map((entry) => entry.message)).toEqual(['initial'])
    expect(result.current.nextCursor).toBe(64)

    await act(async () => {
      vi.advanceTimersByTime(3000)
      await Promise.resolve()
    })

    expect(result.current.entries.map((entry) => entry.message)).toEqual(['initial', 'appended'])
    expect(result.current.nextCursor).toBe(128)

    const calls = mocks.getLogTail.mock.calls
    expect(calls[0][0]).toBeUndefined()
    expect(calls[1][0]).toBe(64)
    expect(calls[2][0]).toBe(64)
  })

  it('does not poll while the page is hidden (visibility-gated)', async () => {
    vi.useFakeTimers()
    mocks.getLogTail.mockResolvedValue(
      ok({ lines: [], reset: true, source: 'server.log' }),
    )

    renderHook(() => useLogs())

    await act(async () => {
      await Promise.resolve()
      await Promise.resolve()
    })

    // Simulate the tab going hidden.
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'hidden' })
    document.dispatchEvent(new Event('visibilitychange'))

    mocks.getLogTail.mockClear()

    await act(async () => {
      vi.advanceTimersByTime(9000)
    })

    expect(mocks.getLogTail).not.toHaveBeenCalled()
  })
})
