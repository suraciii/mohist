import { act, renderHook } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { setMatchesForTest, useMediaQuery } from './use-media-query'

function installMatchMediaStub(impl: (query: string) => MediaQueryList) {
  vi.stubGlobal('matchMedia', vi.fn(impl))
}

describe('useMediaQuery', () => {
  beforeEach(() => {
    setMatchesForTest(null)
  })

  it('reads the initial value from the test seam when set before render', () => {
    setMatchesForTest(true)

    const { result } = renderHook(() => useMediaQuery('(min-width: 1280px)'))

    expect(result.current).toBe(true)
  })

  it('seeds the initial value as false when no test seam is set', () => {
    const { result } = renderHook(() => useMediaQuery('(min-width: 1280px)'))

    expect(result.current).toBe(false)
  })

  it('reads the initial browser match synchronously when no test seam is set', () => {
    const matchMedia = vi.fn().mockImplementation((query: string) => ({
      matches: true,
      media: query,
      onchange: null,
      addListener: vi.fn(),
      removeListener: vi.fn(),
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      dispatchEvent: vi.fn(),
    }))
    installMatchMediaStub(matchMedia)

    try {
      const { result } = renderHook(() => useMediaQuery('(min-width: 1280px)'))

      expect(result.current).toBe(true)
    } finally {
      vi.unstubAllGlobals()
    }
  })

  it('updates when the test seam flips from false to true', async () => {
    const { result } = renderHook(() => useMediaQuery('(min-width: 1280px)'))

    expect(result.current).toBe(false)

    await act(async () => {
      setMatchesForTest(true)
    })

    expect(result.current).toBe(true)
  })

  it('updates when the test seam flips from true to false', async () => {
    setMatchesForTest(true)

    const { result } = renderHook(() => useMediaQuery('(min-width: 1280px)'))

    expect(result.current).toBe(true)

    await act(async () => {
      setMatchesForTest(false)
    })

    expect(result.current).toBe(false)
  })

  it('clears the seam when setMatchesForTest(null) is called and falls back to the live matchMedia value', async () => {
    const mqlAddEventListener = vi.fn()
    const mqlRemoveEventListener = vi.fn()
    const matchMedia = vi.fn().mockImplementation((query: string) => ({
      matches: false,
      media: query,
      onchange: null,
      addListener: vi.fn(),
      removeListener: vi.fn(),
      addEventListener: mqlAddEventListener,
      removeEventListener: mqlRemoveEventListener,
      dispatchEvent: vi.fn(),
    }))
    installMatchMediaStub(matchMedia)

    try {
      setMatchesForTest(true)

      const { result } = renderHook(() => useMediaQuery('(min-width: 1280px)'))
      expect(result.current).toBe(true)

      await act(async () => {
        setMatchesForTest(null)
      })

      expect(result.current).toBe(false)
      expect(matchMedia).toHaveBeenCalledWith('(min-width: 1280px)')
      expect(mqlAddEventListener).toHaveBeenCalledWith('change', expect.any(Function))
    } finally {
      vi.unstubAllGlobals()
    }
  })

  it('subscribes to matchMedia change events when the seam is inactive', async () => {
    const listeners: Array<() => void> = []
    const matchMedia = vi.fn().mockImplementation((query: string) => ({
      matches: false,
      media: query,
      onchange: null,
      addListener: vi.fn(),
      removeListener: vi.fn(),
      addEventListener: (_: string, cb: () => void) => listeners.push(cb),
      removeEventListener: vi.fn(),
      dispatchEvent: vi.fn(),
    }))
    installMatchMediaStub(matchMedia)

    try {
      const { result } = renderHook(() => useMediaQuery('(min-width: 1280px)'))
      expect(result.current).toBe(false)

      const mql = matchMedia.mock.results[matchMedia.mock.results.length - 1]?.value as { matches: boolean } | undefined
      if (mql) mql.matches = true

      await act(async () => {
        for (const listener of listeners) listener()
      })

      expect(result.current).toBe(true)
    } finally {
      vi.unstubAllGlobals()
    }
  })

  it('removes the matchMedia change listener on unmount', () => {
    const mqlRemoveEventListener = vi.fn()
    const matchMedia = vi.fn().mockImplementation((query: string) => ({
      matches: false,
      media: query,
      onchange: null,
      addListener: vi.fn(),
      removeListener: vi.fn(),
      addEventListener: vi.fn(),
      removeEventListener: mqlRemoveEventListener,
      dispatchEvent: vi.fn(),
    }))
    installMatchMediaStub(matchMedia)

    try {
      const { unmount } = renderHook(() => useMediaQuery('(min-width: 1280px)'))

      unmount()

      expect(mqlRemoveEventListener).toHaveBeenCalledWith('change', expect.any(Function))
    } finally {
      vi.unstubAllGlobals()
    }
  })

  it('does not subscribe to matchMedia when the test seam is set', () => {
    const matchMedia = vi.fn().mockImplementation((query: string) => ({
      matches: false,
      media: query,
      onchange: null,
      addListener: vi.fn(),
      removeListener: vi.fn(),
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      dispatchEvent: vi.fn(),
    }))
    installMatchMediaStub(matchMedia)

    try {
      setMatchesForTest(true)

      renderHook(() => useMediaQuery('(min-width: 1280px)'))

      expect(matchMedia).not.toHaveBeenCalled()
    } finally {
      vi.unstubAllGlobals()
    }
  })

  it('keeps subscribers on subsequent overrides without leaking listeners across mounts', async () => {
    setMatchesForTest(true)

    const first = renderHook(() => useMediaQuery('(min-width: 1280px)'))
    expect(first.result.current).toBe(true)

    const second = renderHook(() => useMediaQuery('(min-width: 1280px)'))
    expect(second.result.current).toBe(true)

    await act(async () => {
      setMatchesForTest(false)
    })
    expect(first.result.current).toBe(false)
    expect(second.result.current).toBe(false)

    first.unmount()

    await act(async () => {
      setMatchesForTest(true)
    })
    expect(second.result.current).toBe(true)
  })
})
