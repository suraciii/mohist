import '@testing-library/jest-dom'
import { act, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ThemeProvider, useTheme } from './ThemeProvider'
import { THEME_STORAGE_KEY } from './theme'
import { installMatchMedia, resetThemeTestState, ThemeProbe } from './ThemeProviderTestSupport'

beforeEach(resetThemeTestState)
afterEach(() => {
  resetThemeTestState()
  vi.restoreAllMocks()
})

describe('ThemeProvider preference updates', () => {
  it('persists selections and updates the document theme', () => {
    window.localStorage.setItem(THEME_STORAGE_KEY, 'system')
    installMatchMedia(false)
    render(<ThemeProvider><ThemeProbe /></ThemeProvider>)
    act(() => screen.getByRole('button', { name: 'dark' }).click())
    expect(window.localStorage.getItem(THEME_STORAGE_KEY)).toBe('dark')
    expect(document.documentElement.classList.contains('dark')).toBe(true)
    act(() => screen.getByRole('button', { name: 'light' }).click())
    expect(document.documentElement.classList.contains('dark')).toBe(false)
    expect(window.localStorage.getItem(THEME_STORAGE_KEY)).toBe('light')
  })

  it('subscribes to matchMedia only in system mode', () => {
    window.localStorage.setItem(THEME_STORAGE_KEY, 'light')
    const { lists } = installMatchMedia(false)
    render(<ThemeProvider><ThemeProbe /></ThemeProvider>)
    expect(lists.every((list) => list.addEventListener.mock.calls.length === 0)).toBe(true)
    act(() => screen.getByRole('button', { name: 'system' }).click())
    expect(lists.some((list) => list.addEventListener.mock.calls.length > 0)).toBe(true)
  })

  it('unsubscribes safely on unmount', () => {
    const { setMatches } = installMatchMedia(false)
    const { unmount } = render(<ThemeProvider><ThemeProbe /></ThemeProvider>)
    unmount()
    expect(() => setMatches(true)).not.toThrow()
  })

  it('survives a localStorage read failure', () => {
    const readSpy = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('SecurityError')
    })
    installMatchMedia(false)
    render(<ThemeProvider><ThemeProbe /></ThemeProvider>)
    expect(screen.getByTestId('theme-probe-theme')).toHaveTextContent('system')
    readSpy.mockRestore()
  })

  it('survives a localStorage write failure', () => {
    installMatchMedia(false)
    render(<ThemeProvider><ThemeProbe /></ThemeProvider>)
    const writeSpy = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('QuotaExceededError')
    })
    expect(() => act(() => screen.getByRole('button', { name: 'dark' }).click())).not.toThrow()
    expect(writeSpy).toHaveBeenCalled()
    expect(screen.getByTestId('theme-probe-theme')).toHaveTextContent('dark')
    expect(document.documentElement.classList.contains('dark')).toBe(true)
  })

  it('returns inert defaults outside a provider', () => {
    function NakedProbe() {
      const { theme, resolvedTheme, setTheme } = useTheme()
      return <button type="button" data-theme={theme} data-resolved={resolvedTheme} onClick={() => setTheme('dark')}>noop</button>
    }
    render(<NakedProbe />)
    act(() => screen.getByRole('button', { name: 'noop' }).click())
    expect(screen.getByRole('button', { name: 'noop' })).toHaveAttribute('data-theme', 'system')
    expect(screen.getByRole('button', { name: 'noop' })).toHaveAttribute('data-resolved', 'light')
    expect(document.documentElement.classList.contains('dark')).toBe(false)
  })
})
