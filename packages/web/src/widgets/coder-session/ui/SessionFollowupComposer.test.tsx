import '@testing-library/jest-dom'
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { describe, expect, it, vi, beforeEach, type Mock } from 'vitest'
import { SessionFollowupComposer } from './SessionFollowupComposer'

const onSendMock: Mock<(text: string) => Promise<void>> = vi.fn()

function renderComposer(props: Partial<React.ComponentProps<typeof SessionFollowupComposer>> = {}) {
  return render(
    <SessionFollowupComposer
      onSend={onSendMock}
      {...props}
    />,
  )
}

beforeEach(() => {
  onSendMock.mockReset()
  onSendMock.mockResolvedValue(undefined)
})

describe('SessionFollowupComposer — visibility and enabled state', () => {
  it('renders a textarea and send button when not disabled', () => {
    renderComposer()
    expect(screen.getByTestId('session-followup-composer')).toBeInTheDocument()
    expect(screen.getByTestId('session-followup-input')).toBeInTheDocument()
    expect(screen.getByTestId('session-followup-send')).toBeInTheDocument()
  })

  it('disables the send button when textarea is empty', () => {
    renderComposer()
    expect(screen.getByTestId('session-followup-send')).toBeDisabled()
  })

  it('disables the send button when textarea contains only whitespace', () => {
    renderComposer()
    const input = screen.getByTestId('session-followup-input') as HTMLTextAreaElement
    fireEvent.change(input, { target: { value: '   \n  ' } })
    expect(screen.getByTestId('session-followup-send')).toBeDisabled()
  })

  it('enables the send button once non-whitespace text is typed', () => {
    renderComposer()
    const input = screen.getByTestId('session-followup-input') as HTMLTextAreaElement
    fireEvent.change(input, { target: { value: 'hello' } })
    expect(screen.getByTestId('session-followup-send')).not.toBeDisabled()
  })
})

describe('SessionFollowupComposer — disabled (unknown activity)', () => {
  it('renders the unavailable banner when disabled prop is true', () => {
    renderComposer({ disabled: true })
    const composer = screen.getByTestId('session-followup-composer')
    expect(composer).toHaveAttribute('data-disabled', 'true')
    // New activity model: sessions never reach a terminal state; the
    // composer is unavailable only when activity is 'unknown'.
    expect(composer).toHaveTextContent(/activity is unknown/i)
    expect(screen.queryByTestId('session-followup-input')).not.toBeInTheDocument()
    expect(screen.queryByTestId('session-followup-send')).not.toBeInTheDocument()
  })

  it('does not call onSend when disabled', () => {
    renderComposer({ disabled: true })
    expect(onSendMock).not.toHaveBeenCalled()
  })
})

describe('SessionFollowupComposer — sending and success state', () => {
  it('calls onSend with trimmed text on submit', async () => {
    renderComposer()

    const input = screen.getByTestId('session-followup-input') as HTMLTextAreaElement
    fireEvent.change(input, { target: { value: '  please add a logout button  ' } })
    fireEvent.click(screen.getByTestId('session-followup-send'))

    await waitFor(() => {
      expect(onSendMock).toHaveBeenCalledTimes(1)
    })
    expect(onSendMock).toHaveBeenCalledWith('please add a logout button')
  })

  it('clears the textarea and shows a brief sent state on success', async () => {
    renderComposer()

    const input = screen.getByTestId('session-followup-input') as HTMLTextAreaElement
    fireEvent.change(input, { target: { value: 'add login button' } })
    fireEvent.click(screen.getByTestId('session-followup-send'))

    await waitFor(() => {
      expect((screen.getByTestId('session-followup-input') as HTMLTextAreaElement).value).toBe('')
    })

    const sendButton = screen.getByTestId('session-followup-send')
    expect(sendButton).toBeDisabled()
    expect(sendButton).toHaveAttribute('data-state', 'sent')
    expect(screen.getByTestId('session-followup-status')).toHaveTextContent(/sent/i)
  })

  it('submits when Enter is pressed without Shift', async () => {
    renderComposer()

    const input = screen.getByTestId('session-followup-input') as HTMLTextAreaElement
    fireEvent.change(input, { target: { value: 'press enter' } })
    fireEvent.keyDown(input, { key: 'Enter', shiftKey: false })

    await waitFor(() => {
      expect(onSendMock).toHaveBeenCalledTimes(1)
    })
  })

  it('does not submit when Enter is pressed with Shift held', () => {
    onSendMock.mockResolvedValue(undefined)
    renderComposer()

    const input = screen.getByTestId('session-followup-input') as HTMLTextAreaElement
    fireEvent.change(input, { target: { value: 'multiline' } })
    fireEvent.keyDown(input, { key: 'Enter', shiftKey: true })

    expect(onSendMock).not.toHaveBeenCalled()
  })

  it('does not call onSend when send button is clicked with empty text', () => {
    renderComposer()
    const button = screen.getByTestId('session-followup-send')
    expect(button).toBeDisabled()
    fireEvent.click(button)
    expect(onSendMock).not.toHaveBeenCalled()
  })

  it('clears inline error from a prior failed send before retrying', async () => {
    onSendMock
      .mockRejectedValueOnce(new Error('Failed'))
      .mockResolvedValueOnce(undefined)

    renderComposer()
    const input = screen.getByTestId('session-followup-input') as HTMLTextAreaElement
    fireEvent.change(input, { target: { value: 'first attempt' } })
    fireEvent.click(screen.getByTestId('session-followup-send'))

    await waitFor(() => {
      expect(screen.getByTestId('session-followup-error')).toBeInTheDocument()
    })

    fireEvent.change(input, { target: { value: 'second attempt' } })
    fireEvent.click(screen.getByTestId('session-followup-send'))

    await waitFor(() => {
      expect(onSendMock).toHaveBeenCalledTimes(2)
    })
    expect(onSendMock).toHaveBeenNthCalledWith(2, 'second attempt')
  })
})

describe('SessionFollowupComposer — error handling', () => {
  it('shows an inline error when onSend rejects', async () => {
    onSendMock.mockRejectedValue(new Error('Failed to send message'))
    renderComposer()

    const input = screen.getByTestId('session-followup-input') as HTMLTextAreaElement
    fireEvent.change(input, { target: { value: 'add logout' } })
    fireEvent.click(screen.getByTestId('session-followup-send'))

    await waitFor(() => {
      expect(screen.getByTestId('session-followup-error')).toBeInTheDocument()
    })
    expect(screen.getByTestId('session-followup-error')).toHaveTextContent(
      /failed to send message/i,
    )
    expect((screen.getByTestId('session-followup-input') as HTMLTextAreaElement).value).toBe('add logout')
    expect(screen.getByTestId('session-followup-send')).not.toBeDisabled()
  })

  it('does not throw an uncaught exception when onSend rejects', async () => {
    onSendMock.mockRejectedValue(new Error('Boom'))

    renderComposer()

    const input = screen.getByTestId('session-followup-input') as HTMLTextAreaElement
    fireEvent.change(input, { target: { value: 'hi' } })

    await act(async () => {
      fireEvent.click(screen.getByTestId('session-followup-send'))
    })

    await waitFor(() => {
      expect(screen.getByTestId('session-followup-error')).toBeInTheDocument()
    })
  })
})

describe('SessionFollowupComposer — disabled transition clears prior errors', () => {
  it('clears the inline error when the composer becomes disabled', async () => {
    onSendMock.mockRejectedValue(new Error('Offline'))

    const { rerender } = render(
      <SessionFollowupComposer onSend={onSendMock} />,
    )

    const input = screen.getByTestId('session-followup-input') as HTMLTextAreaElement
    fireEvent.change(input, { target: { value: 'first' } })
    fireEvent.click(screen.getByTestId('session-followup-send'))

    await waitFor(() => {
      expect(screen.getByTestId('session-followup-error')).toBeInTheDocument()
    })

    rerender(
      <SessionFollowupComposer onSend={onSendMock} disabled />,
    )

    expect(screen.queryByTestId('session-followup-error')).not.toBeInTheDocument()
    expect(screen.getByTestId('session-followup-composer')).toHaveAttribute('data-disabled', 'true')
  })
})

describe('SessionFollowupComposer — three-state data-state attribute', () => {
  it('renders data-state="interactive" by default', () => {
    renderComposer()
    expect(screen.getByTestId('session-followup-composer')).toHaveAttribute('data-state', 'interactive')
  })

  it('renders data-state="unavailable" when disabled is true', () => {
    renderComposer({ disabled: true })
    const composer = screen.getByTestId('session-followup-composer')
    // Activity model: 'closed' terminal state is gone; unknown activity
    // resolves to 'unavailable'.
    expect(composer).toHaveAttribute('data-state', 'unavailable')
    expect(composer).toHaveAttribute('data-disabled', 'true')
  })

  it('renders data-state="unavailable" when state="closed" is explicitly passed overriding disabled=false', () => {
    renderComposer({ state: 'closed' })
    const composer = screen.getByTestId('session-followup-composer')
    expect(composer).toHaveAttribute('data-state', 'unavailable')
    expect(composer).toHaveAttribute('data-disabled', 'true')
    expect(composer).toHaveTextContent(/activity is unknown/i)
  })

  it('keeps the input disabled when disabled=true overrides an interactive visual state', () => {
    renderComposer({ disabled: true, state: 'interactive' })

    expect(screen.getByTestId('session-followup-composer')).toHaveAttribute('data-state', 'interactive')
    expect(screen.getByTestId('session-followup-input')).toBeDisabled()
    expect(screen.getByTestId('session-followup-send')).toBeDisabled()
  })

  it('renders data-state="queued" when hasQueuedFollowup is true', () => {
    renderComposer({ hasQueuedFollowup: true })
    expect(screen.getByTestId('session-followup-composer')).toHaveAttribute('data-state', 'queued')
  })

  it('renders data-state="queued" when isSending is true and hasQueuedFollowup is unset', () => {
    renderComposer({ isSending: true })
    expect(screen.getByTestId('session-followup-composer')).toHaveAttribute('data-state', 'queued')
  })
})

describe('SessionFollowupComposer — unavailable banner copy (activity unknown)', () => {
  // Issue 484: sessions never reach a terminal/ended state. The disabled
  // composer now renders a single activity-unknown banner regardless of any
  // legacy `endedAt` value (the prop is no longer rendered). The former
  // endedAt relative-time scenarios are obsolete under the activity model.
  it('renders the activity-unknown banner and ignores legacy endedAt', () => {
    const endedAt = new Date(Date.now() - 8 * 60 * 60 * 1000).toISOString()
    renderComposer({ disabled: true, endedAt })

    const composer = screen.getByTestId('session-followup-composer')
    expect(composer).toHaveAttribute('data-state', 'unavailable')
    expect(composer).toHaveTextContent(/activity is unknown/i)
    expect(composer).not.toHaveTextContent(/session ended/i)
  })

  it('renders the activity-unknown banner when endedAt is absent', () => {
    renderComposer({ disabled: true })
    const composer = screen.getByTestId('session-followup-composer')
    expect(composer).toHaveTextContent(/activity is unknown/i)
    expect(composer).not.toHaveTextContent(/session ended/i)
  })

  it('renders the activity-unknown banner when endedAt is null', () => {
    renderComposer({ disabled: true, endedAt: null })
    const composer = screen.getByTestId('session-followup-composer')
    expect(composer).toHaveTextContent(/activity is unknown/i)
  })

  it('renders the activity-unknown banner when endedAt is an unparseable string', () => {
    renderComposer({ disabled: true, endedAt: 'not-a-date' })
    const composer = screen.getByTestId('session-followup-composer')
    expect(composer).toHaveTextContent(/activity is unknown/i)
    expect(composer).not.toHaveTextContent(/session ended/i)
  })
})

describe('SessionFollowupComposer — queued-state persistent indicator', () => {
  it('keeps the input enabled and shows the queued indicator when hasQueuedFollowup is true', () => {
    renderComposer({ hasQueuedFollowup: true })

    expect(screen.getByTestId('session-followup-input')).not.toBeDisabled()
    expect(screen.getByTestId('session-followup-send')).toBeDisabled()
    expect(screen.getByTestId('session-followup-status')).toHaveTextContent(/queued.*waiting for agent/i)
    expect(screen.getByTestId('session-followup-composer')).toHaveAttribute('data-state', 'queued')
  })

  it('disables the input and shows the queued indicator when isSending is true with no hasQueuedFollowup', () => {
    renderComposer({ isSending: true })

    expect(screen.getByTestId('session-followup-input')).toBeDisabled()
    expect(screen.getByTestId('session-followup-send')).toBeDisabled()
    expect(screen.getByTestId('session-followup-status')).toHaveTextContent(/queued.*waiting for agent/i)
  })

  it('keeps the queued indicator visible and accepts another input after its own send settles', () => {
    const { rerender } = render(
      <SessionFollowupComposer onSend={onSendMock} isSending hasQueuedFollowup />,
    )

    expect(screen.getByTestId('session-followup-status')).toHaveTextContent(/queued.*waiting for agent/i)
    expect(screen.getByTestId('session-followup-input')).toBeDisabled()

    rerender(
      <SessionFollowupComposer onSend={onSendMock} isSending={false} hasQueuedFollowup />,
    )

    expect(screen.getByTestId('session-followup-status')).toHaveTextContent(/queued.*waiting for agent/i)
    expect(screen.getByTestId('session-followup-input')).not.toBeDisabled()
    expect(screen.getByTestId('session-followup-composer')).toHaveAttribute('data-state', 'queued')
  })

  it('returns to interactive when hasQueuedFollowup flips back to false', () => {
    const { rerender } = render(
      <SessionFollowupComposer onSend={onSendMock} hasQueuedFollowup />,
    )

    expect(screen.getByTestId('session-followup-composer')).toHaveAttribute('data-state', 'queued')

    rerender(
      <SessionFollowupComposer onSend={onSendMock} hasQueuedFollowup={false} />,
    )

    expect(screen.getByTestId('session-followup-composer')).toHaveAttribute('data-state', 'interactive')
    expect(screen.getByTestId('session-followup-input')).not.toBeDisabled()
  })

  it('suppresses the transient Sent flash when hasQueuedFollowup is supplied', async () => {
    onSendMock.mockResolvedValue(undefined)
    const { rerender } = renderComposer({ hasQueuedFollowup: true, isSending: true })

    rerender(
      <SessionFollowupComposer
        onSend={onSendMock}
        isSending={false}
        hasQueuedFollowup
      />,
    )

    const sendButton = screen.getByTestId('session-followup-send')
    expect(sendButton).not.toHaveAttribute('data-state', 'sent')
    expect(screen.getByTestId('session-followup-status')).toHaveTextContent(/queued.*waiting for agent/i)
  })
})

describe('SessionFollowupComposer — observed follow-up status', () => {
  it('shows accepted-pending when the accepted input has a queued turn', () => {
    renderComposer({
      followupStatus: {
        outcome: 'accepted',
        inputAcceptance: 'accepted',
        turnStatus: 'queued',
        inputId: 'input-1',
        turnId: 'turn-1',
      },
    })

    expect(screen.getByTestId('session-followup-status')).toHaveTextContent('Accepted — pending')
    expect(screen.getByTestId('session-followup-status')).toHaveAttribute('data-tone', 'queued')
    expect(screen.getByTestId('session-followup-input')).not.toBeDisabled()
  })

  it('shows executing instead of pending when the observed turn is executing', () => {
    renderComposer({
      hasQueuedFollowup: true,
      followupStatus: {
        outcome: 'accepted',
        inputAcceptance: 'accepted',
        turnStatus: 'executing',
        inputId: 'input-1',
        turnId: 'turn-1',
      },
    })

    expect(screen.getByTestId('session-followup-status')).toHaveTextContent('Executing')
    expect(screen.getByTestId('session-followup-status')).toHaveAttribute('data-tone', 'executing')
    expect(screen.getByTestId('session-followup-status')).not.toHaveTextContent(/pending/i)
  })

  it.each([
    ['completed', 'Completed', 'success', 'text-success'],
    ['failed', 'Failed', 'terminal', 'text-destructive'],
    ['cancelled', 'Cancelled', 'terminal', 'text-warning'],
    ['unknown', 'Unknown', 'terminal', 'text-warning'],
  ] as const)('shows the terminal %s turn status', (turnStatus, label, tone, color) => {
    renderComposer({
      followupStatus: {
        outcome: 'accepted',
        inputAcceptance: 'accepted',
        turnStatus,
        inputId: 'input-1',
        turnId: 'turn-1',
      },
    })

    const status = screen.getByTestId('session-followup-status')
    expect(status).toHaveTextContent(label)
    expect(status).toHaveAttribute('data-tone', tone)
    expect(status).toHaveClass(color)
    expect(status).not.toHaveClass('text-transparent')
  })

  it.each([
    ['rejected', 'Rejected', 'text-destructive'],
    ['unknown', 'Outcome unknown — retry with the same key', 'text-warning'],
  ] as const)('shows a visible %s outcome', (outcome, label, color) => {
    renderComposer({
      followupStatus: {
        outcome,
        inputAcceptance: null,
        turnStatus: null,
        inputId: null,
        turnId: null,
      },
    })

    const status = screen.getByTestId('session-followup-status')
    expect(status).toHaveTextContent(label)
    expect(status).toHaveClass(color)
    expect(status).not.toHaveClass('text-transparent')
  })
})
