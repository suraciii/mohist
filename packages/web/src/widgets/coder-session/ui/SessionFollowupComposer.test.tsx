// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { describe, expect, it, vi, beforeEach } from 'vitest'
import { ProjectProvider } from '../../../entities/project'
import { ApiError } from '../../../shared/api/client'
import { SessionFollowupComposer } from './SessionFollowupComposer'

const apiMocks = vi.hoisted(() => ({
  postFollowup: vi.fn(),
}))

vi.mock('../../../entities/coder-session/api/client', () => ({
  postFollowup: (...args: unknown[]) => apiMocks.postFollowup(...args),
}))

vi.mock('../../../entities/coder-session/model/useFollowupMutation', () => ({
  useFollowupMutation: () => ({
    mutate: (input: unknown, options?: { onSuccess?: (value: unknown) => void; onError?: (err: unknown) => void }) => {
      const promise = apiMocks.postFollowup(input)
      promise
        .then((value: unknown) => options?.onSuccess?.(value))
        .catch((err: unknown) => options?.onError?.(err))
    },
    isPending: false,
  }),
}))

function createQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  })
}

function renderComposer(props: Partial<React.ComponentProps<typeof SessionFollowupComposer>> = {}) {
  const queryClient = createQueryClient()
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj-1" initialProjects={[{
        id: 'proj-1',
        name: 'Test',
        createdAt: '2026-01-01T00:00:00Z',
        updatedAt: '2026-01-01T00:00:00Z',
        repositories: [],
      }]}>
        <SessionFollowupComposer
          issueNumber={42}
          sessionName="session-abc"
          {...props}
        />
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  apiMocks.postFollowup.mockReset()
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

  it('does not call postFollowup when disabled', () => {
    renderComposer({ disabled: true })
    expect(apiMocks.postFollowup).not.toHaveBeenCalled()
  })
})

describe('SessionFollowupComposer — sending and success state', () => {
  it('calls postFollowup with trimmed text, issue number and session name on submit', async () => {
    apiMocks.postFollowup.mockResolvedValue({ status: 'sent' })
    renderComposer()

    const input = screen.getByTestId('session-followup-input') as HTMLTextAreaElement
    fireEvent.change(input, { target: { value: '  please add a logout button  ' } })
    fireEvent.click(screen.getByTestId('session-followup-send'))

    await waitFor(() => {
      expect(apiMocks.postFollowup).toHaveBeenCalledTimes(1)
    })
    expect(apiMocks.postFollowup).toHaveBeenCalledWith({
      issueNumber: 42,
      sessionName: 'session-abc',
      text: 'please add a logout button',
    })
  })

  it('clears the textarea and shows a brief sent state on success', async () => {
    apiMocks.postFollowup.mockResolvedValue({ status: 'sent' })
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
    apiMocks.postFollowup.mockResolvedValue({ status: 'sent' })
    renderComposer()

    const input = screen.getByTestId('session-followup-input') as HTMLTextAreaElement
    fireEvent.change(input, { target: { value: 'press enter' } })
    fireEvent.keyDown(input, { key: 'Enter', shiftKey: false })

    await waitFor(() => {
      expect(apiMocks.postFollowup).toHaveBeenCalledTimes(1)
    })
  })

  it('does not submit when Enter is pressed with Shift held', () => {
    apiMocks.postFollowup.mockResolvedValue({ status: 'sent' })
    renderComposer()

    const input = screen.getByTestId('session-followup-input') as HTMLTextAreaElement
    fireEvent.change(input, { target: { value: 'multiline' } })
    fireEvent.keyDown(input, { key: 'Enter', shiftKey: true })

    expect(apiMocks.postFollowup).not.toHaveBeenCalled()
  })

  it('does not call postFollowup when send button is clicked with empty text', () => {
    renderComposer()
    const button = screen.getByTestId('session-followup-send')
    expect(button).toBeDisabled()
    fireEvent.click(button)
    expect(apiMocks.postFollowup).not.toHaveBeenCalled()
  })

  it('clears inline error from a prior failed send before retrying', async () => {
    apiMocks.postFollowup
      .mockRejectedValueOnce(new ApiError('Session is no longer active.', 409))
      .mockResolvedValueOnce({ status: 'sent' })

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
      expect(apiMocks.postFollowup).toHaveBeenCalledTimes(2)
    })
    expect(apiMocks.postFollowup.mock.calls[1]?.[0]).toEqual({
      issueNumber: 42,
      sessionName: 'session-abc',
      text: 'second attempt',
    })
  })
})

describe('SessionFollowupComposer — error handling', () => {
  it('shows an inline error and keeps the text when the server returns 409', async () => {
    apiMocks.postFollowup.mockRejectedValue(
      new ApiError('Session is no longer active.', 409),
    )
    renderComposer()

    const input = screen.getByTestId('session-followup-input') as HTMLTextAreaElement
    fireEvent.change(input, { target: { value: 'add logout' } })
    fireEvent.click(screen.getByTestId('session-followup-send'))

    await waitFor(() => {
      expect(screen.getByTestId('session-followup-error')).toBeInTheDocument()
    })
    expect(screen.getByTestId('session-followup-error')).toHaveTextContent(
      /session is no longer active/i,
    )
    expect((screen.getByTestId('session-followup-input') as HTMLTextAreaElement).value).toBe('add logout')
    expect(screen.getByTestId('session-followup-send')).not.toBeDisabled()
  })

  it('shows an inline error when the server returns 503 (runner offline)', async () => {
    apiMocks.postFollowup.mockRejectedValue(
      new ApiError('Runner is offline.', 503),
    )
    renderComposer()

    const input = screen.getByTestId('session-followup-input') as HTMLTextAreaElement
    fireEvent.change(input, { target: { value: 'add logout' } })
    fireEvent.click(screen.getByTestId('session-followup-send'))

    await waitFor(() => {
      expect(screen.getByTestId('session-followup-error')).toBeInTheDocument()
    })
    expect(screen.getByTestId('session-followup-error')).toHaveTextContent(/runner is offline/i)
  })

  it('falls back to a generic 409 message when the server body is empty', async () => {
    apiMocks.postFollowup.mockRejectedValue(new ApiError('', 409))
    renderComposer()

    const input = screen.getByTestId('session-followup-input') as HTMLTextAreaElement
    fireEvent.change(input, { target: { value: 'hi' } })
    fireEvent.click(screen.getByTestId('session-followup-send'))

    await waitFor(() => {
      expect(screen.getByTestId('session-followup-error')).toBeInTheDocument()
    })
    expect(screen.getByTestId('session-followup-error')).toHaveTextContent(
      /session is no longer active/i,
    )
  })

  it('falls back to a generic 503 message when the server body is empty', async () => {
    apiMocks.postFollowup.mockRejectedValue(new ApiError('', 503))
    renderComposer()

    const input = screen.getByTestId('session-followup-input') as HTMLTextAreaElement
    fireEvent.change(input, { target: { value: 'hi' } })
    fireEvent.click(screen.getByTestId('session-followup-send'))

    await waitFor(() => {
      expect(screen.getByTestId('session-followup-error')).toBeInTheDocument()
    })
    expect(screen.getByTestId('session-followup-error')).toHaveTextContent(
      /runner is offline/i,
    )
  })

  it('does not throw an uncaught exception when the mutation rejects', async () => {
    apiMocks.postFollowup.mockRejectedValue(new ApiError('Boom', 503))
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
    apiMocks.postFollowup.mockRejectedValue(new ApiError('Offline', 503))

    const { rerender } = render(
      <QueryClientProvider client={createQueryClient()}>
        <ProjectProvider initialProjectId="proj-1" initialProjects={[{
          id: 'proj-1',
          name: 'Test',
          createdAt: '2026-01-01T00:00:00Z',
          updatedAt: '2026-01-01T00:00:00Z',
          repositories: [],
        }]}>
          <SessionFollowupComposer
            issueNumber={42}
            sessionName="session-abc"
          />
        </ProjectProvider>
      </QueryClientProvider>,
    )

    const input = screen.getByTestId('session-followup-input') as HTMLTextAreaElement
    fireEvent.change(input, { target: { value: 'first' } })
    fireEvent.click(screen.getByTestId('session-followup-send'))

    await waitFor(() => {
      expect(screen.getByTestId('session-followup-error')).toBeInTheDocument()
    })

    rerender(
      <QueryClientProvider client={createQueryClient()}>
        <ProjectProvider initialProjectId="proj-1" initialProjects={[{
          id: 'proj-1',
          name: 'Test',
          createdAt: '2026-01-01T00:00:00Z',
          updatedAt: '2026-01-01T00:00:00Z',
          repositories: [],
        }]}>
          <SessionFollowupComposer
            issueNumber={42}
            sessionName="session-abc"
            disabled
          />
        </ProjectProvider>
      </QueryClientProvider>,
    )

    expect(screen.queryByTestId('session-followup-error')).not.toBeInTheDocument()
    expect(screen.getByTestId('session-followup-composer')).toHaveAttribute('data-disabled', 'true')
  })
})
