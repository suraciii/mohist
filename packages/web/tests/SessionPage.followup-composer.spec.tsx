import { describe, it, expect, afterEach, beforeEach, vi } from 'vitest'
import { act, TEST_PROJECT, baseRender, fireEvent, screen, waitFor } from './test-utils'
import { SessionPage, type SessionPageDependencies } from '../src/pages/session/ui/SessionPage'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ProjectProvider } from '../src/entities/project/model/ProjectContext'
import { MemoryRouter, Routes, Route } from 'react-router-dom'
import React from 'react'
import type {
  AgentSessionMetadata,
  AgentSessionTranscriptResponse,
  SessionTurn,
} from '../src/entities/coder-session'
import { setScopedValue } from './support/scoped-property'

const ISSUE = 42
const SESSION = 'T-003.1'

let issueData: unknown = null
let sessionsData: unknown[] = []
let sessionsLoading = false
let metadata: AgentSessionMetadata | null = null
let transcript: AgentSessionTranscriptResponse = { turns: [], partCount: 0, lastActivityAt: null }
let transcriptVersion = 0
type FollowupRequest = { issueNumber: number; sessionName: string; text: string; idempotencyKey: string }
type FollowupResponse = { status: string; inputId?: string; turnId?: string }
const followupMutateAsync = vi.fn<(input: FollowupRequest) => Promise<FollowupResponse>>(async () => ({
  status: 'accepted',
  inputId: 'input-1',
  turnId: 'turn-1',
}))

const sessionPageDependencies: SessionPageDependencies = {
  dataSource: {
    useSessionTranscript: () => ({
      turns: transcript.turns,
      transcriptVersion,
      scrollToBottom: vi.fn(),
      newContentAvailable: false,
      setIsNearBottom: vi.fn(),
      isFinalizing: false,
      isThinking: false,
      isStreaming: false,
    }) as never,
    useIssue: () => ({ data: issueData }) as never,
    useCoderSessions: () => ({ sessions: sessionsData, isLoading: sessionsLoading }) as never,
    useSiblingSessions: () => ({
      sessions: [],
      currentIndex: -1,
      previous: null,
      next: null,
      hasPrevious: false,
      hasNext: false,
    }),
    getAgentSessionMetadata: async () => metadata as never,
    getAgentSessionTranscript: async () => transcript,
    useFollowupMutation: () => ({ mutateAsync: followupMutateAsync, isPending: false }) as never,
    useCancelSessionMutation: () => ({ mutate: vi.fn(), isPending: false }) as never,
  },
}

const queryClients: QueryClient[] = []

beforeEach(() => {
  issueData = null
  sessionsData = []
  sessionsLoading = false
  metadata = null
  transcript = { turns: [], partCount: 0, lastActivityAt: null }
  transcriptVersion = 0
  followupMutateAsync.mockClear()
  setScopedValue(Element.prototype, 'scrollTo', vi.fn())
})

afterEach(() => {
  vi.useRealTimers()
  for (const queryClient of queryClients) queryClient.clear()
  queryClients.length = 0
})

function createMockQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  })
}

function renderWithQueryClient(ui: React.ReactElement) {
  const queryClient = createMockQueryClient()
  queryClients.push(queryClient)
  return baseRender(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
        <MemoryRouter initialEntries={[`/issues/${ISSUE}/workflow/sessions/${SESSION}`]}>
          <Routes>
            <Route path="/issues/:number/workflow/sessions/:sessionName" element={ui} />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

function makeMetadata(overrides: Partial<AgentSessionMetadata> = {}): AgentSessionMetadata {
  return {
    id: 'proj/wr/T-003.1',
    sessionName: 'T-003.1',
    runtimeSessionId: 'runtime-123',
    runtime: 'opencode',
    // issue-484: sessions key off `activity` (idle/active/unknown), not the
    // legacy terminal status. Default to 'idle' (post-execution) so tests
    // model the new world unless they override activity explicitly.
    activity: 'idle',
    status: 'completed',
    statusKind: 'completed',
    model: 'claude-3-5-sonnet',
    stage: 'build',
    title: 'Composer integration test',
    createdAt: '2024-01-01T10:00:00.000Z',
    completedAt: '2024-01-01T11:00:00.000Z',
    lastActivityAt: '2024-01-01T10:30:00.000Z',
    metadata: {
      eventCount: 5,
      toolCount: 2,
      partCount: 3,
    },
    ...overrides,
  }
}

function makeTurn(): SessionTurn {
  return {
    id: 'turn-1',
    startedAt: '2024-01-01T10:00:00.000Z',
    completedAt: null,
    user: {
      role: 'mohist',
      text: 'Original prompt',
      kind: 'task',
      sentAt: '2024-01-01T10:00:00.000Z',
    },
    assistant: [
      {
        id: 'text-1',
        type: 'text',
        text: 'Acknowledged.',
        startedAt: '2024-01-01T10:00:01.000Z',
        completedAt: null,
      },
    ],
  }
}

function makeSessionsForLookup() {
  return [{
    id: 'proj/wr/T-003.1',
    sessionName: 'T-003.1',
    runtimeSessionId: 'runtime-123',
    executionId: 'exec-T-003.1',
    taskDescription: 'Composer integration test',
    status: 'completed',
    createdAt: '2024-01-01T10:00:00.000Z',
    completedAt: '2024-01-01T11:00:00.000Z',
    model: 'claude-3-5-sonnet',
    runtime: 'opencode',
    stage: 'build',
    title: 'Composer integration test',
    lastDataAt: null,
    probeSentAt: null,
    probeDeadlineAt: null,
    failureReason: null,
  }]
}

describe('SessionPage followup composer integration', () => {
  it('renders an interactive composer below the transcript when the session is active', async () => {
    sessionsData = makeSessionsForLookup()
    metadata = makeMetadata({
      activity: 'active',
      completedAt: null,
    })
    transcript = {
      turns: [makeTurn()],
      partCount: 1,
      lastActivityAt: '2024-01-01T10:00:01.000Z',
    }

    renderWithQueryClient(<SessionPage dependencies={sessionPageDependencies} />)

    await waitFor(() => {
      expect(screen.getByTestId('session-followup-composer')).toBeInTheDocument()
    })
    expect(screen.getByTestId('session-followup-input')).toBeInTheDocument()
    expect(screen.getByTestId('session-followup-send')).toBeInTheDocument()
    expect(screen.queryByTestId('session-followup-composer')).not.toHaveAttribute('data-disabled', 'true')
  })

  it('submits workflow follow-ups through the canonical session name', async () => {
    sessionsData = makeSessionsForLookup()
    metadata = makeMetadata({ activity: 'active', completedAt: null })

    renderWithQueryClient(<SessionPage dependencies={sessionPageDependencies} />)

    await waitFor(() => expect(screen.getByTestId('session-followup-input')).toBeInTheDocument())
    await act(async () => {
      fireEvent.change(screen.getByTestId('session-followup-input'), { target: { value: 'Continue with tests' } })
    })
    await act(async () => {
      fireEvent.click(screen.getByTestId('session-followup-send'))
    })

    await waitFor(() => {
      expect(followupMutateAsync).toHaveBeenCalledWith(expect.objectContaining({
        issueNumber: ISSUE,
        sessionName: SESSION,
        text: 'Continue with tests',
        idempotencyKey: expect.any(String),
      }))
    })
  })

  it('reuses the same idempotency key when an accepted outcome is unknown', async () => {
    sessionsData = makeSessionsForLookup()
    metadata = makeMetadata({ activity: 'active', completedAt: null })
    followupMutateAsync
      .mockResolvedValueOnce({ status: 'unknown' })
      .mockResolvedValueOnce({ status: 'accepted', inputId: 'input-1', turnId: 'turn-1' })

    renderWithQueryClient(<SessionPage dependencies={sessionPageDependencies} />)

    await waitFor(() => expect(screen.getByTestId('session-followup-input')).toBeInTheDocument())
    const input = screen.getByTestId('session-followup-input') as HTMLTextAreaElement
    fireEvent.change(input, { target: { value: 'Retry this request' } })
    await act(async () => {
      fireEvent.click(screen.getByTestId('session-followup-send'))
    })

    await waitFor(() => expect(screen.getByTestId('session-followup-error')).toBeInTheDocument())
    expect(input.value).toBe('Retry this request')
    const firstKey = followupMutateAsync.mock.calls[0]![0].idempotencyKey

    await act(async () => {
      fireEvent.click(screen.getByTestId('session-followup-send'))
    })

    await waitFor(() => expect(followupMutateAsync).toHaveBeenCalledTimes(2))
    expect(followupMutateAsync.mock.calls[1]![0].idempotencyKey).toBe(firstKey)
  })

  it('keeps the composer interactive when the session has finished executing (legacy completed)', async () => {
    // issue-484: sessions never enter a terminal status. After execution the
    // activity returns to 'idle', and an idle session still accepts follow-ups.
    // The composer is therefore interactive (no 'closed'/'session ended' UI).
    sessionsData = makeSessionsForLookup()
    metadata = makeMetadata({
      activity: 'idle',
      completedAt: '2024-01-01T11:00:00.000Z',
    })
    transcript = {
      turns: [makeTurn()],
      partCount: 1,
      lastActivityAt: '2024-01-01T11:00:00.000Z',
    }

    renderWithQueryClient(<SessionPage dependencies={sessionPageDependencies} />)

    await waitFor(() => {
      expect(screen.getByTestId('session-followup-composer')).toBeInTheDocument()
    })
    const composer = screen.getByTestId('session-followup-composer')
    expect(composer).not.toHaveAttribute('data-disabled', 'true')
    expect(screen.getByTestId('session-followup-input')).toBeInTheDocument()
    expect(composer).not.toHaveTextContent(/session ended/i)
  })

  it('keeps the composer interactive when the session previously failed (now idle)', async () => {
    // issue-484: a session that previously failed is not terminal; execution
    // ending returns activity to idle, so follow-up remains available. The
    // failure surface is expressed via failureReason/errors evidence, not by
    // disabling the composer.
    sessionsData = makeSessionsForLookup()
    metadata = makeMetadata({
      activity: 'idle',
      failureReason: 'agent crashed',
    })
    transcript = {
      turns: [makeTurn()],
      partCount: 1,
      lastActivityAt: '2024-01-01T11:00:00.000Z',
    }

    renderWithQueryClient(<SessionPage dependencies={sessionPageDependencies} />)

    await waitFor(() => {
      expect(screen.getByTestId('session-followup-composer')).toBeInTheDocument()
    })
    const composer = screen.getByTestId('session-followup-composer')
    expect(composer).not.toHaveAttribute('data-disabled', 'true')
    expect(screen.getByTestId('session-followup-input')).toBeInTheDocument()
  })

  it('renders the composer for an idle session with no turns', async () => {
    // issue-484: idle session with no turns still shows an interactive
    // composer (idle activity is followup-eligible). There is no 'closed'
    // composer state anymore.
    sessionsData = makeSessionsForLookup()
    metadata = makeMetadata({
      activity: 'idle',
      completedAt: '2024-01-01T11:00:00.000Z',
    })
    transcript = { turns: [], partCount: 0, lastActivityAt: null }

    renderWithQueryClient(<SessionPage dependencies={sessionPageDependencies} />)

    const composer = await screen.findByTestId('session-followup-composer')
    expect(composer).not.toHaveAttribute('data-disabled', 'true')
    expect(screen.getByTestId('session-followup-input')).toBeInTheDocument()
  })

  it('shows the unavailable composer when activity is unknown and there are no turns', async () => {
    // issue-484: the only state that disables the composer is activity==='unknown'
    // (Mohist cannot confirm whether execution is still active). This replaces the
    // old 'completed/failed session ended' closed-composer behaviour.
    sessionsData = makeSessionsForLookup()
    metadata = makeMetadata({
      activity: 'unknown',
      completedAt: '2024-01-01T11:00:00.000Z',
    })
    transcript = { turns: [], partCount: 0, lastActivityAt: null }

    renderWithQueryClient(<SessionPage dependencies={sessionPageDependencies} />)

    const composer = await screen.findByTestId('session-followup-composer')
    expect(composer).toHaveAttribute('data-disabled', 'true')
    expect(composer).toHaveAttribute('data-state', 'unavailable')
    expect(composer).toHaveTextContent(/follow-up is unavailable/i)
    expect(screen.queryByTestId('session-followup-input')).not.toBeInTheDocument()
  })

  it('shows the composer even while waiting for activity on an active session with no turns', async () => {
    sessionsData = [{
      ...makeSessionsForLookup()[0],
      status: 'running',
      completedAt: null,
    }]
    metadata = makeMetadata({
      activity: 'active',
      completedAt: null,
    })
    transcript = { turns: [], partCount: 0, lastActivityAt: null }

    renderWithQueryClient(<SessionPage dependencies={sessionPageDependencies} />)

    await waitFor(() => {
      expect(screen.getByTestId('session-followup-composer')).toBeInTheDocument()
    })
    expect(screen.getByTestId('session-followup-input')).toBeInTheDocument()
    expect(screen.getByTestId('session-followup-send')).toBeInTheDocument()
  })

  it('keeps a submitted followup queued until new transcript content arrives', async () => {
    sessionsData = makeSessionsForLookup()
    metadata = makeMetadata({ activity: 'active', completedAt: null })
    transcript = { turns: [makeTurn()], partCount: 1, lastActivityAt: '2024-01-01T10:00:01.000Z' }

    const page = renderWithQueryClient(<SessionPage dependencies={sessionPageDependencies} />)

    await waitFor(() => expect(screen.getByTestId('session-followup-input')).toBeInTheDocument())
    fireEvent.change(screen.getByTestId('session-followup-input'), { target: { value: 'Continue with tests' } })
    await act(async () => {
      fireEvent.click(screen.getByTestId('session-followup-send'))
    })

    await waitFor(() => {
      expect(screen.getByTestId('session-followup-composer')).toHaveAttribute('data-state', 'queued')
    })
    expect(screen.getByTestId('session-followup-status')).toHaveTextContent(/accepted.*pending/i)

    transcriptVersion = 1
    page.rerender(
      <QueryClientProvider client={queryClients[queryClients.length - 1]}>
        <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
          <MemoryRouter initialEntries={[`/issues/${ISSUE}/workflow/sessions/${SESSION}`]}>
            <Routes>
              <Route path="/issues/:number/workflow/sessions/:sessionName" element={<SessionPage dependencies={sessionPageDependencies} />} />
            </Routes>
          </MemoryRouter>
        </ProjectProvider>
      </QueryClientProvider>,
    )

    await waitFor(() => {
      expect(screen.getByTestId('session-followup-composer')).toHaveAttribute('data-state', 'interactive')
    })
  })
})
