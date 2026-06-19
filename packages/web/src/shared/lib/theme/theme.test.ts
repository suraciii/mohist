import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import {
  THEME_STORAGE_KEY,
  THEME_OPTIONS,
  applyResolvedTheme,
  createMatchMedia,
  isThemeOption,
  readStoredTheme,
  resolveTheme,
  writeStoredTheme,
} from './theme'

describe('resolveTheme', () => {
  it('returns "dark" when stored preference is dark regardless of prefersDark', () => {
    expect(resolveTheme('dark', true)).toBe('dark')
    expect(resolveTheme('dark', false)).toBe('dark')
  })

  it('returns "light" when stored preference is light regardless of prefersDark', () => {
    expect(resolveTheme('light', true)).toBe('light')
    expect(resolveTheme('light', false)).toBe('light')
  })

  it('returns "dark" for system when prefersDark is true', () => {
    expect(resolveTheme('system', true)).toBe('dark')
  })

  it('returns "light" for system when prefersDark is false', () => {
    expect(resolveTheme('system', false)).toBe('light')
  })

  it('returns "dark" for system when prefersDark is true (covered case)', () => {
    expect(resolveTheme('system', true)).toBe('dark')
  })

  it('returns "light" when no stored preference and OS prefers light', () => {
    expect(resolveTheme(null, false)).toBe('light')
    expect(resolveTheme(undefined, false)).toBe('light')
  })

  it('returns "dark" when no stored preference and OS prefers dark', () => {
    expect(resolveTheme(null, true)).toBe('dark')
    expect(resolveTheme(undefined, true)).toBe('dark')
  })

  it('falls back to system behaviour for unknown stored values', () => {
    expect(resolveTheme('auto', true)).toBe('dark')
    expect(resolveTheme('auto', false)).toBe('light')
    expect(resolveTheme('', true)).toBe('dark')
  })
})

describe('isThemeOption', () => {
  it('accepts the three canonical options', () => {
    expect(isThemeOption('light')).toBe(true)
    expect(isThemeOption('dark')).toBe(true)
    expect(isThemeOption('system')).toBe(true)
  })

  it('rejects everything else', () => {
    expect(isThemeOption('LIGHT')).toBe(false)
    expect(isThemeOption('')).toBe(false)
    expect(isThemeOption(null)).toBe(false)
    expect(isThemeOption(undefined)).toBe(false)
    expect(isThemeOption(1)).toBe(false)
    expect(isThemeOption({})).toBe(false)
  })

  it('exposes exactly the three options via THEME_OPTIONS', () => {
    expect(THEME_OPTIONS).toEqual(['light', 'dark', 'system'])
  })
})

describe('storage helpers', () => {
  beforeEach(() => {
    window.localStorage.clear()
  })

  afterEach(() => {
    window.localStorage.clear()
  })

  it('readStoredTheme returns null when key is missing', () => {
    expect(readStoredTheme()).toBeNull()
  })

  it('readStoredTheme returns the raw stored string', () => {
    window.localStorage.setItem(THEME_STORAGE_KEY, 'dark')
    expect(readStoredTheme()).toBe('dark')
  })

  it('writeStoredTheme persists the value to the canonical key', () => {
    writeStoredTheme('system')
    expect(window.localStorage.getItem(THEME_STORAGE_KEY)).toBe('system')
  })

  it('writeStoredTheme tolerates private-mode / quota errors without throwing', () => {
    const setItemSpy = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('QuotaExceededError')
    })

    expect(() => writeStoredTheme('dark')).not.toThrow()
    expect(setItemSpy).toHaveBeenCalledWith(THEME_STORAGE_KEY, 'dark')

    setItemSpy.mockRestore()
  })

  it('readStoredTheme tolerates private-mode / quota errors without throwing', () => {
    const getItemSpy = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('SecurityError')
    })

    expect(() => readStoredTheme()).not.toThrow()
    expect(readStoredTheme()).toBeNull()

    getItemSpy.mockRestore()
  })
})

describe('applyResolvedTheme', () => {
  beforeEach(() => {
    document.documentElement.className = ''
  })

  it('adds the .dark class for resolved dark', () => {
    applyResolvedTheme('dark')
    expect(document.documentElement.classList.contains('dark')).toBe(true)
  })

  it('removes the .dark class for resolved light', () => {
    document.documentElement.classList.add('dark')
    applyResolvedTheme('light')
    expect(document.documentElement.classList.contains('dark')).toBe(false)
  })

  it('removes the .dark class when called twice with light', () => {
    applyResolvedTheme('light')
    applyResolvedTheme('light')
    expect(document.documentElement.classList.contains('dark')).toBe(false)
  })
})

describe('createMatchMedia', () => {
  it('returns the real matchMedia value when available', () => {
    const original = window.matchMedia
    const matchesSpy = vi.fn((query: string) => ({
      matches: true,
      media: query,
      onchange: null,
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      addListener: vi.fn(),
      removeListener: vi.fn(),
      dispatchEvent: vi.fn(),
    }))
    window.matchMedia = matchesSpy as unknown as typeof window.matchMedia

    const mql = createMatchMedia('(prefers-color-scheme: dark)')
    expect(mql.matches).toBe(true)
    expect(mql.media).toBe('(prefers-color-scheme: dark)')

    window.matchMedia = original
  })

  it('falls back to a no-op listener when matchMedia is unavailable', () => {
    const original = window.matchMedia
    delete (window as { matchMedia?: unknown }).matchMedia

    const mql = createMatchMedia('(prefers-color-scheme: dark)')
    expect(mql.matches).toBe(false)
    expect(() => mql.addEventListener('change', () => {})).not.toThrow()
    expect(() => mql.removeEventListener('change', () => {})).not.toThrow()

    window.matchMedia = original
  })
})
