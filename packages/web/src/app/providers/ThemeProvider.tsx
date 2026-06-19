import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import {
  applyResolvedTheme,
  createMatchMedia,
  readStoredTheme,
  resolveTheme,
  writeStoredTheme,
  type ResolvedTheme,
  type ThemeOption,
} from '../../shared/lib/theme/theme'

const PREFERS_DARK_QUERY = '(prefers-color-scheme: dark)'

interface ThemeContextValue {
  theme: ThemeOption
  resolvedTheme: ResolvedTheme
  setTheme: (next: ThemeOption) => void
}

const defaultThemeContext: ThemeContextValue = {
  theme: 'system',
  resolvedTheme: 'light',
  setTheme: () => {},
}

const ThemeContext = createContext<ThemeContextValue>(defaultThemeContext)

interface ThemeProviderProps {
  children: ReactNode
}

function readInitialTheme(): ThemeOption {
  const stored = readStoredTheme()
  if (stored === 'light' || stored === 'dark' || stored === 'system') {
    return stored
  }
  return 'system'
}

function readInitialPrefersDark(): boolean {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') {
    return false
  }
  try {
    return window.matchMedia(PREFERS_DARK_QUERY).matches
  } catch {
    return false
  }
}

export function ThemeProvider({ children }: ThemeProviderProps) {
  const [theme, setThemeState] = useState<ThemeOption>(readInitialTheme)
  const [prefersDark, setPrefersDark] = useState<boolean>(readInitialPrefersDark)

  useEffect(() => {
    if (theme !== 'system') return undefined
    const mql = createMatchMedia(PREFERS_DARK_QUERY)
    const handleChange = (event: { matches: boolean }) => {
      setPrefersDark(event.matches)
    }
    try {
      mql.addEventListener('change', handleChange)
    } catch {
      // Some environments lack addEventListener on MediaQueryList; ignore.
    }
    return () => {
      try {
        mql.removeEventListener('change', handleChange)
      } catch {
        // ignore
      }
    }
  }, [theme])

  const resolvedTheme: ResolvedTheme = useMemo(
    () => resolveTheme(theme, prefersDark),
    [theme, prefersDark],
  )

  useEffect(() => {
    applyResolvedTheme(resolvedTheme)
  }, [resolvedTheme])

  const setTheme = useCallback((next: ThemeOption) => {
    setThemeState(next)
    writeStoredTheme(next)
  }, [])

  const value = useMemo<ThemeContextValue>(
    () => ({ theme, resolvedTheme, setTheme }),
    [theme, resolvedTheme, setTheme],
  )

  return <ThemeContext.Provider value={value}>{children}</ThemeContext.Provider>
}

export function useTheme(): ThemeContextValue {
  return useContext(ThemeContext)
}

export const __testing__ = { readInitialTheme, readInitialPrefersDark }
