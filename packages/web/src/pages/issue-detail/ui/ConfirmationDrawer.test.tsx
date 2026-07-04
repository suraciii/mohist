// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { ConfirmationDrawer } from './ConfirmationDrawer'

function ControlledDrawer({ initialOpen = true }: { initialOpen?: boolean }) {
  const [open, setOpen] = useState(initialOpen)
  return (
    <>
      <button
        type="button"
        data-testid="external-trigger"
        onClick={() => setOpen(true)}
      >
        open
      </button>
      <ConfirmationDrawer
        open={open}
        onClose={() => setOpen(false)}
        testId="confirmation-drawer"
        titleId="confirmation-drawer-title"
        descriptionId="confirmation-drawer-description"
      >
        <div>
          <h2 id="confirmation-drawer-title">Drawer title</h2>
          <p id="confirmation-drawer-description">Drawer description.</p>
          <button type="button" data-testid="first-action">First action</button>
          <button type="button" data-testid="second-action">Second action</button>
        </div>
      </ConfirmationDrawer>
    </>
  )
}

describe('ConfirmationDrawer', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('renders nothing when closed', () => {
    render(<ControlledDrawer initialOpen={false} />)
    expect(screen.queryByTestId('confirmation-drawer')).toBeNull()
  })

  it('renders with role=dialog, aria-modal=true, and the provided ids', () => {
    render(<ControlledDrawer />)
    const drawer = screen.getByTestId('confirmation-drawer')
    expect(drawer).toHaveAttribute('role', 'dialog')
    expect(drawer).toHaveAttribute('aria-modal', 'true')
    expect(drawer).toHaveAttribute('aria-labelledby', 'confirmation-drawer-title')
    expect(drawer).toHaveAttribute('aria-describedby', 'confirmation-drawer-description')
  })

  it('anchors to the bottom of the viewport with z-50 and no dimming scrim', () => {
    const { container } = render(<ControlledDrawer />)
    const drawer = screen.getByTestId('confirmation-drawer')
    expect(drawer.className).toMatch(/\bbottom-0\b/)
    expect(drawer.className).toMatch(/\binset-x-0\b/)
    expect(drawer.className).toMatch(/\bfixed\b/)
    expect(drawer.className).toMatch(/\bz-50\b/)
    expect(drawer.className).not.toMatch(/inset-0/)
    expect(container.querySelector('[data-testid="confirmation-drawer"] [data-testid="confirmation-drawer-scrim"]')).toBeNull()
  })

  it('animates in via slide-in-from-bottom via tw-animate-css', () => {
    render(<ControlledDrawer />)
    const drawer = screen.getByTestId('confirmation-drawer')
    const panel = drawer.firstElementChild as HTMLElement
    expect(panel.className).toMatch(/slide-in-from-bottom-full/)
  })

  it('closes when Escape is pressed and calls onClose', () => {
    render(<ControlledDrawer />)
    expect(screen.getByTestId('confirmation-drawer')).toBeInTheDocument()

    fireEvent.keyDown(document, { key: 'Escape' })

    expect(screen.queryByTestId('confirmation-drawer')).toBeNull()
  })

  it('does not close on non-Escape keys', () => {
    render(<ControlledDrawer />)
    fireEvent.keyDown(document, { key: 'Enter' })
    fireEvent.keyDown(document, { key: ' ' })
    expect(screen.getByTestId('confirmation-drawer')).toBeInTheDocument()
  })

  it('moves focus to the first focusable element in the drawer on open', () => {
    render(<ControlledDrawer />)
    const firstAction = screen.getByTestId('first-action')
    expect(document.activeElement).toBe(firstAction)
  })

  it('traps keyboard focus inside the modal drawer', async () => {
    const user = userEvent.setup()
    render(<ControlledDrawer />)

    const firstAction = screen.getByTestId('first-action')
    const secondAction = screen.getByTestId('second-action')
    const externalTrigger = screen.getByTestId('external-trigger')

    expect(document.activeElement).toBe(firstAction)

    await user.tab()
    expect(document.activeElement).toBe(secondAction)

    await user.tab()
    expect(document.activeElement).toBe(firstAction)

    await user.tab({ shift: true })
    expect(document.activeElement).toBe(secondAction)

    externalTrigger.focus()
    expect(document.activeElement).toBe(firstAction)
  })

  it('blocks pointer interaction outside the sheet region while keeping the overlay transparent', () => {
    render(<ControlledDrawer />)
    const drawer = screen.getByTestId('confirmation-drawer')
    expect(drawer.className).toMatch(/pointer-events-auto/)
    expect(drawer.className).toMatch(/bg-transparent/)
    expect(drawer.className).not.toMatch(/pointer-events-none/)
  })

  it('renders the drawer content with title and description', () => {
    render(<ControlledDrawer />)
    expect(screen.getByRole('heading', { name: 'Drawer title' })).toBeInTheDocument()
    expect(screen.getByText('Drawer description.')).toBeInTheDocument()
  })
})
