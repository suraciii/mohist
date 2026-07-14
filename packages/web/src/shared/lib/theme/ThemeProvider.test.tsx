import '@testing-library/jest-dom'
import { act, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { ThemeProvider } from './ThemeProvider'
import { THEME_STORAGE_KEY } from './theme'
import { installMatchMedia, resetThemeTestState, ThemeProbe } from './ThemeProviderTestSupport'

beforeEach(resetThemeTestState)
afterEach(resetThemeTestState)

describe('ThemeProvider initialization', () => {
  it('defaults to system and resolves to light without a stored preference', () => {
    installMatchMedia(false)
    render(<ThemeProvider><ThemeProbe /></ThemeProvider>)
    expect(screen.getByTestId('theme-probe-theme')).toHaveTextContent('system')
    expect(screen.getByTestId('theme-probe-resolved')).toHaveTextContent('light')
    expect(document.documentElement.classList.contains('dark')).toBe(false)
  })

  it('follows prefers-color-scheme dark in system mode', () => {
    installMatchMedia(true)
    render(<ThemeProvider><ThemeProbe /></ThemeProvider>)
    expect(screen.getByTestId('theme-probe-resolved')).toHaveTextContent('dark')
    expect(document.documentElement.classList.contains('dark')).toBe(true)
  })

  it('reacts to prefers-color-scheme changes in system mode', () => {
    const { setMatches } = installMatchMedia(false)
    render(<ThemeProvider><ThemeProbe /></ThemeProvider>)
    expect(screen.getByTestId('theme-probe-resolved')).toHaveTextContent('light')
    act(() => setMatches(true))
    expect(screen.getByTestId('theme-probe-resolved')).toHaveTextContent('dark')
    act(() => setMatches(false))
    expect(screen.getByTestId('theme-probe-resolved')).toHaveTextContent('light')
  })

  it('does not react to the operating system for an explicit light preference', () => {
    window.localStorage.setItem(THEME_STORAGE_KEY, 'light')
    const { setMatches } = installMatchMedia(false)
    render(<ThemeProvider><ThemeProbe /></ThemeProvider>)
    act(() => setMatches(true))
    expect(screen.getByTestId('theme-probe-resolved')).toHaveTextContent('light')
  })

  it('honours an explicit dark preference on mount', () => {
    window.localStorage.setItem(THEME_STORAGE_KEY, 'dark')
    installMatchMedia(false)
    render(<ThemeProvider><ThemeProbe testId="dark-probe" /></ThemeProvider>)
    expect(screen.getByTestId('dark-probe-theme')).toHaveTextContent('dark')
    expect(document.documentElement.classList.contains('dark')).toBe(true)
  })
})
