import * as React from 'react'
import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'

import { MaskedCredentialInput } from './masked-credential-input'

const SECRET = 'xapp-1-A-THIS-IS-A-SECRET'

function ControlledCredentialField() {
  const [value, setValue] = React.useState('')
  return (
    <label>
      App token
      <MaskedCredentialInput
        value={value}
        onChange={(event) => setValue(event.target.value)}
        aria-label="App token"
      />
    </label>
  )
}

describe('shared/ui MaskedCredentialInput', () => {
  it('renders a password-typed input and never displays the typed value as cleartext on the page', async () => {
    const user = userEvent.setup()
    render(<ControlledCredentialField />)

    const input = screen.getByLabelText('App token') as HTMLInputElement
    expect(input.type).toBe('password')

    await user.type(input, SECRET)

    expect(input.value).toBe(SECRET)
    expect(input.type).toBe('password')

    const rendered = document.body.textContent ?? ''
    expect(rendered).not.toContain(SECRET)
  })

  it('exposes no reveal/show toggle or any other code path that renders the value in cleartext', () => {
    render(
      <div>
        <MaskedCredentialInput aria-label="App token" defaultValue={SECRET} />
        <MaskedCredentialInput aria-label="Bot token" defaultValue="xoxb-other-secret" />
      </div>,
    )

    const appTokenInput = screen.getByLabelText('App token') as HTMLInputElement
    const botTokenInput = screen.getByLabelText('Bot token') as HTMLInputElement

    expect(appTokenInput.getAttribute('type')).toBe('password')
    expect(botTokenInput.getAttribute('type')).toBe('password')

    expect(screen.queryByRole('button')).not.toBeInTheDocument()

    const rendered = document.body.textContent ?? ''
    expect(rendered).not.toContain(SECRET)
    expect(rendered).not.toContain('xoxb-other-secret')
  })

  it('forwards refs and standard input props while keeping the masked rendering', () => {
    const ref = React.createRef<HTMLInputElement>()
    render(
      <MaskedCredentialInput
        ref={ref}
        name="appToken"
        placeholder="xapp-…"
        required
        aria-label="App token"
      />,
    )

    const input = screen.getByLabelText('App token') as HTMLInputElement
    expect(ref.current).toBe(input)
    expect(input.name).toBe('appToken')
    expect(input.placeholder).toBe('xapp-…')
    expect(input.required).toBe(true)
    expect(input.type).toBe('password')
  })

  it('does not write the value to localStorage, sessionStorage, or the URL while typing or after unmount', async () => {
    const user = userEvent.setup()
    const originalHref = window.location.href
    const originalSearch = window.location.search
    const originalHash = window.location.hash
    const setItemSpy = vi.spyOn(Storage.prototype, 'setItem')

    const { unmount } = render(<ControlledCredentialField />)
    const input = screen.getByLabelText('App token') as HTMLInputElement

    await user.type(input, SECRET)
    unmount()

    expect(window.localStorage.getItem('appToken')).toBeNull()
    expect(window.sessionStorage.getItem('appToken')).toBeNull()
    expect(window.localStorage.length).toBe(0)
    expect(window.sessionStorage.length).toBe(0)
    expect(window.location.href).toBe(originalHref)
    expect(window.location.search).toBe(originalSearch)
    expect(window.location.hash).toBe(originalHash)

    for (const call of setItemSpy.mock.calls) {
      const payload = call[1]
      if (typeof payload === 'string') {
        expect(payload).not.toContain(SECRET)
      }
    }
    setItemSpy.mockRestore()
  })

  it('never logs the value through console.error or console.warn', async () => {
    const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {})
    const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {})
    const user = userEvent.setup()

    render(<ControlledCredentialField />)
    const input = screen.getByLabelText('App token') as HTMLInputElement
    await user.type(input, SECRET)

    expect(input.getAttribute('data-slot')).toBe('masked-credential-input')

    for (const call of [...warnSpy.mock.calls, ...errorSpy.mock.calls]) {
      const text = call.map((value) => String(value)).join(' ')
      expect(text).not.toContain(SECRET)
    }

    warnSpy.mockRestore()
    errorSpy.mockRestore()
  })
})
