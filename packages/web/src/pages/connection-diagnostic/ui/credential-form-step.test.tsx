import '@testing-library/jest-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { CredentialFormStep } from './credential-form-step'

const APP_TOKEN = 'xapp-1-A-SECRET-FORM'
const BOT_TOKEN = 'xoxb-1-B-SECRET-FORM'

afterEach(cleanup)

describe('CredentialFormStep', () => {
  it('renders masked inputs and never displays the typed value in cleartext on the page', async () => {
    const user = userEvent.setup()
    render(<CredentialFormStep onSubmit={() => undefined} isSubmitting={false} errorMessage={null} />)

    const appInput = screen.getByLabelText('App token') as HTMLInputElement
    const botInput = screen.getByLabelText('Bot token') as HTMLInputElement
    expect(appInput.type).toBe('password')
    expect(botInput.type).toBe('password')

    await user.type(appInput, APP_TOKEN)
    await user.type(botInput, BOT_TOKEN)

    expect(appInput.value).toBe(APP_TOKEN)
    expect(botInput.value).toBe(BOT_TOKEN)
    const rendered = document.body.textContent ?? ''
    expect(rendered).not.toContain(APP_TOKEN)
    expect(rendered).not.toContain(BOT_TOKEN)
  })

  it('submits the credentials through the callback and clears local state so the values do not linger', async () => {
    const user = userEvent.setup()
    const onSubmit = vi.fn()
    const { unmount } = render(
      <CredentialFormStep onSubmit={onSubmit} isSubmitting={false} errorMessage={null} />,
    )

    const appInput = screen.getByLabelText('App token') as HTMLInputElement
    const botInput = screen.getByLabelText('Bot token') as HTMLInputElement

    await user.type(appInput, APP_TOKEN)
    await user.type(botInput, BOT_TOKEN)

    await user.click(screen.getByTestId('connection-setup-credential-form-submit'))

    expect(onSubmit).toHaveBeenCalledWith({ appToken: APP_TOKEN, botToken: BOT_TOKEN })

    expect(appInput.value).toBe('')
    expect(botInput.value).toBe('')

    unmount()
    const rendered = document.body.textContent ?? ''
    expect(rendered).not.toContain(APP_TOKEN)
    expect(rendered).not.toContain(BOT_TOKEN)
  })

  it('disables the submit button while submitting', () => {
    render(<CredentialFormStep onSubmit={() => undefined} isSubmitting={true} errorMessage={null} />)

    const button = screen.getByTestId('connection-setup-credential-form-submit')
    expect(button).toBeDisabled()
  })

  it('disables the submit button until both fields have a value', () => {
    render(<CredentialFormStep onSubmit={() => undefined} isSubmitting={false} errorMessage={null} />)

    const button = screen.getByTestId('connection-setup-credential-form-submit')
    expect(button).toBeDisabled()
  })

  it('renders the server error message when provided', () => {
    render(
      <CredentialFormStep
        onSubmit={() => undefined}
        isSubmitting={false}
        errorMessage="Slack rejected the credentials"
      />,
    )

    expect(screen.getByTestId('connection-setup-credential-form-error')).toHaveTextContent(
      'Slack rejected the credentials',
    )
  })

  it('does not write the typed value to localStorage, sessionStorage, or the URL', async () => {
    const user = userEvent.setup()
    const originalHref = window.location.href
    const setItemSpy = vi.spyOn(Storage.prototype, 'setItem')
    render(<CredentialFormStep onSubmit={() => undefined} isSubmitting={false} errorMessage={null} />)

    const appInput = screen.getByLabelText('App token') as HTMLInputElement
    await user.type(appInput, APP_TOKEN)

    expect(window.location.href).toBe(originalHref)
    expect(window.localStorage.length).toBe(0)
    expect(window.sessionStorage.length).toBe(0)
    for (const call of setItemSpy.mock.calls) {
      const payload = call[1]
      if (typeof payload === 'string') {
        expect(payload).not.toContain(APP_TOKEN)
      }
    }
    setItemSpy.mockRestore()
  })
})
