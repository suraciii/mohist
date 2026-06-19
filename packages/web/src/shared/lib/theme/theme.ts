/**
 * Theme resolution and persistence shared between the inline pre-paint
 * `<script>` in `index.html` and the React `ThemeProvider`.
 *
 * The pre-paint script reads `mohist:theme` and applies the `.dark` class
 * before first paint so dark/system-dark users do not observe a light flash.
 * `resolveTheme` is the shared pure helper; the same function is used here
 * and inlined verbatim into the script so behaviour stays in lockstep.
 */

export const THEME_STORAGE_KEY = 'mohist:theme'

export const THEME_OPTIONS = ['light', 'dark', 'system'] as const
export type ThemeOption = (typeof THEME_OPTIONS)[number]
export type ResolvedTheme = 'light' | 'dark'

export const DARK_CLASS = 'dark'

export function isThemeOption(value: unknown): value is ThemeOption {
  return typeof value === 'string' && (THEME_OPTIONS as readonly string[]).includes(value)
}

/**
 * Resolves the stored theme preference against the current OS preference.
 * - `'light'` / `'dark'` map to themselves regardless of `prefersDark`.
 * - `'system'` (or any unknown value) defers to `prefersDark`.
 * - `null` / `undefined` (no stored value) defaults to `system`.
 */
export function resolveTheme(stored: string | null | undefined, prefersDark: boolean): ResolvedTheme {
  if (stored === 'light' || stored === 'dark') {
    return stored
  }
  return prefersDark ? 'dark' : 'light'
}

export function readStoredTheme(): string | null {
  if (typeof window === 'undefined') return null
  try {
    return window.localStorage.getItem(THEME_STORAGE_KEY)
  } catch {
    return null
  }
}

export function writeStoredTheme(value: ThemeOption): void {
  if (typeof window === 'undefined') return
  try {
    window.localStorage.setItem(THEME_STORAGE_KEY, value)
  } catch {
    // Private-mode / quota errors are non-fatal: in-memory state still applies.
  }
}

/**
 * Applies the resolved theme to `documentElement` by toggling the `.dark`
 * class. Pure DOM mutation; safe to call from the pre-paint script.
 */
export function applyResolvedTheme(resolved: ResolvedTheme): void {
  if (typeof document === 'undefined') return
  const root = document.documentElement
  if (resolved === 'dark') {
    root.classList.add(DARK_CLASS)
  } else {
    root.classList.remove(DARK_CLASS)
  }
}

export interface MatchMediaLike {
  matches: boolean
  media: string
  onchange: ((event: MediaQueryListEvent) => void) | null
  addEventListener: (type: 'change', listener: (event: MediaQueryListEvent) => void) => void
  removeEventListener: (type: 'change', listener: (event: MediaQueryListEvent) => void) => void
  addListener?: (listener: (event: MediaQueryListEvent) => void) => void
  removeListener?: (listener: (event: MediaQueryListEvent) => void) => void
}

export function createMatchMedia(query: string): MatchMediaLike {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') {
    return {
      matches: false,
      media: query,
      onchange: null,
      addEventListener: () => {},
      removeEventListener: () => {},
    }
  }
  return window.matchMedia(query) as unknown as MatchMediaLike
}
