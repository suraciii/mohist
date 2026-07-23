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
    // Issue 484: sessions carry an `activity` (idle/active/unknown) instead
    // of a `status`. The list/session-name fallbacks below default to active
    // so pre-resolution callers still see a live session.
    activity: 'active',
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
    // Issue 484: metadata is keyed by `activity`, not `status`/`statusKind`.
    activity: 'active',
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

  // Issue 484: `isRunning` is now derived from `activity === 'active'`.
  // While metadata is loading, the session list entry's activity (active)
  // is the fallback, so `isRunning` stays true during loading — matching the
  // pre-484 behaviour. The assertion now documents that this is driven by
  // the session-list activity rather than a raw `status` field.
  it('sets isRunning to true during metadata loading (session list activity=active)', async () => {
    _metadataData = null

    renderPage('/issues/84/session/canonical-session-id')

    await waitFor(() => {
      expect(capturedTranscriptOptions.length).toBeGreaterThan(0)
    })

    const first = capturedTranscriptOptions[0]
    expect(first.isRunning).toBe(true)
  })

  it('reports isRunning=true once metadata resolves activity=active', async () => {
    renderPage('/issues/84/session/canonical-session-id')

    await waitFor(() => {
      const last = capturedTranscriptOptions[capturedTranscriptOptions.length - 1]
      expect(last.isRunning).toBe(true)
    })
  })

  // Issue 484: the historical-runtime-view (?rt=) affordance was removed from
  // this data source — it no longer forwards an `isHistoricalRuntimeView`
  // flag (the option is always undefined), nor does it remap the runtime
  // session id from the query string. The transcript now scopes to the
  // metadata-reported runtime session id only. These cases assert the new
  // (non-)behaviour rather than the removed mapping.
  it('does not forward isHistoricalRuntimeView even when ?rt= is present', async () => {
    renderPage('/issues/84/session/canonical-session-id?rt=rt-old')

    await waitFor(() => {
      expect(capturedTranscriptOptions.length).toBeGreaterThan(0)
    })

    const last = capturedTranscriptOptions[capturedTranscriptOptions.length - 1]
    expect(last.isHistoricalRuntimeView).toBeUndefined()
    // The metadata-reported runtime session id wins; ?rt= no longer overrides.
    expect(last.runtimeSessionId).toBe('rt-current')
  })

  it('does not forward isHistoricalRuntimeView when no ?rt= is present', async () => {
    renderPage('/issues/84/session/canonical-session-id')

    await waitFor(() => {
      expect(capturedTranscriptOptions.length).toBeGreaterThan(0)
    })

    const last = capturedTranscriptOptions[capturedTranscriptOptions.length - 1]
    expect(last.isHistoricalRuntimeView).toBeUndefined()
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

  // Issue 484: empty-state kind is now derived from `activity`
  // (active-no-content / idle-no-content / unknown-no-content). A session
  // whose execution finished never enters a terminal status — it returns to
  // `idle`, so the old terminal-no-content branch no longer exists.
  it('shows active-no-content state when session is active and has no turns', async () => {
    _metadataData = baseMetadata({ activity: 'active' })

    renderPage('/issues/84/session/canonical-session-id')

    const emptyState = await screen.findByTestId('session-empty-state')
    expect(emptyState).toHaveAttribute('data-state-kind', 'active-no-content')
    expect(emptyState).toHaveTextContent(/active but no content has been received/i)
  })

  it('shows idle-no-content state when session is idle (finished) and has no turns', async () => {
    _metadataData = baseMetadata({ activity: 'idle' })

    renderPage('/issues/84/session/canonical-session-id')

    const emptyState = await screen.findByTestId('session-empty-state')
    expect(emptyState).toHaveAttribute('data-state-kind', 'idle-no-content')
    expect(emptyState).toHaveTextContent(/Send a follow-up to continue the conversation/i)
  })

  it('shows unknown-no-content state when activity is unresolved and has no turns', async () => {
    _metadataData = baseMetadata({ activity: undefined })

    renderPage('/issues/84/session/canonical-session-id')

    const emptyState = await screen.findByTestId('session-empty-state')
    expect(emptyState).toHaveAttribute('data-state-kind', 'unknown-no-content')
    expect(emptyState).toHaveTextContent(/activity is unknown/i)
  })

  // Issue 484: the `runtime-filtered` empty-state branch (and the
  // `?rt=`-driven historical-runtime-lineage view that produced it) was
  // removed from this data source. The transcript is now scoped solely by
  // the metadata-reported runtime session id, and there is no
  // `runtime-filtered` / history-link affordance to assert. The two former
  // cases ("shows runtime-filtered state when explicit runtime view is empty
  // but unfiltered has content" and "falls back to running-no-content when
  // runtime-filtered view is empty but unfiltered has no content") have no
  // equivalent under the activity model and were deleted intentionally.

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
