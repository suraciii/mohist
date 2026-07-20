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
const followupMutateAsync = vi.fn(async () => ({ status: 'sent' }))

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
      status: 'active',
      statusKind: 'live',
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
    metadata = makeMetadata({ status: 'active', statusKind: 'live', completedAt: null })

    renderWithQueryClient(<SessionPage dependencies={sessionPageDependencies} />)

    await waitFor(() => expect(screen.getByTestId('session-followup-input')).toBeInTheDocument())
    await act(async () => {
      fireEvent.change(screen.getByTestId('session-followup-input'), { target: { value: 'Continue with tests' } })
    })
    await act(async () => {
      fireEvent.click(screen.getByTestId('session-followup-send'))
    })

    await waitFor(() => {
      expect(followupMutateAsync).toHaveBeenCalledWith({
        issueNumber: ISSUE,
        sessionName: SESSION,
        text: 'Continue with tests',
      })
    })
  })

  it('hides the composer input when the session is completed', async () => {
    sessionsData = makeSessionsForLookup()
    metadata = makeMetadata({
      status: 'completed',
      statusKind: 'completed',
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
    expect(composer).toHaveAttribute('data-disabled', 'true')
    expect(screen.queryByTestId('session-followup-input')).not.toBeInTheDocument()
    expect(composer).toHaveTextContent(/session ended .*not accepting new followups/i)
  })

  it('hides the composer input when the session is failed', async () => {
    sessionsData = makeSessionsForLookup()
    metadata = makeMetadata({
      status: 'failed',
      statusKind: 'failed',
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
    expect(composer).toHaveAttribute('data-disabled', 'true')
    expect(screen.queryByTestId('session-followup-input')).not.toBeInTheDocument()
  })

  it('shows the closed composer when a completed session has no turns', async () => {
    sessionsData = makeSessionsForLookup()
    metadata = makeMetadata({
      status: 'completed',
      statusKind: 'completed',
      completedAt: '2024-01-01T11:00:00.000Z',
    })
    transcript = { turns: [], partCount: 0, lastActivityAt: null }

    renderWithQueryClient(<SessionPage dependencies={sessionPageDependencies} />)

    const composer = await screen.findByTestId('session-followup-composer')
    expect(composer).toHaveAttribute('data-state', 'closed')
    expect(composer).toHaveTextContent(/session ended .*not accepting new followups/i)
    expect(screen.queryByTestId('session-followup-input')).not.toBeInTheDocument()
  })

  it('shows the closed composer when a failed session has no turns', async () => {
    sessionsData = makeSessionsForLookup()
    metadata = makeMetadata({
      status: 'failed',
      statusKind: 'failed',
      completedAt: '2024-01-01T11:00:00.000Z',
    })
    transcript = { turns: [], partCount: 0, lastActivityAt: null }

    renderWithQueryClient(<SessionPage dependencies={sessionPageDependencies} />)

    const composer = await screen.findByTestId('session-followup-composer')
    expect(composer).toHaveAttribute('data-state', 'closed')
    expect(composer).toHaveTextContent(/session ended .*not accepting new followups/i)
    expect(screen.queryByTestId('session-followup-input')).not.toBeInTheDocument()
  })

  it('shows the composer even while waiting for activity on a running session with no turns', async () => {
    sessionsData = [{
      ...makeSessionsForLookup()[0],
      status: 'running',
      completedAt: null,
    }]
    metadata = makeMetadata({
      status: 'active',
      statusKind: 'live',
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
    metadata = makeMetadata({ status: 'active', statusKind: 'live', completedAt: null })
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
    expect(screen.getByTestId('session-followup-status')).toHaveTextContent(/queued.*waiting for agent/i)

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
