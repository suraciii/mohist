import '@testing-library/jest-dom'
import { screen, waitFor, waitForElementToBeRemoved } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { NO_MATCHES_COPY } from './SettingsSearch'
import {
  openSettingsSearch,
  renderSettingsSearch,
  renderSettingsSearchWithLocationSpy,
  resetSettingsSearchTestState,
} from './SettingsSearchTestSupport'

beforeEach(resetSettingsSearchTestState)
afterEach(resetSettingsSearchTestState)

describe('SettingsSearch filtering and dismissal', () => {
  it('matches labels, descriptions, and placeholders', async () => {
    const user = userEvent.setup()
    renderSettingsSearch('/settings/agent')
    openSettingsSearch()
    const input = await screen.findByTestId('settings-search-input')

    await user.type(input, 'timeout')
    expect(screen.getByTestId('settings-search-result-agent-runtime-timeout')).toBeInTheDocument()
    await user.clear(input)
    await user.type(input, 'upper bound')
    expect(screen.getByTestId('settings-search-result-agent-runtime-maxConcurrent')).toBeInTheDocument()
    await user.clear(input)
    await user.type(input, 'e.g. frontend')
    expect(screen.getByTestId('settings-search-result-repository-add-name')).toBeInTheDocument()
  })

  it('does not match live numeric values', async () => {
    const user = userEvent.setup()
    renderSettingsSearch('/settings/agent')
    openSettingsSearch()
    await user.type(await screen.findByTestId('settings-search-input'), '30')
    expect(screen.queryByTestId('settings-search-result-agent-runtime-timeout')).not.toBeInTheDocument()
  })

  it('shows empty state for an unmatched query', async () => {
    const user = userEvent.setup()
    renderSettingsSearch('/settings/agent')
    openSettingsSearch()
    await user.type(await screen.findByTestId('settings-search-input'), 'zzz-no-such-setting')
    await waitFor(() => expect(screen.getByTestId('settings-search-empty')).toHaveTextContent(NO_MATCHES_COPY))
  })

  it('closes on Escape without navigating', async () => {
    const user = userEvent.setup()
    renderSettingsSearchWithLocationSpy('/settings/ai')
    openSettingsSearch()
    await user.keyboard('{Escape}')
    await waitFor(() => expect(screen.queryByTestId('settings-search-input')).not.toBeInTheDocument())
    expect(screen.getByTestId('location-spy')).toHaveAttribute('data-pathname', '/settings/ai')
  })

  it('closes on overlay click without navigating', async () => {
    const user = userEvent.setup()
    renderSettingsSearchWithLocationSpy('/settings/agent')
    openSettingsSearch()
    const overlay = document.querySelector('[data-slot="dialog-overlay"]') as HTMLElement
    const removed = waitForElementToBeRemoved(() => screen.queryByTestId('settings-search-input'))
    await user.click(overlay)
    await removed
    expect(screen.getByTestId('location-spy')).toHaveAttribute('data-pathname', '/settings/agent')
  })

  it('renders the cmdk dialog primitives', async () => {
    renderSettingsSearch('/settings/agent')
    openSettingsSearch()
    await screen.findByTestId('settings-search-input')
    expect(screen.getByTestId('settings-search-list')).toBeInTheDocument()
    expect(document.querySelector('[data-slot="dialog-content"]')).not.toBeNull()
  })
})
