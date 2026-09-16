import '@testing-library/jest-dom'
import { fireEvent, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  openSettingsSearch,
  renderSettingsSearchWithLocationSpy,
  renderSettingsSearchWithProject,
  resetSettingsSearchTestState,
} from './SettingsSearchTestSupport'

beforeEach(resetSettingsSearchTestState)
afterEach(() => {
  resetSettingsSearchTestState()
  vi.restoreAllMocks()
})

describe('SettingsSearch navigation', () => {
  it('navigates on Enter and focuses the result target', async () => {
    const user = userEvent.setup()
    renderSettingsSearchWithProject('/settings/scheduling')
    openSettingsSearch()
    await user.type(await screen.findByTestId('settings-search-input'), 'session timeout')
    await user.keyboard('{Enter}')
    await waitFor(() =>
      expect(screen.getByTestId('location-spy')).toHaveAttribute('data-pathname', '/settings/scheduling'),
    )
    await waitFor(() => expect(document.activeElement?.id).toBe('agent-runtime-timeout'))
  })

  it('navigates from the focused command item', async () => {
    const user = userEvent.setup()
    renderSettingsSearchWithProject('/settings/scheduling')
    openSettingsSearch()
    const input = await screen.findByTestId('settings-search-input')
    await user.type(input, 'session timeout')
    await user.keyboard('{Enter}')
    await waitFor(() => expect(document.activeElement?.id).toBe('agent-runtime-timeout'))
  })

  it('focuses an already mounted route target without waiting for an animation frame', async () => {
    vi.spyOn(window, 'requestAnimationFrame').mockImplementation(() => 1)
    const user = userEvent.setup()
    renderSettingsSearchWithProject('/settings/scheduling')
    openSettingsSearch()
    await user.type(await screen.findByTestId('settings-search-input'), 'session timeout')
    await user.keyboard('{Enter}')

    await waitFor(() => expect(document.activeElement?.id).toBe('agent-runtime-timeout'))
  })

  it('carries a reveal event across application and project route branches', async () => {
    let revealPath: string | null = null
    const recordRevealPath = () => {
      revealPath = screen.getByTestId('location-spy').getAttribute('data-pathname')
    }
    window.addEventListener('mohist:settings:reveal-repository-add-form', recordRevealPath, { once: true })
    const user = userEvent.setup()
    renderSettingsSearchWithProject('/settings/scheduling')
    openSettingsSearch()
    await user.type(await screen.findByTestId('settings-search-input'), 'repository name')
    await user.keyboard('{Enter}')
    await waitFor(() => expect(revealPath).toBe('/selected-project/settings/repositories'))
  })

  it('uses a project route for repository settings', async () => {
    const user = userEvent.setup()
    renderSettingsSearchWithProject('/settings/scheduling')
    openSettingsSearch()
    await user.type(await screen.findByTestId('settings-search-input'), 'e.g. frontend')
    await user.click(screen.getByTestId('settings-search-result-repository-add-name'))
    await waitFor(() =>
      expect(screen.getByTestId('location-spy')).toHaveAttribute(
        'data-pathname',
        '/selected-project/settings/repositories',
      ),
    )
    await waitFor(() => expect(document.activeElement?.id).toBe('repository-add-name'))
  })

  it('uses a project route for workflow settings', async () => {
    const user = userEvent.setup()
    renderSettingsSearchWithProject('/settings/scheduling')
    openSettingsSearch()
    await user.type(await screen.findByTestId('settings-search-input'), 'workflow')
    await user.click(screen.getByTestId('settings-search-result-workflow-profiles-section'))
    await waitFor(() =>
      expect(screen.getByTestId('location-spy')).toHaveAttribute(
        'data-pathname',
        '/selected-project/settings/workflows',
      ),
    )
    await waitFor(() => expect(document.activeElement?.id).toBe('workflow-profiles-section'))
  })

  it('uses an application route for Scheduling settings', async () => {
    const user = userEvent.setup()
    renderSettingsSearchWithProject('/settings/scheduling')
    openSettingsSearch()
    await user.type(await screen.findByTestId('settings-search-input'), 'session timeout')
    await user.click(screen.getByTestId('settings-search-result-agent-runtime-timeout'))
    await waitFor(() =>
      expect(screen.getByTestId('location-spy')).toHaveAttribute('data-pathname', '/settings/scheduling'),
    )
    await waitFor(() => expect(document.activeElement?.id).toBe('agent-runtime-timeout'))
  })

  it('focuses the project default workflow control', async () => {
    const user = userEvent.setup()
    renderSettingsSearchWithProject('/settings/scheduling')
    openSettingsSearch()
    await user.type(await screen.findByTestId('settings-search-input'), 'default workflow')
    await user.click(screen.getByTestId('settings-search-result-project-default-workflow'))
    await waitFor(() =>
      expect(screen.getByTestId('location-spy')).toHaveAttribute(
        'data-pathname',
        '/selected-project/settings/workflows',
      ),
    )
    await waitFor(() => expect(document.activeElement?.id).toBe('project-default-workflow'))
  })

  it('disables project-scoped results when no project is selected', async () => {
    const user = userEvent.setup()
    renderSettingsSearchWithLocationSpy('/settings/scheduling')
    openSettingsSearch()
    await user.type(await screen.findByTestId('settings-search-input'), 'e.g. frontend')
    const result = screen.getByTestId('settings-search-result-repository-add-name')
    expect(result).toHaveAttribute('data-disabled', 'true')
    fireEvent.click(result)
    expect(screen.getByTestId('location-spy')).toHaveAttribute('data-pathname', '/settings/scheduling')
  })
})
