// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { describe, expect, it, vi, beforeEach } from 'vitest'
import { http, HttpResponse } from 'msw'
import { ProjectProvider } from '../../../entities/project'
import { SessionRecoveryActions } from './SessionRecoveryActions'
import { useMswServer } from '../../../../tests/support/msw'
import type { SessionRecoveryResult } from '../../../entities/coder-session'

let _compactData: SessionRecoveryResult | null = null
let _compactError: { status: number; message: string } | null = null
let _resetData: SessionRecoveryResult | null = null
let _resetError: { status: number; message: string } | null = null

const compactHandler = vi.fn(({ request }: { request: Request }) => {
  void request
  if (_compactError) {
    return HttpResponse.json({ success: false, error: _compactError.message }, { status: _compactError.status })
  }
  return HttpResponse.json({ success: true, data: _compactData ?? { id: '', status: 'completed', wasCompacted: false } })
})

const resetHandler = vi.fn(({ request }: { request: Request }) => {
  void request
  if (_resetError) {
    return HttpResponse.json({ success: false, error: _resetError.message }, { status: _resetError.status })
  }
  return HttpResponse.json({ success: true, data: _resetData ?? { id: '', status: 'completed', wasCompacted: false } })
})

useMswServer(
  http.post('*/api/projects/:projectId/issues/:number/sessions/:name/compact', compactHandler),
  http.post('*/api/projects/:projectId/issues/:number/sessions/:name/reset', resetHandler),
)

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
  compactHandler.mockClear()
  resetHandler.mockClear()
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

  it('disables both buttons and shows the tooltip wrapper when status is running', () => {
    renderActions({ status: 'running' })
    const compact = screen.getByTestId('session-recovery-compact')
    const reset = screen.getByTestId('session-recovery-reset')
    expect(compact).toBeDisabled()
    expect(reset).toBeDisabled()
    expect(compact).toHaveAttribute('data-active', 'true')
    expect(reset).toHaveAttribute('data-active', 'true')
    expect(compact).toHaveAttribute('title', 'Unavailable while session is active')
    expect(reset).toHaveAttribute('title', 'Unavailable while session is active')
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
      expect(compactHandler).toHaveBeenCalledTimes(1)
    })
    const url = new URL(compactHandler.mock.calls[0]![0].request.url)
    expect(url.pathname).toContain('/issues/110/sessions/session-abc/compact')
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
    expect(compactHandler).toHaveBeenCalledTimes(1)
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

    expect(compactHandler).not.toHaveBeenCalled()
  })
})

describe('SessionRecoveryActions — reset action and confirmation dialog', () => {
  it('opens a confirmation dialog when Reset is clicked on an inactive session', () => {
    renderActions({ status: 'completed' })

    fireEvent.click(screen.getByTestId('session-recovery-reset'))

    const dialog = screen.getByTestId('session-recovery-reset-dialog')
    expect(dialog).toBeInTheDocument()
    expect(dialog).toHaveTextContent('This will clear all session context. The agent will lose all conversation history.')
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
    expect(resetHandler).not.toHaveBeenCalled()
  })

  it('closes the dialog and does not call the API when Cancel is clicked', async () => {
    renderActions({ status: 'completed' })
    fireEvent.click(screen.getByTestId('session-recovery-reset'))

    fireEvent.click(screen.getByTestId('session-recovery-reset-cancel'))

    await waitFor(() => {
      expect(screen.queryByTestId('session-recovery-reset-dialog')).not.toBeInTheDocument()
    })
    expect(resetHandler).not.toHaveBeenCalled()
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
      expect(resetHandler).toHaveBeenCalledTimes(1)
    })
    const url = new URL(resetHandler.mock.calls[0]![0].request.url)
    expect(url.pathname).toContain('/issues/110/sessions/session-abc/reset')
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
          />
        </ProjectProvider>
      </QueryClientProvider>,
    )

    expect(screen.queryByTestId('session-recovery-error')).not.toBeInTheDocument()
  })
})
