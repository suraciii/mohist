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

describe('SessionFollowupComposer — disabled (terminal state)', () => {
  it('renders the disabled banner when disabled prop is true', () => {
    renderComposer({ disabled: true })
    const composer = screen.getByTestId('session-followup-composer')
    expect(composer).toHaveAttribute('data-disabled', 'true')
    expect(composer).toHaveTextContent(/no longer accepting followups/i)
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
