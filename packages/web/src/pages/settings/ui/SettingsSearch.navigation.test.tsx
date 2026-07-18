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
    renderSettingsSearchWithProject('/settings/ai')
    openSettingsSearch()
    await user.type(await screen.findByTestId('settings-search-input'), 'session timeout')
    await user.keyboard('{Enter}')
    await waitFor(() => expect(screen.getByTestId('location-spy')).toHaveAttribute('data-pathname', '/settings/agent'))
    await waitFor(() => expect(document.activeElement?.id).toBe('agent-runtime-timeout'))
  })

  it('navigates from the focused command item', async () => {
    const user = userEvent.setup()
    renderSettingsSearchWithProject('/settings/ai')
    openSettingsSearch()
    const input = await screen.findByTestId('settings-search-input')
    await user.type(input, 'session timeout')
    await user.keyboard('{Enter}')
    await waitFor(() => expect(document.activeElement?.id).toBe('agent-runtime-timeout'))
  })

  it('dispatches an entry reveal event before focusing a conditional target', async () => {
    const dispatchSpy = vi.spyOn(window, 'dispatchEvent')
    const user = userEvent.setup()
    renderSettingsSearchWithLocationSpy('/settings/agent')
    openSettingsSearch()
    await user.type(await screen.findByTestId('settings-search-input'), 'plan stage model')
    await user.keyboard('{Enter}')
    await waitFor(() => expect(dispatchSpy).toHaveBeenCalledWith(expect.objectContaining({ type: 'mohist:settings:reveal-stage-model-overrides' })))
  })

  it('uses application routes for application settings', async () => {
    const user = userEvent.setup()
    renderSettingsSearchWithProject('/settings/agent')
    openSettingsSearch()
    await user.type(await screen.findByTestId('settings-search-input'), 'default coder agent model')
    await user.click(screen.getByTestId('settings-search-result-settings-default-model'))
    await waitFor(() => expect(screen.getByTestId('location-spy')).toHaveAttribute('data-pathname', '/settings/ai'))
    await waitFor(() => expect(document.activeElement?.id).toBe('settings-default-model'))
  })

  it('uses a project route for repository settings', async () => {
    const user = userEvent.setup()
    renderSettingsSearchWithProject('/settings/ai')
    openSettingsSearch()
    await user.type(await screen.findByTestId('settings-search-input'), 'e.g. frontend')
    await user.click(screen.getByTestId('settings-search-result-repository-add-name'))
    await waitFor(() => expect(screen.getByTestId('location-spy')).toHaveAttribute('data-pathname', '/selected-project/settings/repositories'))
    await waitFor(() => expect(document.activeElement?.id).toBe('repository-add-name'))
  })

  it('uses a project route for workflow settings', async () => {
    const user = userEvent.setup()
    renderSettingsSearchWithProject('/settings/ai')
    openSettingsSearch()
    await user.type(await screen.findByTestId('settings-search-input'), 'workflow')
    await user.click(screen.getByTestId('settings-search-result-workflow-profiles-section'))
    await waitFor(() => expect(screen.getByTestId('location-spy')).toHaveAttribute('data-pathname', '/selected-project/settings/workflows'))
    await waitFor(() => expect(document.activeElement?.id).toBe('workflow-profiles-section'))
  })

  it('uses an application route for Runtime settings', async () => {
    const user = userEvent.setup()
    renderSettingsSearchWithProject('/settings/ai')
    openSettingsSearch()
    await user.type(await screen.findByTestId('settings-search-input'), 'session timeout')
    await user.click(screen.getByTestId('settings-search-result-agent-runtime-timeout'))
    await waitFor(() => expect(screen.getByTestId('location-spy')).toHaveAttribute('data-pathname', '/settings/agent'))
    await waitFor(() => expect(document.activeElement?.id).toBe('agent-runtime-timeout'))
  })

  it('focuses the project default workflow control', async () => {
    const user = userEvent.setup()
    renderSettingsSearchWithProject('/settings/ai')
    openSettingsSearch()
    await user.type(await screen.findByTestId('settings-search-input'), 'default workflow')
    await user.click(screen.getByTestId('settings-search-result-project-default-workflow'))
    await waitFor(() => expect(screen.getByTestId('location-spy')).toHaveAttribute('data-pathname', '/selected-project/settings/workflows'))
    await waitFor(() => expect(document.activeElement?.id).toBe('project-default-workflow'))
  })

  it('disables project-scoped results when no project is selected', async () => {
    const user = userEvent.setup()
    renderSettingsSearchWithLocationSpy('/settings/ai')
    openSettingsSearch()
    await user.type(await screen.findByTestId('settings-search-input'), 'e.g. frontend')
    const result = screen.getByTestId('settings-search-result-repository-add-name')
    expect(result).toHaveAttribute('data-disabled', 'true')
    fireEvent.click(result)
    expect(screen.getByTestId('location-spy')).toHaveAttribute('data-pathname', '/settings/ai')
  })
})
