import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { TEST_PROJECT, baseRender, screen, waitFor } from './test-utils'
import { SessionPage } from '../src/pages/session/ui/SessionPage'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ProjectProvider } from '../src/entities/project/model/ProjectContext'
import { MemoryRouter } from 'react-router-dom'
import React from 'react'
import type {
  AgentSessionMetadata,
  AgentSessionTranscriptResponse,
  SessionTurn,
} from '../src/entities/coder-session'

const endpointMocks = vi.hoisted(() => ({
  sessions: [] as any[],
  sessionsLoading: false,
  issue: null as any,
  metadata: null as AgentSessionMetadata | null,
  transcript: { turns: [], partCount: 0, lastActivityAt: null } as AgentSessionTranscriptResponse,
  params: { number: '42', sessionName: 'T-003.1' } as Record<string, string>,
}))

vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual<typeof import('react-router-dom')>('react-router-dom')
  return {
    ...actual,
    useParams: () => endpointMocks.params,
  }
})

vi.mock('../src/entities/coder-session/model/useCoderSessions', () => ({
  useCoderSessions: () => ({ sessions: endpointMocks.sessions, isLoading: endpointMocks.sessionsLoading }),
}))

vi.mock('../src/entities/issue/api/queries', () => ({
  useIssue: () => ({ data: endpointMocks.issue }),
}))

vi.mock('../src/entities/coder-session/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/entities/coder-session/api/client')>()),
  getAgentSessionMetadata: vi.fn(() => Promise.resolve(endpointMocks.metadata)),
  getAgentSessionTranscript: vi.fn(() => Promise.resolve(endpointMocks.transcript)),
}))

vi.mock('../src/entities/coder-session/model/useFollowupMutation', () => ({
  useFollowupMutation: () => ({
    mutate: vi.fn(),
    isPending: false,
  }),
}))

const originalScrollTo = Element.prototype.scrollTo
const queryClients: QueryClient[] = []

beforeEach(() => {
  vi.clearAllMocks()
  endpointMocks.sessions = []
  endpointMocks.sessionsLoading = false
  endpointMocks.issue = null
  endpointMocks.metadata = null
  endpointMocks.transcript = { turns: [], partCount: 0, lastActivityAt: null }
  endpointMocks.params = { number: '42', sessionName: 'T-003.1' }
  Element.prototype.scrollTo = vi.fn()
})

afterEach(() => {
  vi.useRealTimers()
  Element.prototype.scrollTo = originalScrollTo
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
        <MemoryRouter>{ui}</MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

function makeMetadata(overrides: Partial<AgentSessionMetadata> = {}): AgentSessionMetadata {
  return {
    id: 'proj/wr/T-003.1',
    sessionName: 'T-003.1',
    acpSessionId: 'acp-123',
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
    acpSessionId: 'acp-123',
    executionId: 'exec-T-003.1',
    taskDescription: 'Composer integration test',
    status: 'completed',
    createdAt: '2024-01-01T10:00:00.000Z',
    completedAt: '2024-01-01T11:00:00.000Z',
    model: 'claude-3-5-sonnet',
    coderType: null,
    stage: 'build',
    title: 'Composer integration test',
    lastDataAt: null,
    probeSentAt: null,
    probeDeadlineAt: null,
    failureReason: null,
  }]
}

describe('T-003: SessionPage followup composer integration', () => {
  it('renders an interactive composer below the transcript when the session is active', async () => {
    endpointMocks.sessions = makeSessionsForLookup()
    endpointMocks.metadata = makeMetadata({
      status: 'active',
      statusKind: 'live',
      completedAt: null,
    })
    endpointMocks.transcript = {
      turns: [makeTurn()],
      partCount: 1,
      lastActivityAt: '2024-01-01T10:00:01.000Z',
    }

    renderWithQueryClient(<SessionPage />)

    await waitFor(() => {
      expect(screen.getByTestId('session-followup-composer')).toBeInTheDocument()
    })
    expect(screen.getByTestId('session-followup-input')).toBeInTheDocument()
    expect(screen.getByTestId('session-followup-send')).toBeInTheDocument()
    expect(screen.queryByTestId('session-followup-composer')).not.toHaveAttribute('data-disabled', 'true')
  })

  it('hides the composer input when the session is completed', async () => {
    endpointMocks.sessions = makeSessionsForLookup()
    endpointMocks.metadata = makeMetadata({
      status: 'completed',
      statusKind: 'completed',
      completedAt: '2024-01-01T11:00:00.000Z',
    })
    endpointMocks.transcript = {
      turns: [makeTurn()],
      partCount: 1,
      lastActivityAt: '2024-01-01T11:00:00.000Z',
    }

    renderWithQueryClient(<SessionPage />)

    await waitFor(() => {
      expect(screen.getByTestId('session-followup-composer')).toBeInTheDocument()
    })
    const composer = screen.getByTestId('session-followup-composer')
    expect(composer).toHaveAttribute('data-disabled', 'true')
    expect(screen.queryByTestId('session-followup-input')).not.toBeInTheDocument()
    expect(composer).toHaveTextContent(/no longer accepting followups/i)
  })

  it('hides the composer input when the session is failed', async () => {
    endpointMocks.sessions = makeSessionsForLookup()
    endpointMocks.metadata = makeMetadata({
      status: 'failed',
      statusKind: 'failed',
      failureReason: 'agent crashed',
    })
    endpointMocks.transcript = {
      turns: [makeTurn()],
      partCount: 1,
      lastActivityAt: '2024-01-01T11:00:00.000Z',
    }

    renderWithQueryClient(<SessionPage />)

    await waitFor(() => {
      expect(screen.getByTestId('session-followup-composer')).toBeInTheDocument()
    })
    const composer = screen.getByTestId('session-followup-composer')
    expect(composer).toHaveAttribute('data-disabled', 'true')
    expect(screen.queryByTestId('session-followup-input')).not.toBeInTheDocument()
  })

  it('shows the composer even while waiting for activity on a running session with no turns', async () => {
    endpointMocks.sessions = [{
      ...makeSessionsForLookup()[0],
      status: 'running',
      completedAt: null,
    }]
    endpointMocks.metadata = makeMetadata({
      status: 'active',
      statusKind: 'live',
      completedAt: null,
    })
    endpointMocks.transcript = { turns: [], partCount: 0, lastActivityAt: null }

    renderWithQueryClient(<SessionPage />)

    await waitFor(() => {
      expect(screen.getByTestId('session-followup-composer')).toBeInTheDocument()
    })
    expect(screen.getByTestId('session-followup-input')).toBeInTheDocument()
    expect(screen.getByTestId('session-followup-send')).toBeInTheDocument()
  })
})
