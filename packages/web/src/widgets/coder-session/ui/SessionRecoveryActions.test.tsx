import '@testing-library/jest-dom'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { describe, expect, it, vi, beforeEach } from 'vitest'
import { ProjectProvider } from '../../../entities/project'
import {
  SessionRecoveryActions,
  type SessionRecoveryActionsClients,
} from './SessionRecoveryActions'
import type { SessionRecoveryResult } from '../../../entities/coder-session'
import { ApiError } from '../../../shared/api/client'

let _compactData: SessionRecoveryResult | null = null
let _compactError: { status: number; message: string } | null = null
let _resetData: SessionRecoveryResult | null = null
let _resetError: { status: number; message: string } | null = null

const compactClient = vi.fn<SessionRecoveryActionsClients['compact']>(async () => {
  if (_compactError) throw new ApiError(_compactError.message, _compactError.status)
  return _compactData ?? { id: '', status: 'completed', wasCompacted: false }
})

const resetClient = vi.fn<SessionRecoveryActionsClients['reset']>(async () => {
  if (_resetError) throw new ApiError(_resetError.message, _resetError.status)
  return _resetData ?? { id: '', status: 'completed', wasCompacted: false }
})

const recoveryClients: SessionRecoveryActionsClients = {
  compact: compactClient,
  reset: resetClient,
}

function createQueryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
}

function renderActions(props: Partial<React.ComponentProps<typeof SessionRecoveryActions>> = {}) {
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
        <SessionRecoveryActions
          issueNumber={110}
          sessionName="session-abc"
          status="completed"
          clients={recoveryClients}
          {...props}
        />
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

function makeCompactResult(overrides?: Partial<SessionRecoveryResult>): SessionRecoveryResult {
  return {
    id: '',
    status: 'completed',
    wasCompacted: false,
    ...overrides,
  }
}

beforeEach(() => {
  compactClient.mockClear()
  resetClient.mockClear()
  _compactData = null
  _compactError = null
  _resetData = null
  _resetError = null
})

describe('SessionRecoveryActions — visibility and enabled/disabled states', () => {
  it('renders both Compact and Reset buttons', () => {
    renderActions({ status: 'completed' })
    expect(screen.getByTestId('session-recovery-compact')).toBeInTheDocument()
    expect(screen.getByTestId('session-recovery-reset')).toBeInTheDocument()
  })

  it('enables both buttons for completed sessions', () => {
    renderActions({ status: 'completed' })
    const compact = screen.getByTestId('session-recovery-compact')
    const reset = screen.getByTestId('session-recovery-reset')
    expect(compact).not.toBeDisabled()
    expect(reset).not.toBeDisabled()
    expect(compact).toHaveAttribute('data-active', 'false')
    expect(reset).toHaveAttribute('data-active', 'false')
  })

  it('enables both buttons for failed sessions', () => {
    renderActions({ status: 'failed' })
    expect(screen.getByTestId('session-recovery-compact')).not.toBeDisabled()
    expect(screen.getByTestId('session-recovery-reset')).not.toBeDisabled()
  })

  it('enables both buttons for cancelled sessions', () => {
    renderActions({ status: 'cancelled' })
    expect(screen.getByTestId('session-recovery-compact')).not.toBeDisabled()
    expect(screen.getByTestId('session-recovery-reset')).not.toBeDisabled()
  })

  it('enables both buttons for probing sessions', () => {
    renderActions({ status: 'probing' })
    expect(screen.getByTestId('session-recovery-compact')).not.toBeDisabled()
    expect(screen.getByTestId('session-recovery-reset')).not.toBeDisabled()
  })

  it('disables both buttons when status is running and drops the native title attribute', () => {
    renderActions({ status: 'running' })
    const compact = screen.getByTestId('session-recovery-compact')
    const reset = screen.getByTestId('session-recovery-reset')
    expect(compact).toBeDisabled()
    expect(reset).toBeDisabled()
    expect(compact).toHaveAttribute('data-active', 'true')
    expect(reset).toHaveAttribute('data-active', 'true')
    expect(compact).not.toHaveAttribute('title')
    expect(reset).not.toHaveAttribute('title')
  })

  it('disables both buttons for the legacy "active" status', () => {
    renderActions({ status: 'active' })
    expect(screen.getByTestId('session-recovery-compact')).toBeDisabled()
    expect(screen.getByTestId('session-recovery-reset')).toBeDisabled()
  })

  it('disables both buttons for the "live" status kind', () => {
    renderActions({ status: 'live' })
    expect(screen.getByTestId('session-recovery-compact')).toBeDisabled()
    expect(screen.getByTestId('session-recovery-reset')).toBeDisabled()
  })
})

describe('SessionRecoveryActions — structured disabled-reason tooltip', () => {
  function focusDisabledWrapper(buttonTestId: string): HTMLElement {
    const button = screen.getByTestId(buttonTestId)
    const wrapper = button.parentElement
    if (!wrapper) throw new Error(`${buttonTestId} has no parent wrapper`)
    expect(wrapper).toHaveAttribute('tabindex', '0')
    fireEvent.focus(wrapper)
    return wrapper
  }

  it('renders the running-block structured tooltip when the session is running', () => {
    renderActions({ status: 'running' })

    focusDisabledWrapper('session-recovery-compact')

    const tooltip = screen.getByRole('tooltip')
    expect(tooltip).toHaveTextContent('Session is running')
    expect(tooltip).toHaveTextContent(
      /finish or cancel the session before compacting or resetting/i,
    )

    fireEvent.blur(screen.getByTestId('session-recovery-compact').parentElement as HTMLElement)
    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument()
  })

  it('renders the running-block structured tooltip on the Reset button when the session is running', () => {
    renderActions({ status: 'running' })

    focusDisabledWrapper('session-recovery-reset')

    const tooltip = screen.getByRole('tooltip')
    expect(tooltip).toHaveTextContent('Session is running')
    expect(tooltip).toHaveTextContent(
      /finish or cancel the session before compacting or resetting/i,
    )
  })

  it('does not wrap enabled buttons with a disabled-reason tooltip', () => {
    renderActions({ status: 'completed' })

    const compact = screen.getByTestId('session-recovery-compact')
    const reset = screen.getByTestId('session-recovery-reset')

    expect(compact.parentElement).not.toHaveAttribute('tabindex', '0')
    expect(reset.parentElement).not.toHaveAttribute('tabindex', '0')
    expect(compact).not.toHaveAttribute('title')
    expect(reset).not.toHaveAttribute('title')

    fireEvent.mouseEnter(compact)
    fireEvent.mouseEnter(reset)
    fireEvent.focus(compact)
    fireEvent.focus(reset)

    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument()
  })
})

describe('SessionRecoveryActions — compact action', () => {
  it('calls compactSession with issue number and session name', async () => {
    _compactData = makeCompactResult({
      id: 'session-abc',
      status: 'completed',
      contextWindowUsed: 10_000,
      contextWindowSize: 200_000,
      contextUsagePercent: 5,
      wasCompacted: true,
    })
    const onSuccess = vi.fn()
    renderActions({ status: 'completed', onSuccess })

    fireEvent.click(screen.getByTestId('session-recovery-compact'))

    await waitFor(() => {
      expect(compactClient).toHaveBeenCalledTimes(1)
    })
    expect(compactClient).toHaveBeenCalledWith(110, 'session-abc', 'proj-1', expect.any(String))
    await waitFor(() => {
      expect(onSuccess).toHaveBeenCalledTimes(1)
    })
  })

  it('invokes the onSuccess callback after a successful compact so the page can refresh', async () => {
    _compactData = makeCompactResult({ id: 'session-abc', status: 'completed', wasCompacted: true })
    const onSuccess = vi.fn()
    renderActions({ status: 'failed', onSuccess })

    fireEvent.click(screen.getByTestId('session-recovery-compact'))

    await waitFor(() => {
      expect(onSuccess).toHaveBeenCalledTimes(1)
    })
    expect(compactClient).toHaveBeenCalledTimes(1)
  })

  it('shows an inline error when the server returns 409 (session active)', async () => {
    _compactError = { status: 409, message: 'Cannot compact while session is active' }
    renderActions({ status: 'completed' })

    fireEvent.click(screen.getByTestId('session-recovery-compact'))

    await waitFor(() => {
      expect(screen.getByTestId('session-recovery-error')).toBeInTheDocument()
    })
    expect(screen.getByTestId('session-recovery-error')).toHaveTextContent(/cannot compact while session is active/i)
  })

  it('shows an inline error when the server returns 404 (session not found)', async () => {
    _compactError = { status: 404, message: 'Session not found' }
    renderActions({ status: 'completed' })

    fireEvent.click(screen.getByTestId('session-recovery-compact'))

    await waitFor(() => {
      expect(screen.getByTestId('session-recovery-error')).toBeInTheDocument()
    })
    expect(screen.getByTestId('session-recovery-error')).toHaveTextContent(/session not found/i)
  })

  it('does not call compactSession when the session is running', () => {
    renderActions({ status: 'running' })

    const compact = screen.getByTestId('session-recovery-compact')
    expect(compact).toBeDisabled()
    fireEvent.click(compact)

    expect(compactClient).not.toHaveBeenCalled()
  })
})

describe('SessionRecoveryActions — reset action and confirmation dialog', () => {
  it('opens a confirmation dialog when Reset is clicked on an inactive session', () => {
    renderActions({ status: 'completed' })

    fireEvent.click(screen.getByTestId('session-recovery-reset'))

    const dialog = screen.getByTestId('session-recovery-reset-dialog')
    expect(dialog).toBeInTheDocument()
    expect(dialog).toHaveTextContent('A new runtime session will start without prior context. Transcript and audit history remain available.')
  })

  it('renders Cancel and "Reset Session" buttons inside the dialog', () => {
    renderActions({ status: 'completed' })
    fireEvent.click(screen.getByTestId('session-recovery-reset'))

    expect(screen.getByTestId('session-recovery-reset-cancel')).toBeInTheDocument()
    expect(screen.getByTestId('session-recovery-reset-confirm')).toBeInTheDocument()
    expect(screen.getByTestId('session-recovery-reset-confirm')).toHaveTextContent('Reset Session')
  })

  it('does not call resetSession while the dialog is open', () => {
    renderActions({ status: 'completed' })
    fireEvent.click(screen.getByTestId('session-recovery-reset'))
    expect(resetClient).not.toHaveBeenCalled()
  })

  it('closes the dialog and does not call the API when Cancel is clicked', async () => {
    renderActions({ status: 'completed' })
    fireEvent.click(screen.getByTestId('session-recovery-reset'))

    fireEvent.click(screen.getByTestId('session-recovery-reset-cancel'))

    await waitFor(() => {
      expect(screen.queryByTestId('session-recovery-reset-dialog')).not.toBeInTheDocument()
    })
    expect(resetClient).not.toHaveBeenCalled()
  })

  it('does not open the dialog when the session is running', () => {
    renderActions({ status: 'running' })
    const reset = screen.getByTestId('session-recovery-reset')
    expect(reset).toBeDisabled()
    fireEvent.click(reset)
    expect(screen.queryByTestId('session-recovery-reset-dialog')).not.toBeInTheDocument()
  })

  it('calls resetSession with the correct parameters when confirmed', async () => {
    _resetData = makeCompactResult({ id: 'session-abc', status: 'completed', wasCompacted: false })
    const onSuccess = vi.fn()
    renderActions({ status: 'failed', onSuccess })

    fireEvent.click(screen.getByTestId('session-recovery-reset'))
    fireEvent.click(screen.getByTestId('session-recovery-reset-confirm'))

    await waitFor(() => {
      expect(resetClient).toHaveBeenCalledTimes(1)
    })
    expect(resetClient).toHaveBeenCalledWith(110, 'session-abc', 'proj-1', expect.any(String))
    await waitFor(() => {
      expect(onSuccess).toHaveBeenCalledTimes(1)
    })
    await waitFor(() => {
      expect(screen.queryByTestId('session-recovery-reset-dialog')).not.toBeInTheDocument()
    })
  })

  it('shows an inline error and keeps the dialog open when resetSession returns 409', async () => {
    _resetError = { status: 409, message: 'Cannot reset while session is active' }
    renderActions({ status: 'completed' })

    fireEvent.click(screen.getByTestId('session-recovery-reset'))
    fireEvent.click(screen.getByTestId('session-recovery-reset-confirm'))

    await waitFor(() => {
      expect(screen.getByTestId('session-recovery-error')).toBeInTheDocument()
    })
    expect(screen.getByTestId('session-recovery-error')).toHaveTextContent(/cannot reset while session is active/i)
    expect(screen.getByTestId('session-recovery-reset-dialog')).toBeInTheDocument()
  })

  it('shows a "Session not found" error when resetSession returns 404', async () => {
    _resetError = { status: 404, message: 'Session not found' }
    renderActions({ status: 'completed' })

    fireEvent.click(screen.getByTestId('session-recovery-reset'))
    fireEvent.click(screen.getByTestId('session-recovery-reset-confirm'))

    await waitFor(() => {
      expect(screen.getByTestId('session-recovery-error')).toBeInTheDocument()
    })
    expect(screen.getByTestId('session-recovery-error')).toHaveTextContent(/session not found/i)
  })

  it('clears the inline error when the user changes the session status', async () => {
    const { rerender } = render(
      <QueryClientProvider client={createQueryClient()}>
        <ProjectProvider initialProjectId="proj-1" initialProjects={[{
          id: 'proj-1',
          name: 'Test',
          createdAt: '2026-01-01T00:00:00Z',
          updatedAt: '2026-01-01T00:00:00Z',
          repositories: [],
        }]}>
          <SessionRecoveryActions
            issueNumber={110}
            sessionName="session-abc"
            status="completed"
            clients={recoveryClients}
          />
        </ProjectProvider>
      </QueryClientProvider>,
    )
    _compactError = { status: 409, message: 'Cannot compact while session is active' }
    fireEvent.click(screen.getByTestId('session-recovery-compact'))

    await waitFor(() => {
      expect(screen.getByTestId('session-recovery-error')).toBeInTheDocument()
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
          <SessionRecoveryActions
            issueNumber={110}
            sessionName="session-abc"
            status="failed"
            clients={recoveryClients}
          />
        </ProjectProvider>
      </QueryClientProvider>,
    )

    expect(screen.queryByTestId('session-recovery-error')).not.toBeInTheDocument()
  })
})
