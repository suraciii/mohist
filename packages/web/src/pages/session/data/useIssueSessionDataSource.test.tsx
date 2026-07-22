import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Routes, Route } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { SessionPage, type SessionPageDependencies } from '../ui/SessionPage'

let _issueData: unknown = null
let _coderSessionsData: unknown[] = []
let _metadataData: unknown = null
let _transcriptData: unknown = null
let _unfilteredTranscriptData: unknown = null

const capturedTranscriptOptions: Array<{
  sessionId: string
  runtimeSessionId: string
  runtime: string | null | undefined
  isHistoricalRuntimeView: boolean | undefined
  isRunning: boolean
}> = []

function resetCapturedOptions() {
  capturedTranscriptOptions.length = 0
}

function baseSession(overrides: Record<string, unknown> = {}) {
  return {
    id: 'canonical-session-id',
    sessionName: 'session-name-abc',
    runtimeSessionId: 'rt-current',
    executionId: null,
    status: 'active',
    taskDescription: 'Test session',
    createdAt: '2026-01-01T00:00:00Z',
    completedAt: null,
    title: 'Test session',
    model: 'gpt-4o',
    changes: [],
    ...overrides,
  }
}

function baseMetadata(overrides: Record<string, unknown> = {}) {
  return {
    id: 'canonical-meta-id',
    sessionName: 'session-name-abc',
    runtimeSessionId: 'rt-current',
    runtime: 'opencode',
    status: 'active',
    statusKind: 'live',
    createdAt: '2026-01-01T00:00:00Z',
    completedAt: null,
    lastActivityAt: '2026-01-01T00:05:00Z',
    lastDataAt: '2026-01-01T00:05:00Z',
    title: 'Test session',
    stage: 'build',
    changedFiles: undefined,
    eventSummary: null,
    usage: null,
    metadata: { partCount: 0, toolCount: 0 },
    model: 'gpt-4o',
    ...overrides,
  }
}

function baseIssue(overrides: Record<string, unknown> = {}) {
  return {
    number: 84,
    title: 'Test Issue',
    status: 'open',
    priority: 'medium',
    labels: [],
    repository: 'master',
    ...overrides,
  }
}

const mocks = {
  transcriptReturn: {
    turns: [] as any[],
    transcriptVersion: 0,
    scrollToBottom: vi.fn(),
    newContentAvailable: false,
    setIsNearBottom: vi.fn(),
    isFinalizing: false,
    isThinking: false,
    isStreaming: false,
  },
}

function makeDeps(): SessionPageDependencies {
  return {
    dataSource: {
      useSessionTranscript: ((options: any) => {
        capturedTranscriptOptions.push({
          sessionId: options.sessionId,
          runtimeSessionId: options.runtimeSessionId,
          runtime: options.runtime,
          isHistoricalRuntimeView: options.isHistoricalRuntimeView,
          isRunning: options.isRunning,
        })
        return mocks.transcriptReturn
      }) as any,
      projectTurn: (turn: any) => turn,
      useIssue: () => ({ data: _issueData }) as never,
      useCoderSessions: () => ({ sessions: _coderSessionsData, isLoading: false, isFetching: false, refetch: vi.fn() }) as never,
      useSiblingSessions: () => ({
        sessions: [],
        currentIndex: -1,
        previous: null,
        next: null,
        hasPrevious: false,
        hasNext: false,
      }),
      getAgentSessionMetadata: async () => _metadataData as never,
      getAgentSessionTranscript: async (_number: any, _name: any, _projectId: any, runtimeSessionId: any) => {
        if (runtimeSessionId) return _transcriptData as never
        return (_unfilteredTranscriptData ?? _transcriptData) as never
      },
      useFollowupMutation: () => ({ mutateAsync: vi.fn(), isPending: false }) as never,
      useCancelSessionMutation: () => ({ mutate: vi.fn(), isPending: false }) as never,
    },
    shellComponents: {
      SessionTranscriptLayout: () => <></>,
      SessionRecoveryActions: () => <></>,
      SessionFollowupComposer: () => <></>,
    },
  }
}

const queryClients: QueryClient[] = []

function createQueryClient() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { retry: false } },
  })
  queryClients.push(queryClient)
  return queryClient
}

async function renderPage(initialEntry: string) {
  const queryClient = createQueryClient()
  const deps = makeDeps()
  return {
    deps,
    ...render(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId="proj-1" initialProjects={[{
          id: 'proj-1', name: 'Test', createdAt: '2026-01-01T00:00:00Z',
          updatedAt: '2026-01-01T00:00:00Z', repositories: [],
        }]}>
          <MemoryRouter initialEntries={[initialEntry]}>
            <Routes>
              <Route path="/issues/:number/session/:sessionId" element={<SessionPage dependencies={deps} />} />
              <Route path="/issues/:number/workflow/sessions/:sessionName" element={<SessionPage dependencies={deps} />} />
            </Routes>
          </MemoryRouter>
        </ProjectProvider>
      </QueryClientProvider>,
    ),
  }
}

describe('useIssueSessionDataSource — canonical session ID wiring', () => {
  beforeEach(() => {
    _issueData = baseIssue()
    _coderSessionsData = [baseSession()]
    _metadataData = baseMetadata()
    _transcriptData = { turns: [], partCount: 0, lastActivityAt: null }
    _unfilteredTranscriptData = null
    resetCapturedOptions()
  })

  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('passes detail.id as canonical sessionId when metadata is available', async () => {
    renderPage('/issues/84/session/canonical-session-id')

    await waitFor(() => {
      const last = capturedTranscriptOptions[capturedTranscriptOptions.length - 1]
      expect(last.sessionId).toBe('canonical-meta-id')
    })
  })

  it('passes session.list id as fallback when metadata is not yet available', async () => {
    _metadataData = null

    renderPage('/issues/84/session/canonical-session-id')

    await waitFor(() => {
      const valid = capturedTranscriptOptions.filter((o) => o.sessionId !== '')
      expect(valid.length).toBeGreaterThan(0)
    })

    const valid = capturedTranscriptOptions.filter((o) => o.sessionId !== '')
    expect(valid[0].sessionId).toBe('canonical-session-id')
  })

  it('never passes sessionName as sessionId on the workflow-sessions route', async () => {
    _metadataData = null

    renderPage('/issues/84/workflow/sessions/session-name-abc')

    await waitFor(() => {
      expect(capturedTranscriptOptions.length).toBeGreaterThan(0)
    })

    for (const opt of capturedTranscriptOptions) {
      expect(opt.sessionId).not.toBe('session-name-abc')
    }
  })

  it('sets isRunning to true during metadata loading', async () => {
    _metadataData = null

    renderPage('/issues/84/session/canonical-session-id')

    await waitFor(() => {
      expect(capturedTranscriptOptions.length).toBeGreaterThan(0)
    })

    const first = capturedTranscriptOptions[0]
    expect(first.isRunning).toBe(true)
  })

  it('passes isHistoricalRuntimeView = true when ?rt= is present', async () => {
    renderPage('/issues/84/session/canonical-session-id?rt=rt-old')

    await waitFor(() => {
      expect(capturedTranscriptOptions.length).toBeGreaterThan(0)
    })

    const last = capturedTranscriptOptions[capturedTranscriptOptions.length - 1]
    expect(last.isHistoricalRuntimeView).toBe(true)
    expect(last.runtimeSessionId).toBe('rt-old')
  })

  it('passes isHistoricalRuntimeView = false when no ?rt= is present', async () => {
    renderPage('/issues/84/session/canonical-session-id')

    await waitFor(() => {
      expect(capturedTranscriptOptions.length).toBeGreaterThan(0)
    })

    const last = capturedTranscriptOptions[capturedTranscriptOptions.length - 1]
    expect(last.isHistoricalRuntimeView).toBe(false)
  })
})

describe('useIssueSessionDataSource — empty state diagnostics', () => {
  beforeEach(() => {
    _issueData = baseIssue()
    _coderSessionsData = [baseSession()]
    _metadataData = baseMetadata()
    _transcriptData = { turns: [], partCount: 0, lastActivityAt: null }
    _unfilteredTranscriptData = null
    resetCapturedOptions()
  })

  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('shows running-no-content state when session is running and has no turns', async () => {
    _metadataData = baseMetadata({ status: 'active', statusKind: 'live' })

    renderPage('/issues/84/session/canonical-session-id')

    const emptyState = await screen.findByTestId('session-empty-state')
    expect(emptyState).toHaveAttribute('data-state-kind', 'running-no-content')
    expect(emptyState).toHaveTextContent(/started but no content has been received/i)
  })

  it('shows terminal-no-content state when session is completed and has no turns', async () => {
    _metadataData = baseMetadata({ status: 'completed', statusKind: 'completed' })

    renderPage('/issues/84/session/canonical-session-id')

    const emptyState = await screen.findByTestId('session-empty-state')
    expect(emptyState).toHaveAttribute('data-state-kind', 'terminal-no-content')
    expect(emptyState).toHaveTextContent(/No content was received for this session/i)
  })

  it('shows runtime-filtered state when explicit runtime view is empty but unfiltered has content', async () => {
    _metadataData = baseMetadata({
      runtimeSessionLineage: [
        { runtimeSessionId: 'rt-current', boundAt: '2026-01-01T00:00:00Z' },
        { runtimeSessionId: 'rt-old', boundAt: '2026-01-02T00:00:00Z' },
      ],
    })
    _transcriptData = { turns: [], partCount: 0, lastActivityAt: null }
    _unfilteredTranscriptData = {
      turns: [
        {
          id: 'turn-1',
          startedAt: '2026-01-01T00:00:00Z',
          completedAt: '2026-01-01T00:01:00Z',
          user: {
            role: 'mohist',
            text: 'do something',
            kind: 'task',
            sentAt: '2026-01-01T00:00:00Z',
            runtimeSessionId: 'rt-old',
          },
          assistant: [
            {
              id: 'part-1',
              type: 'text',
              text: 'done',
              startedAt: '2026-01-01T00:00:30Z',
              completedAt: '2026-01-01T00:01:00Z',
            },
          ],
        },
      ],
      partCount: 1,
      lastActivityAt: '2026-01-01T00:01:00Z',
    }

    renderPage('/issues/84/session/canonical-session-id?rt=rt-current')

    await waitFor(() => {
      const emptyState = screen.getByTestId('session-empty-state')
      expect(emptyState).toHaveAttribute('data-state-kind', 'runtime-filtered')
    })
    const emptyState = screen.getByTestId('session-empty-state')
    expect(emptyState).toHaveTextContent(/current runtime has no content/i)

    const historyLink = screen.getByTestId('session-empty-state-history-link')
    expect(historyLink).toHaveTextContent(/View historical runtime/i)
    expect(historyLink).toHaveAttribute('href', expect.stringContaining('rt=rt-old'))
    expect(historyLink).toHaveAttribute('href', expect.stringContaining('/issues/84/workflow/sessions/session-name-abc'))
  })

  it('falls back to running-no-content when runtime-filtered view is empty but unfiltered has no content', async () => {
    _metadataData = baseMetadata({
      runtimeSessionLineage: [
        { runtimeSessionId: 'rt-current', boundAt: '2026-01-01T00:00:00Z' },
        { runtimeSessionId: 'rt-old', boundAt: '2026-01-02T00:00:00Z' },
      ],
    })
    _transcriptData = { turns: [], partCount: 0, lastActivityAt: null }
    _unfilteredTranscriptData = { turns: [], partCount: 0, lastActivityAt: null }

    renderPage('/issues/84/session/canonical-session-id?rt=rt-current')

    const emptyState = await screen.findByTestId('session-empty-state')
    expect(emptyState).toHaveAttribute('data-state-kind', 'running-no-content')
    expect(emptyState).not.toHaveTextContent(/current runtime has no content/i)
  })

  it('does not render empty state when turns are present', async () => {
    _transcriptData = {
      turns: [
        {
          id: 'turn-1',
          startedAt: '2026-01-01T00:00:00Z',
          completedAt: '2026-01-01T00:01:00Z',
          user: {
            role: 'mohist',
            text: 'hello',
            kind: 'task',
            sentAt: '2026-01-01T00:00:00Z',
            runtimeSessionId: 'rt-current',
          },
          assistant: [],
        },
      ],
      partCount: 1,
      lastActivityAt: '2026-01-01T00:01:00Z',
    }

    renderPage('/issues/84/session/canonical-session-id')

    await waitFor(() => {
      expect(screen.queryByTestId('session-empty-state')).toBeNull()
    })
  })
})
