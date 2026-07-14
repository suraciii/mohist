import '@testing-library/jest-dom'
import { fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  getShortcutHandler,
  registerShortcutHandler,
} from '../../../shared/lib/keyboard-shortcuts'
import {
  renderSettingsSearch,
  resetSettingsSearchTestState,
} from './SettingsSearchTestSupport'

beforeEach(resetSettingsSearchTestState)
afterEach(() => {
  resetSettingsSearchTestState()
  vi.restoreAllMocks()
})

describe('SettingsSearch shortcut scope', () => {
  it('opens for Command+K on the Settings page', async () => {
    renderSettingsSearch('/settings/agent')
    fireEvent.keyDown(window, { key: 'k', metaKey: true })
    await waitFor(() => expect(screen.getByTestId('settings-search-input')).toBeInTheDocument())
  })

  it('opens for Control+K on the Settings page', async () => {
    renderSettingsSearch('/settings/agent')
    fireEvent.keyDown(window, { key: 'k', ctrlKey: true })
    await waitFor(() => expect(screen.getByTestId('settings-search-input')).toBeInTheDocument())
  })

  it('does not register outside the Settings page', () => {
    expect(getShortcutHandler('settings-search')).toBeUndefined()
    fireEvent.keyDown(window, { key: 'k', metaKey: true })
    expect(screen.queryByTestId('settings-search-input')).not.toBeInTheDocument()
  })

  it('ignores the shortcut while editing', () => {
    renderSettingsSearch('/settings/agent')
    const editable = document.createElement('input')
    document.body.appendChild(editable)
    editable.focus()
    fireEvent.keyDown(editable, { key: 'k', metaKey: true })
    expect(screen.queryByTestId('settings-search-input')).not.toBeInTheDocument()
    editable.remove()
  })

  it('does not reopen an open dialog', async () => {
    renderSettingsSearch('/settings/agent')
    fireEvent.keyDown(window, { key: 'k', metaKey: true })
    await waitFor(() => expect(screen.getByTestId('settings-search-input')).toBeInTheDocument())
    fireEvent.keyDown(screen.getByTestId('settings-search-input'), { key: 'k', metaKey: true })
    expect(screen.getAllByTestId('settings-search-input')).toHaveLength(1)
  })

  it('registers its shortcut while mounted', () => {
    renderSettingsSearch('/settings/agent')
    expect(getShortcutHandler('settings-search')).toBeTypeOf('function')
  })

  it('unregisters its shortcut on unmount', () => {
    const { unmount } = renderSettingsSearch('/settings/agent')
    expect(getShortcutHandler('settings-search')).toBeTypeOf('function')
    unmount()
    expect(getShortcutHandler('settings-search')).toBeUndefined()
  })

  it('uses a page-local keydown listener', () => {
    const addSpy = vi.spyOn(window, 'addEventListener')
    renderSettingsSearch('/settings/agent')
    expect(addSpy.mock.calls.some(([eventName]) => eventName === 'keydown')).toBe(true)
  })

  it('remains independent of other shortcut registrations', () => {
    renderSettingsSearch('/settings/agent')
    registerShortcutHandler('sidebar-toggle', () => {})
    expect(getShortcutHandler('settings-search')).toBeTypeOf('function')
    expect(getShortcutHandler('sidebar-toggle')).toBeTypeOf('function')
  })
})
