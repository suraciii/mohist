import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { TEST_PROJECT, screen, waitFor, baseRender, renderHook, act } from './test-utils'
import { SessionHeader, getSessionStatusLabel } from '../src/widgets/coder-session/ui/SessionHeader'
import { dispatchAgentEvent } from '../src/entities/agent'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ProjectProvider } from '../src/entities/project/model/ProjectContext'
import { MemoryRouter } from 'react-router-dom'
import React from 'react'
import type { CoderSessionItem } from '../src/entities/coder-session'

const apiMocks = vi.hoisted(() => ({
  sessions: [] as CoderSessionItem[],
}))

vi.mock('../src/entities/coder-session/api/client', () => ({
  getCoderSessions: vi.fn(() => Promise.resolve(apiMocks.sessions)),
}))

const queryClients: QueryClient[] = []
beforeEach(() => {
  vi.clearAllMocks()
  apiMocks.sessions = []
})
afterEach(() => {
  for (const qc of queryClients) qc.clear()
  queryClients.length = 0
})

function createQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  })
}

function renderWithProviders(ui: React.ReactElement) {
  const queryClient = createQueryClient()
  queryClients.push(queryClient)
  return baseRender(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
        <MemoryRouter>{ui}</MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

function renderHookWithProviders<T>(callback: () => T) {
  const queryClient = createQueryClient()
  queryClients.push(queryClient)
  return renderHook(callback, {
    wrapper: ({ children }: { children: React.ReactNode }) => (
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
          {children}
        </ProjectProvider>
      </QueryClientProvider>
    ),
  })
}

function makeSession(overrides: Partial<CoderSessionItem> = {}): CoderSessionItem {
  return {
    id: 'session-1',
    acpSessionId: 'acp-1',
    executionId: null,
    taskDescription: null,
    status: 'running',
    createdAt: '2024-01-01T10:00:00.000Z',
    completedAt: null,
    model: null,
    coderType: null,
    stage: null,
    title: null,
    lastDataAt: null,
    probeSentAt: null,
    probeDeadlineAt: null,
    failureReason: null,
    workflowLogs: [],
    ...overrides,
  }
}

describe('getSessionStatusLabel', () => {
  it('returns Running for running status', () => {
    expect(getSessionStatusLabel(makeSession({ status: 'running' }))).toBe('Running')
  })

  it('returns Checking session for probing status', () => {
    expect(getSessionStatusLabel(makeSession({ status: 'probing' }))).toBe('Checking session')
  })

  it('returns Session failed for failed status', () => {
    expect(getSessionStatusLabel(makeSession({ status: 'failed' }))).toBe('Session failed')
  })

  it('returns Completed for completed status', () => {
    expect(getSessionStatusLabel(makeSession({ status: 'completed' }))).toBe('Completed')
  })

  it('returns Cancelled for cancelled status', () => {
    expect(getSessionStatusLabel(makeSession({ status: 'cancelled' }))).toBe('Cancelled')
  })

  it('does not return healthy, quiet, stale, hung-suspected, or recoverable', () => {
    const forbidden = ['healthy', 'quiet', 'stale', 'hung-suspected', 'recoverable']
    const statuses = ['running', 'probing', 'failed', 'completed', 'cancelled']
    for (const status of statuses) {
      const label = getSessionStatusLabel(makeSession({ status }))
      for (const word of forbidden) {
        expect(label.toLowerCase()).not.toContain(word)
      }
    }
  })
})

describe('SessionHeader liveness rendering', () => {
  it('renders running session with Running label', async () => {
    const session = makeSession({ status: 'running' })
    renderWithProviders(<SessionHeader session={session} issueNumber={1} showTranscriptLink />)

    expect(screen.getByText('View transcript')).toBeInTheDocument()
  })

  it('renders probing session with Checking session label', async () => {
    const session = makeSession({ status: 'probing' })
    renderWithProviders(<SessionHeader session={session} issueNumber={1} showTranscriptLink />)

    expect(screen.getByText('Checking session')).toBeInTheDocument()
  })

  it('renders failed session with failureReason when available', async () => {
    const session = makeSession({ status: 'failed', failureReason: 'probe_timeout' })
    renderWithProviders(<SessionHeader session={session} issueNumber={1} showTranscriptLink />)

    expect(screen.getByText('probe_timeout')).toBeInTheDocument()
  })

  it('renders completed session without liveness indicators', async () => {
    const session = makeSession({ status: 'completed', completedAt: '2024-01-01T11:00:00.000Z' })
    renderWithProviders(<SessionHeader session={session} issueNumber={1} showTranscriptLink />)

    expect(screen.queryByText('Checking session')).not.toBeInTheDocument()
    expect(screen.queryByText('probe_timeout')).not.toBeInTheDocument()
  })

  it('does not show failureReason for non-failed sessions', async () => {
    const session = makeSession({ status: 'running', failureReason: null })
    renderWithProviders(<SessionHeader session={session} issueNumber={1} showTranscriptLink />)

    expect(screen.queryByText(/probe_timeout/)).not.toBeInTheDocument()
  })
})

describe('coder_session_status_changed SSE event handling', () => {
  it('updates session status from running to probing via SSE event', async () => {
    const initialSession = makeSession({ id: 'session-1', status: 'running' })
    apiMocks.sessions = [initialSession]

    const { useCoderSessions } = await import('../src/entities/coder-session/model/useCoderSessions')
    const { result } = renderHookWithProviders(() => useCoderSessions(1))

    await waitFor(() => {
      expect(result.current.sessions).toHaveLength(1)
      expect(result.current.sessions[0].status).toBe('running')
    })

    act(() => {
      dispatchAgentEvent('coder_session_status_changed', {
        issueId: '1',
        projectId: 'project-1',
        coderSessionId: 'session-1',
        acpSessionId: 'acp-1',
        status: 'probing',
        lastDataAt: '2024-01-01T10:05:00.000Z',
        probeSentAt: '2024-01-01T10:05:00.000Z',
        probeDeadlineAt: '2024-01-01T10:06:00.000Z',
      })
    })

    await waitFor(() => {
      expect(result.current.sessions[0].status).toBe('probing')
      expect(result.current.sessions[0].probeSentAt).toBe('2024-01-01T10:05:00.000Z')
      expect(result.current.sessions[0].probeDeadlineAt).toBe('2024-01-01T10:06:00.000Z')
    })
  })

  it('updates session status from probing back to running via SSE event', async () => {
    const initialSession = makeSession({ id: 'session-1', status: 'probing', probeSentAt: '2024-01-01T10:05:00.000Z' })
    apiMocks.sessions = [initialSession]

    const { useCoderSessions } = await import('../src/entities/coder-session/model/useCoderSessions')
    const { result } = renderHookWithProviders(() => useCoderSessions(1))

    await waitFor(() => {
      expect(result.current.sessions[0].status).toBe('probing')
    })

    act(() => {
      dispatchAgentEvent('coder_session_status_changed', {
        issueId: '1',
        projectId: 'project-1',
        coderSessionId: 'session-1',
        acpSessionId: 'acp-1',
        status: 'running',
        lastDataAt: '2024-01-01T10:05:30.000Z',
      })
    })

    await waitFor(() => {
      expect(result.current.sessions[0].status).toBe('running')
    })
  })

  it('updates session status to failed with failureReason via SSE event', async () => {
    const initialSession = makeSession({ id: 'session-1', status: 'probing' })
    apiMocks.sessions = [initialSession]

    const { useCoderSessions } = await import('../src/entities/coder-session/model/useCoderSessions')
    const { result } = renderHookWithProviders(() => useCoderSessions(1))

    await waitFor(() => {
      expect(result.current.sessions[0].status).toBe('probing')
    })

    act(() => {
      dispatchAgentEvent('coder_session_status_changed', {
        issueId: '1',
        projectId: 'project-1',
        coderSessionId: 'session-1',
        acpSessionId: 'acp-1',
        status: 'failed',
        failureReason: 'probe_timeout',
      })
    })

    await waitFor(() => {
      expect(result.current.sessions[0].status).toBe('failed')
      expect(result.current.sessions[0].failureReason).toBe('probe_timeout')
    })
  })

  it('ignores SSE event for unknown session ID', async () => {
    const initialSession = makeSession({ id: 'session-1', status: 'running' })
    apiMocks.sessions = [initialSession]

    const { useCoderSessions } = await import('../src/entities/coder-session/model/useCoderSessions')
    const { result } = renderHookWithProviders(() => useCoderSessions(1))

    await waitFor(() => {
      expect(result.current.sessions[0].status).toBe('running')
    })

    act(() => {
      dispatchAgentEvent('coder_session_status_changed', {
        issueId: '1',
        projectId: 'project-1',
        coderSessionId: 'session-unknown',
        acpSessionId: 'acp-1',
        status: 'failed',
        failureReason: 'probe_timeout',
      })
    })

    await waitFor(() => {
      expect(result.current.sessions[0].status).toBe('running')
    })
  })
})
