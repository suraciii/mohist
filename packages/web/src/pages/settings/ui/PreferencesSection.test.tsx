// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { SidebarProvider } from '../../../shared/ui/components/sidebar'
import { ThemeProvider } from '../../../app/providers/ThemeProvider'
import { THEME_STORAGE_KEY } from '../../../shared/lib/theme/theme'
import {
  PreferencesSection,
  PREFERENCES_DESCRIPTORS,
} from './PreferencesSection'
import {
  SHORTCUTS,
  __resetShortcutHandlersForTesting,
  getShortcutHandler,
  registerShortcutHandler,
} from '../../../features/settings-search/keyboard-shortcuts'

function renderPreferences() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <ThemeProvider>
          <SidebarProvider>
            <PreferencesSection />
          </SidebarProvider>
        </ThemeProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

afterEach(() => {
  cleanup()
  window.localStorage.clear()
  __resetShortcutHandlersForTesting()
  vi.clearAllMocks()
})

describe('PreferencesSection (T-003)', () => {
  beforeEach(() => {
    __resetShortcutHandlersForTesting()
  })

  it('composes the page title through SettingsSection and the cards through CardSection', () => {
    renderPreferences()

    const settingsSections = document.querySelectorAll('section > h2.text-sm.font-medium.text-foreground')
    expect(settingsSections.length).toBeGreaterThanOrEqual(1)
    expect(settingsSections[0]).toHaveTextContent('Preferences')

    expect(screen.getByTestId('preferences-theme-card').tagName).toBe('SECTION')
    expect(screen.getByTestId('preferences-shortcuts-card').tagName).toBe('SECTION')
  })

  it('renders the theme selector with exactly light, dark, and system options', () => {
    renderPreferences()

    for (const option of ['light', 'dark', 'system']) {
      expect(
        screen.getByTestId(`preferences-theme-option-${option}`),
      ).toBeInTheDocument()
    }
    // No fourth option, no notification toggle
    expect(screen.queryByTestId('preferences-theme-option-notifications')).toBeNull()
    expect(screen.queryByText(/notification/i)).toBeNull()
  })

  it('does not render notification, timezone, or CLI executable-path fields (non-goal guard)', () => {
    renderPreferences()

    const root = document.body
    for (const forbidden of [
      /notification/i,
      /timezone/i,
      /time zone/i,
      /cli\s*path/i,
      /opencode\s*path/i,
      /executable/i,
    ]) {
      expect(
        Array.from(root.querySelectorAll('*')).some(
          (el) => el.children.length === 0 && forbidden.test(el.textContent ?? ''),
        ),
        `forbidden copy matched: ${forbidden}`,
      ).toBe(false)
    }
  })

  it('labels the theme selector via aria-labelledby so assistive tech announces it', () => {
    renderPreferences()

    const group = document.getElementById('preferences-theme')
    expect(group).not.toBeNull()
    const radioGroup = group?.querySelector('[role="radiogroup"]') ?? group?.querySelector('div')
    expect(radioGroup).not.toBeNull()
    expect(radioGroup?.getAttribute('aria-labelledby')).toMatch(/.+/)
  })

  it('selects "system" by default when no theme is stored, and reflects that in the status line', () => {
    renderPreferences()

    const status = screen.getByTestId('preferences-theme-current')
    expect(status).toHaveTextContent(/Current: System\./)
  })

  it('applies the selected theme immediately without reload and persists to mohist:theme', async () => {
    const user = userEvent.setup()
    renderPreferences()

    // Click the "dark" option
    await user.click(screen.getByTestId('preferences-theme-option-dark'))

    await waitFor(() => {
      expect(window.localStorage.getItem(THEME_STORAGE_KEY)).toBe('dark')
    })
    expect(document.documentElement.classList.contains('dark')).toBe(true)

    // Click "light"
    await user.click(screen.getByTestId('preferences-theme-option-light'))

    await waitFor(() => {
      expect(window.localStorage.getItem(THEME_STORAGE_KEY)).toBe('light')
    })
    expect(document.documentElement.classList.contains('dark')).toBe(false)
  })

  it('renders a non-interactive read-only keyboard shortcut reference', () => {
    renderPreferences()

    const list = screen.getByTestId('preferences-keyboard-shortcuts')
    expect(list.tagName).toBe('UL')
    expect(list.querySelectorAll('button, input, select, textarea').length).toBe(0)
    expect(list.querySelectorAll('a').length).toBe(0)
  })

  it('lists only the real shortcuts in the reference with Mac and Ctrl variants', () => {
    renderPreferences()

    for (const shortcut of SHORTCUTS) {
      expect(
        screen.getByTestId(`preferences-shortcut-${shortcut.id}`),
      ).toBeInTheDocument()
    }
    const expectedIds = new Set(SHORTCUTS.map((s: { id: string }) => s.id))
    expect(expectedIds).toEqual(new Set(['sidebar-toggle', 'settings-search']))
    expect(screen.getByText('⌘B / Ctrl+B')).toBeInTheDocument()
    expect(screen.getByText('⌘K / Ctrl+K')).toBeInTheDocument()

    // No fake/extra shortcuts: the list count matches the SHORTCUTS array.
    const items = screen.getAllByTestId(/^preferences-shortcut-/)
    expect(items.length).toBe(SHORTCUTS.length)
  })

  it('registers the sidebar toggle handler on mount so its id maps to a real handler', () => {
    expect(getShortcutHandler('sidebar-toggle')).toBeUndefined()
    renderPreferences()
    const handler = getShortcutHandler('sidebar-toggle')
    expect(handler).toBeTypeOf('function')
  })

  it('every shortcut id declared in SHORTCUTS can be resolved to a registered handler', () => {
    // Mount the component, then simulate T-004 (the settings search dialog)
    // registering the missing handler. After both are in place every id
    // must resolve — this is the "no fake shortcuts" guard the spec calls
    // for.
    renderPreferences()

    registerShortcutHandler('settings-search', () => {})

    for (const shortcut of SHORTCUTS) {
      const handler = getShortcutHandler(shortcut.id)
      expect(
        handler,
        `expected registered handler for ${shortcut.id}`,
      ).toBeTypeOf('function')
    }
  })

  it('unregisters the sidebar toggle handler on unmount to avoid stale references', () => {
    const { unmount } = renderPreferences()
    expect(getShortcutHandler('sidebar-toggle')).toBeTypeOf('function')

    unmount()
    expect(getShortcutHandler('sidebar-toggle')).toBeUndefined()
  })

  it('does not depend on SidebarProvider for its core render', () => {
    // The Preferences tab may render without SidebarProvider, so mount it
    // that way and assert the core cards still render.
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter>
          <ThemeProvider>
            <PreferencesSection />
          </ThemeProvider>
        </MemoryRouter>
      </QueryClientProvider>,
    )

    expect(screen.getByTestId('preferences-theme-card')).toBeInTheDocument()
    expect(screen.getByTestId('preferences-shortcuts-card')).toBeInTheDocument()
  })
})

describe('PREFERENCES_DESCRIPTORS (T-003 registry entry)', () => {
  it('contains exactly one entry: the theme selector with focusTargetId preferences-theme', () => {
    expect(PREFERENCES_DESCRIPTORS).toEqual([
      {
        tab: 'preferences',
        label: 'Theme',
        description:
          'Choose the application color scheme — light, dark, or follow the operating system.',
        focusTargetId: 'preferences-theme',
      },
    ])
  })
})
