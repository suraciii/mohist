import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { act, render, screen } from '@testing-library/react'
import { ThemeProvider, useTheme } from './ThemeProvider'
import { THEME_STORAGE_KEY } from '../../shared/lib/theme/theme'

interface MatchMediaListener {
  (event: { matches: boolean }): void
}

interface FakeMediaQueryList {
  matches: boolean
  media: string
  onchange: ((event: { matches: boolean }) => void) | null
  listeners: MatchMediaListener[]
  addEventListener: ReturnType<typeof vi.fn>
  removeEventListener: ReturnType<typeof vi.fn>
  addListener: ReturnType<typeof vi.fn>
  removeListener: ReturnType<typeof vi.fn>
  dispatchEvent: ReturnType<typeof vi.fn>
  emit(matches: boolean): void
}

function installMatchMedia(initialMatches: boolean): { setMatches(next: boolean): void; lists: FakeMediaQueryList[] } {
  const lists: FakeMediaQueryList[] = []
  const factory = (query: string): FakeMediaQueryList => {
    const list: FakeMediaQueryList = {
      matches: initialMatches,
      media: query,
      onchange: null,
      listeners: [],
      addEventListener: vi.fn((_type: 'change', listener: MatchMediaListener) => {
        list.listeners.push(listener)
      }),
      removeEventListener: vi.fn((_type: 'change', listener: MatchMediaListener) => {
        list.listeners = list.listeners.filter((entry) => entry !== listener)
      }),
      addListener: vi.fn((listener: MatchMediaListener) => {
        list.listeners.push(listener)
      }),
      removeListener: vi.fn((listener: MatchMediaListener) => {
        list.listeners = list.listeners.filter((entry) => entry !== listener)
      }),
      dispatchEvent: vi.fn(),
      emit(matches: boolean) {
        list.matches = matches
        for (const listener of list.listeners) {
          listener({ matches })
        }
      },
    }
    lists.push(list)
    return list
  }

  window.matchMedia = vi.fn(factory) as unknown as typeof window.matchMedia

  return {
    lists,
    setMatches(next: boolean) {
      for (const list of lists) {
        list.emit(next)
      }
    },
  }
}

function ThemeProbe({ testId = 'theme-probe' }: { testId?: string }) {
  const { theme, resolvedTheme, setTheme } = useTheme()
  return (
    <div data-testid={testId}>
      <span data-testid={`${testId}-theme`}>{theme}</span>
      <span data-testid={`${testId}-resolved`}>{resolvedTheme}</span>
      <button type="button" onClick={() => setTheme('light')}>light</button>
      <button type="button" onClick={() => setTheme('dark')}>dark</button>
      <button type="button" onClick={() => setTheme('system')}>system</button>
    </div>
  )
}

describe('ThemeProvider', () => {
  beforeEach(() => {
    window.localStorage.clear()
    document.documentElement.className = ''
  })

  afterEach(() => {
    window.localStorage.clear()
    document.documentElement.className = ''
  })

  it('defaults to system when no stored preference exists', () => {
    const { setMatches } = installMatchMedia(false)
    void setMatches
    render(
      <ThemeProvider>
        <ThemeProbe />
      </ThemeProvider>,
    )

    expect(screen.getByTestId('theme-probe-theme')).toHaveTextContent('system')
    expect(screen.getByTestId('theme-probe-resolved')).toHaveTextContent('light')
    expect(document.documentElement.classList.contains('dark')).toBe(false)
  })

  it('follows prefers-color-scheme dark when in system mode with no stored preference', () => {
    installMatchMedia(true)
    render(
      <ThemeProvider>
        <ThemeProbe />
      </ThemeProvider>,
    )

    expect(screen.getByTestId('theme-probe-theme')).toHaveTextContent('system')
    expect(screen.getByTestId('theme-probe-resolved')).toHaveTextContent('dark')
    expect(document.documentElement.classList.contains('dark')).toBe(true)
  })

  it('reacts live to prefers-color-scheme changes while in system mode', () => {
    const { setMatches } = installMatchMedia(false)
    render(
      <ThemeProvider>
        <ThemeProbe />
      </ThemeProvider>,
    )

    expect(screen.getByTestId('theme-probe-resolved')).toHaveTextContent('light')
    expect(document.documentElement.classList.contains('dark')).toBe(false)

    act(() => {
      setMatches(true)
    })

    expect(screen.getByTestId('theme-probe-resolved')).toHaveTextContent('dark')
    expect(document.documentElement.classList.contains('dark')).toBe(true)

    act(() => {
      setMatches(false)
    })

    expect(screen.getByTestId('theme-probe-resolved')).toHaveTextContent('light')
    expect(document.documentElement.classList.contains('dark')).toBe(false)
  })

  it('does NOT react to OS changes when an explicit light preference is stored', () => {
    window.localStorage.setItem(THEME_STORAGE_KEY, 'light')
    const { setMatches, lists } = installMatchMedia(false)
    render(
      <ThemeProvider>
        <ThemeProbe />
      </ThemeProvider>,
    )

    expect(screen.getByTestId('theme-probe-theme')).toHaveTextContent('light')
    expect(screen.getByTestId('theme-probe-resolved')).toHaveTextContent('light')
    expect(lists.every((list) => list.addEventListener.mock.calls.length === 0)).toBe(true)

    act(() => {
      setMatches(true)
    })

    expect(screen.getByTestId('theme-probe-resolved')).toHaveTextContent('light')
    expect(document.documentElement.classList.contains('dark')).toBe(false)
  })

  it('honours a stored dark preference on mount', () => {
    window.localStorage.setItem(THEME_STORAGE_KEY, 'dark')
    installMatchMedia(false)
    render(
      <ThemeProvider>
        <ThemeProbe />
      </ThemeProvider>,
    )

    expect(screen.getByTestId('theme-probe-theme')).toHaveTextContent('dark')
    expect(screen.getByTestId('theme-probe-resolved')).toHaveTextContent('dark')
    expect(document.documentElement.classList.contains('dark')).toBe(true)
  })

  it('setTheme adds and removes the .dark class immediately and persists the value', () => {
    window.localStorage.setItem(THEME_STORAGE_KEY, 'system')
    installMatchMedia(false)
    render(
      <ThemeProvider>
        <ThemeProbe />
      </ThemeProvider>,
    )

    expect(document.documentElement.classList.contains('dark')).toBe(false)

    act(() => {
      screen.getByRole('button', { name: 'dark' }).click()
    })
    expect(document.documentElement.classList.contains('dark')).toBe(true)
    expect(window.localStorage.getItem(THEME_STORAGE_KEY)).toBe('dark')

    act(() => {
      screen.getByRole('button', { name: 'light' }).click()
    })
    expect(document.documentElement.classList.contains('dark')).toBe(false)
    expect(window.localStorage.getItem(THEME_STORAGE_KEY)).toBe('light')

    act(() => {
      screen.getByRole('button', { name: 'system' }).click()
    })
    expect(window.localStorage.getItem(THEME_STORAGE_KEY)).toBe('system')
  })

  it('subscribes to matchMedia only while system theme is active', () => {
    window.localStorage.setItem(THEME_STORAGE_KEY, 'light')
    const { lists } = installMatchMedia(false)
    render(
      <ThemeProvider>
        <ThemeProbe />
      </ThemeProvider>,
    )

    expect(lists.every((list) => list.addEventListener.mock.calls.length === 0)).toBe(true)

    act(() => {
      screen.getByRole('button', { name: 'system' }).click()
    })

    expect(lists.some((list) => list.addEventListener.mock.calls.length > 0)).toBe(true)
  })

  it('unsubscribes the matchMedia listener on unmount', () => {
    const { setMatches } = installMatchMedia(false)
    const { unmount } = render(
      <ThemeProvider>
        <ThemeProbe />
      </ThemeProvider>,
    )
    unmount()
    expect(() => setMatches(true)).not.toThrow()
  })

  it('falls back gracefully when localStorage throws on initial read', () => {
    const getItemSpy = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('SecurityError')
    })
    try {
      installMatchMedia(false)
      render(
        <ThemeProvider>
          <ThemeProbe />
        </ThemeProvider>,
      )
      expect(screen.getByTestId('theme-probe-theme')).toHaveTextContent('system')
      expect(screen.getByTestId('theme-probe-resolved')).toHaveTextContent('light')
    } finally {
      getItemSpy.mockRestore()
    }
  })

  it('falls back gracefully when localStorage throws on write', () => {
    installMatchMedia(false)
    render(
      <ThemeProvider>
        <ThemeProbe />
      </ThemeProvider>,
    )

    const setItemSpy = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('QuotaExceededError')
    })

    expect(() => {
      act(() => {
        screen.getByRole('button', { name: 'dark' }).click()
      })
    }).not.toThrow()
    expect(setItemSpy).toHaveBeenCalled()
    expect(screen.getByTestId('theme-probe-theme')).toHaveTextContent('dark')
    expect(document.documentElement.classList.contains('dark')).toBe(true)

    setItemSpy.mockRestore()
  })

  it('useTheme outside a provider returns safe defaults that do not mutate DOM', () => {
    function NakedProbe() {
      const { theme, resolvedTheme, setTheme } = useTheme()
      return (
        <div>
          <span data-testid="naked-theme">{theme}</span>
          <span data-testid="naked-resolved">{resolvedTheme}</span>
          <button type="button" onClick={() => setTheme('dark')}>noop-dark</button>
        </div>
      )
    }

    render(<NakedProbe />)
    expect(screen.getByTestId('naked-theme')).toHaveTextContent('system')
    expect(screen.getByTestId('naked-resolved')).toHaveTextContent('light')

    act(() => {
      screen.getByRole('button', { name: 'noop-dark' }).click()
    })
    expect(screen.getByTestId('naked-theme')).toHaveTextContent('system')
    expect(document.documentElement.classList.contains('dark')).toBe(false)
  })
})
