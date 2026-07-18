import { describe, it, expect, vi } from 'vitest'
import { useState } from 'react'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'

import { AlertDialog } from './alert-dialog'

interface ControlledAlertDialogProps {
  initialOpen?: boolean
  onConfirm?: () => void
  loading?: boolean
  tone?: 'destructive' | 'default'
}

function ControlledAlertDialog({
  initialOpen = false,
  onConfirm = vi.fn(),
  loading = false,
  tone = 'destructive',
}: ControlledAlertDialogProps) {
  const [open, setOpen] = useState(initialOpen)
  const handleConfirm = () => {
    onConfirm()
    setOpen(false)
  }
  return (
    <>
      <button type="button" onClick={() => setOpen(true)} data-testid="open">
        Open
      </button>
      <button type="button" data-testid="outside">
        Outside
      </button>
      <AlertDialog
        open={open}
        onOpenChange={setOpen}
        title="Delete this item?"
        description="This action cannot be undone."
        confirmLabel="Delete"
        cancelLabel="Cancel"
        onConfirm={handleConfirm}
        loading={loading}
        tone={tone}
        data-testid="alert-dialog"
      />
    </>
  )
}

function withinDialog(dialog: HTMLElement) {
  const byTestId = (testId: string): HTMLElement => {
    const el = dialog.querySelector<HTMLElement>(`[data-testid="${testId}"]`)
    if (!el) throw new Error(`No element with data-testid "${testId}" inside dialog`)
    return el
  }
  return {
    getByTestId: byTestId,
    queryByTestId: (testId: string) =>
      dialog.querySelector<HTMLElement>(`[data-testid="${testId}"]`),
  }
}

describe('shared/ui AlertDialog', () => {
  it('moves keyboard focus into the dialog when it opens', async () => {
    const user = userEvent.setup()
    render(<ControlledAlertDialog />)

    const trigger = screen.getByTestId('open')
    trigger.focus()
    await user.click(trigger)

    const dialog = await screen.findByTestId('alert-dialog')
    const cancel = withinDialog(dialog).getByTestId('alert-dialog-cancel')
    cancel.focus()

    expect(dialog.contains(document.activeElement)).toBe(true)
  })

  it('keeps keyboard focus within the dialog by trapping it via the base-ui focus guards', async () => {
    const user = userEvent.setup()
    render(<ControlledAlertDialog />)
    const trigger = screen.getByTestId('open')
    trigger.focus()
    await user.click(trigger)

    const dialog = await screen.findByTestId('alert-dialog')
    const cancel = withinDialog(dialog).getByTestId('alert-dialog-cancel')
    const confirm = withinDialog(dialog).getByTestId('alert-dialog-confirm')
    cancel.focus()
    expect(dialog.contains(document.activeElement)).toBe(true)

    confirm.focus()
    await user.tab()
    const active = document.activeElement as HTMLElement | null
    expect(active).not.toBeNull()
    expect(active === cancel || active?.hasAttribute('data-base-ui-focus-guard')).toBe(true)
    expect(document.activeElement).not.toBe(trigger)
  })

  it('returns keyboard focus to the invoking element when the dialog closes', async () => {
    const user = userEvent.setup()
    render(<ControlledAlertDialog />)

    const trigger = screen.getByTestId('open')
    trigger.focus()
    await user.click(trigger)

    const dialog = await screen.findByTestId('alert-dialog')
    const cancel = withinDialog(dialog).getByTestId('alert-dialog-cancel')
    cancel.focus()
    expect(document.activeElement).toBe(cancel)

    await user.click(cancel)

    await waitFor(() => {
      expect(screen.queryByTestId('alert-dialog')).not.toBeInTheDocument()
    })
    await waitFor(() => {
      expect(document.activeElement).toBe(trigger)
    })
  })

  it('returns keyboard focus to the invoking element after confirm', async () => {
    const user = userEvent.setup()
    render(<ControlledAlertDialog />)

    const trigger = screen.getByTestId('open')
    trigger.focus()
    await user.click(trigger)

    const dialog = await screen.findByTestId('alert-dialog')
    const confirm = withinDialog(dialog).getByTestId('alert-dialog-confirm')
    confirm.focus()
    expect(document.activeElement).toBe(confirm)

    await user.click(confirm)

    await waitFor(() => {
      expect(screen.queryByTestId('alert-dialog')).not.toBeInTheDocument()
    })
    await waitFor(() => {
      expect(document.activeElement).toBe(trigger)
    })
  })

  it('returns keyboard focus to the invoking element after Escape', async () => {
    const user = userEvent.setup()
    render(<ControlledAlertDialog />)

    const trigger = screen.getByTestId('open')
    trigger.focus()
    await user.click(trigger)

    const dialog = await screen.findByTestId('alert-dialog')
    const cancel = withinDialog(dialog).getByTestId('alert-dialog-cancel')
    cancel.focus()
    expect(document.activeElement).toBe(cancel)

    await user.keyboard('{Escape}')

    await waitFor(() => {
      expect(screen.queryByTestId('alert-dialog')).not.toBeInTheDocument()
    })
    await waitFor(() => {
      expect(document.activeElement).toBe(trigger)
    })
  })

  it('dismisses on Escape without invoking onConfirm (acts as cancellation)', async () => {
    const user = userEvent.setup()
    const onConfirm = vi.fn()
    render(<ControlledAlertDialog onConfirm={onConfirm} />)

    await user.click(screen.getByTestId('open'))
    await screen.findByTestId('alert-dialog')

    await user.keyboard('{Escape}')

    await waitFor(() => {
      expect(screen.queryByTestId('alert-dialog')).not.toBeInTheDocument()
    })

    expect(onConfirm).not.toHaveBeenCalled()
  })

  it('invokes onConfirm only when the explicit confirm button is clicked', async () => {
    const user = userEvent.setup()
    const onConfirm = vi.fn()
    render(<ControlledAlertDialog onConfirm={onConfirm} />)

    await user.click(screen.getByTestId('open'))

    const dialog = await screen.findByTestId('alert-dialog')

    await user.click(withinDialog(dialog).getByTestId('alert-dialog-cancel'))
    expect(onConfirm).not.toHaveBeenCalled()

    await user.click(screen.getByTestId('open'))
    await user.click(withinDialog(screen.getByTestId('alert-dialog')).getByTestId('alert-dialog-confirm'))
    expect(onConfirm).toHaveBeenCalledTimes(1)
  })

  it('renders a destructive-tone confirm button and removes the close X by default', async () => {
    const user = userEvent.setup()
    render(<ControlledAlertDialog />)
    await user.click(screen.getByTestId('open'))
    const dialog = await screen.findByTestId('alert-dialog')

    expect(dialog).toHaveAttribute('data-tone', 'destructive')

    const confirm = withinDialog(dialog).getByTestId('alert-dialog-confirm')
    expect(confirm.className).toContain('bg-red-600')

    expect(dialog.querySelector('button[aria-label="Close"]')).toBeNull()
  })

  it('falls back to a non-destructive confirm tone when tone is "default"', async () => {
    const user = userEvent.setup()
    render(<ControlledAlertDialog tone="default" />)
    await user.click(screen.getByTestId('open'))
    const dialog = await screen.findByTestId('alert-dialog')

    expect(dialog).toHaveAttribute('data-tone', 'default')
    const confirm = withinDialog(dialog).getByTestId('alert-dialog-confirm')
    expect(confirm.className).not.toContain('bg-red-600')
  })

  it('does not close while loading and reflects the loading state on both buttons', async () => {
    const onConfirm = vi.fn()
    render(<ControlledAlertDialog onConfirm={onConfirm} loading />)
    const user = userEvent.setup()
    await user.click(screen.getByTestId('open'))
    const dialog = await screen.findByTestId('alert-dialog')

    const cancel = withinDialog(dialog).getByTestId('alert-dialog-cancel') as HTMLButtonElement
    const confirm = withinDialog(dialog).getByTestId('alert-dialog-confirm') as HTMLButtonElement
    expect(cancel.disabled).toBe(true)
    expect(confirm.disabled).toBe(true)
    expect(confirm.textContent).toContain('Working')

    await user.click(cancel)
    expect(onConfirm).not.toHaveBeenCalled()
    expect(screen.queryByTestId('alert-dialog')).toBeInTheDocument()
  })
})
