import { useTheme } from './ThemeProvider'
import { vi } from 'vitest'

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

export function installMatchMedia(initialMatches: boolean) {
  const lists: FakeMediaQueryList[] = []
  const factory = (query: string): FakeMediaQueryList => {
    const list: FakeMediaQueryList = {
      matches: initialMatches,
      media: query,
      onchange: null,
      listeners: [],
      addEventListener: vi.fn((_type: 'change', listener: MatchMediaListener) => list.listeners.push(listener)),
      removeEventListener: vi.fn((_type: 'change', listener: MatchMediaListener) => {
        list.listeners = list.listeners.filter((entry) => entry !== listener)
      }),
      addListener: vi.fn((listener: MatchMediaListener) => list.listeners.push(listener)),
      removeListener: vi.fn((listener: MatchMediaListener) => {
        list.listeners = list.listeners.filter((entry) => entry !== listener)
      }),
      dispatchEvent: vi.fn(),
      emit(matches: boolean) {
        list.matches = matches
        for (const listener of list.listeners) listener({ matches })
      },
    }
    lists.push(list)
    return list
  }
  vi.stubGlobal('matchMedia', vi.fn(factory))
  return {
    lists,
    setMatches(next: boolean) {
      for (const list of lists) list.emit(next)
    },
  }
}

export function ThemeProbe({ testId = 'theme-probe' }: { testId?: string }) {
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

export function resetThemeTestState() {
  window.localStorage.clear()
  document.documentElement.removeAttribute('class')
}
