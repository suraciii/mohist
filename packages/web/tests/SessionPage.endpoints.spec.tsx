import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { TEST_PROJECT, baseRender, screen, waitFor, fireEvent } from './test-utils'
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

let _sessionsData: any[] = []
let _sessionsLoading = false
let _issueData: any = null
let _metadataData: AgentSessionMetadata | null = null
let _transcriptData: AgentSessionTranscriptResponse = { turns: [], partCount: 0, lastActivityAt: null }
let _params: Record<string, string> = { number: '51', sessionName: 'T-003.1' }

const _sessionsHandler = vi.fn()
const _metadataHandler = vi.fn()
const _transcriptHandler = vi.fn()

const sessionPageDependencies: SessionPageDependencies = {
  dataSource: {
    useIssue: () => ({ data: _issueData }) as never,
    useCoderSessions: (issueNumber) => {
      _sessionsHandler(issueNumber, TEST_PROJECT.id)
      return {
        sessions: _sessionsData,
        isLoading: _sessionsLoading,
        isFetching: false,
        refetch: async () => ({}) as never,
      }
    },
    useSiblingSessions: () => ({
      sessions: [],
      currentIndex: -1,
      previous: null,
      next: null,
      hasPrevious: false,
      hasNext: false,
    }),
    getAgentSessionMetadata: async (issueNumber, sessionName, projectId) => {
      _metadataHandler(issueNumber, sessionName, projectId)
      return _metadataData!
    },
    getAgentSessionTranscript: async (issueNumber, sessionName, projectId) => {
      _transcriptHandler(issueNumber, sessionName, projectId)
      return _transcriptData
    },
  },
}

const queryClients: QueryClient[] = []

beforeEach(() => {
  vi.clearAllMocks()
  _sessionsData = []
  _sessionsLoading = false
  _issueData = null
  _metadataData = null
  _transcriptData = { turns: [], partCount: 0, lastActivityAt: null }
  _params = { number: '51', sessionName: 'T-003.1' }
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
  const { number, sessionName } = _params
  const initialEntry = `/issues/${number}/workflow/sessions/${sessionName}`
  return baseRender(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
        <MemoryRouter initialEntries={[initialEntry]}>
          <Routes>
            <Route path="/issues/:number/workflow/sessions/:sessionName" element={ui} />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

function renderSessionPage() {
  return renderWithQueryClient(<SessionPage dependencies={sessionPageDependencies} />)
}

function makeMetadata(overrides: Partial<AgentSessionMetadata> = {}): AgentSessionMetadata {
  return {
    id: 'proj/wr/T-003.1',
    sessionName: 'T-003.1',
    runtimeSessionId: 'runtime-123',
    status: 'completed',
    statusKind: 'completed',
    model: 'claude-3-5-sonnet',
    stage: 'build',
    title: 'Implement endpoint split',
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

function makeTurn(overrides: Partial<SessionTurn> = {}): SessionTurn {
  return {
    id: 'turn-1',
    startedAt: '2024-01-01T10:00:00.000Z',
    completedAt: null,
    user: {
      role: 'mohist',
      text: 'T-009 split prompt',
      kind: 'task',
      sentAt: '2024-01-01T10:00:00.000Z',
      summary: {
        kind: 'task',
        title: 'Task prompt',
        rawText: 'T-009 split prompt',
      },
    },
    assistant: [
      {
        id: 'reasoning-1',
        type: 'reasoning',
        text: 'Considering endpoint shapes',
        startedAt: '2024-01-01T10:00:01.000Z',
        completedAt: null,
      },
      {
        id: 'text-1',
        type: 'text',
        text: 'Metadata endpoint first, transcript on demand.',
        startedAt: '2024-01-01T10:00:02.000Z',
        completedAt: null,
      },
      {
        id: 'tool-1',
        type: 'tool',
        tool: {
          toolCallId: 'tc-t009',
          normalizedName: 'read',
          toolName: 'read',
          status: 'completed',
          title: 'Read src/session.ts',
          input: '{"file_path":"src/session.ts"}',
          output: 'export function split() { ... }',
          startedAt: '2024-01-01T10:00:03.000Z',
          completedAt: '2024-01-01T10:00:04.000Z',
        },
      },
    ],
    ...overrides,
  }
}

function makeTranscript(turns: SessionTurn[] = []): AgentSessionTranscriptResponse {
  return {
    turns,
    partCount: turns.reduce((count, turn) => count + turn.assistant.length, 0),
    lastActivityAt: turns[0]?.completedAt ?? turns[0]?.startedAt ?? null,
  }
}

function setupEndpointMocks({
  sessions = [] as any[],
  metadata = makeMetadata(),
  transcript = makeTranscript(),
  issue = null as any,
  sessionsLoading = false,
}: {
  sessions?: any[]
  metadata?: AgentSessionMetadata | null
  transcript?: AgentSessionTranscriptResponse
  issue?: any
  sessionsLoading?: boolean
} = {}) {
  _sessionsData = sessions
  _metadataData = metadata
  _transcriptData = transcript
  _issueData = issue
  _sessionsLoading = sessionsLoading
}

function makeSessionsForLookup() {
  return [{
    id: 'proj/wr/T-003.1',
    sessionName: 'T-003.1',
    runtimeSessionId: 'runtime-123',
    executionId: 'exec-T-003.1',
    taskDescription: 'Implement endpoint split',
    status: 'completed',
    createdAt: '2024-01-01T10:00:00.000Z',
    completedAt: '2024-01-01T11:00:00.000Z',
    model: 'claude-3-5-sonnet',
    runtime: null,
    stage: 'build',
    title: 'Implement endpoint split',
    lastDataAt: null,
    probeSentAt: null,
    probeDeadlineAt: null,
    failureReason: null,
  }]
}

describe('SessionPage split endpoints', () => {
  describe('metadata and transcript endpoint usage', () => {
    it('loads metadata and transcript through project-scoped session key endpoints', async () => {
      _params = { number: '51', sessionName: 'T-003.1' }
      setupEndpointMocks({
        sessions: makeSessionsForLookup(),
        metadata: makeMetadata({ id: 'proj/wr/T-003.1', sessionName: 'T-003.1' }),
        transcript: makeTranscript([makeTurn()]),
        issue: { number: 51, title: 'Split session endpoints' },
      })

      renderSessionPage()

      await waitFor(() => {
        expect(_metadataHandler).toHaveBeenCalledWith(51, 'T-003.1', TEST_PROJECT.id)
      })
      await waitFor(() => {
        expect(_transcriptHandler).toHaveBeenCalledWith(51, 'T-003.1', TEST_PROJECT.id)
      })
    })

    it('does not require turns or workflowLogs on the metadata response', async () => {
      const metadata = makeMetadata({ id: 'proj/wr/T-003.1', sessionName: 'T-003.1' })
      expect((metadata as unknown as Record<string, unknown>).turns).toBeUndefined()
      expect((metadata as unknown as Record<string, unknown>).workflowLogs).toBeUndefined()

      _params = { number: '51', sessionName: 'T-003.1' }
      setupEndpointMocks({
        sessions: makeSessionsForLookup(),
        metadata,
        issue: { number: 51, title: 'Split session endpoints' },
      })

      renderSessionPage()

      await waitFor(() => {
        expect(screen.getByRole('heading', { name: 'T-003.1' })).toBeInTheDocument()
      })

      expect(screen.getByText('Issue #51')).toBeInTheDocument()
      const completedBadges = screen.getAllByText('Completed')
      expect(completedBadges.length).toBeGreaterThanOrEqual(1)
      expect(screen.getByText('Build')).toBeInTheDocument()
      expect(screen.getByText('claude-3-5-sonnet')).toBeInTheDocument()
      expect(_metadataHandler).toHaveBeenCalledWith(51, 'T-003.1', TEST_PROJECT.id)
      expect(_transcriptHandler).toHaveBeenCalledWith(51, 'T-003.1', TEST_PROJECT.id)
    })
  })

  describe('header metadata without turns or workflowLogs', () => {
    it('uses sessionName as the session heading instead of the task title', async () => {
      _params = { number: '51', sessionName: 'T-003.1' }
      setupEndpointMocks({
        sessions: makeSessionsForLookup(),
        metadata: makeMetadata({ id: 'proj/wr/T-003.1', sessionName: 'T-003.1' }),
        issue: { number: 51, title: 'Split session endpoints' },
      })

      renderSessionPage()

      await waitFor(() => {
        expect(screen.getByRole('heading', { name: 'T-003.1' })).toBeInTheDocument()
      })
      expect(screen.getByText('Issue #51')).toBeInTheDocument()
      expect(screen.getByText(/session-?1h 00m|1h 00m/)).toBeInTheDocument()
      expect(screen.queryByRole('heading', { name: 'Implement endpoint split' })).not.toBeInTheDocument()
    })

    it('renders plan session metadata without requiring pre-projected turn content', async () => {
      _params = { number: '123', sessionName: 'plan' }
      const metadata = makeMetadata({
        id: 'proj/wr/plan',
        sessionName: 'plan',
        title: 'Plan-only metadata',
        stage: 'plan',
      })
      expect((metadata as unknown as Record<string, unknown>).turns).toBeUndefined()
      expect((metadata as unknown as Record<string, unknown>).workflowLogs).toBeUndefined()

      setupEndpointMocks({
        sessions: [{ ...makeSessionsForLookup()[0], id: 'proj/wr/plan', sessionName: 'plan', executionId: 'exec-plan', stage: 'plan' }],
        metadata,
      })

      renderSessionPage()

      await waitFor(() => {
        expect(screen.getByRole('heading', { name: 'plan' })).toBeInTheDocument()
      })
      expect(screen.getByText('Plan')).toBeInTheDocument()
    })
  })

  describe('transcript rendering', () => {
    it('renders persisted transcript turns returned by the transcript endpoint', async () => {
      _params = { number: '51', sessionName: 'T-003.1' }
      setupEndpointMocks({
        sessions: makeSessionsForLookup(),
        metadata: makeMetadata({ id: 'proj/wr/T-003.1', sessionName: 'T-003.1' }),
        transcript: makeTranscript([makeTurn()]),
      })

      const { container } = renderSessionPage()

      await waitFor(() => {
        expect(screen.getByText('Show full prompt')).toBeInTheDocument()
      })
      fireEvent.click(screen.getByText('Show full prompt'))

      await waitFor(() => {
        expect(screen.getByText('T-009 split prompt')).toBeInTheDocument()
      })
      expect(screen.getByText('Metadata endpoint first, transcript on demand.')).toBeInTheDocument()
      expect(screen.getByText('Considering endpoint shapes')).toBeInTheDocument()
      expect(container.textContent ?? '').toContain('T-009 split prompt')
      expect(container.textContent ?? '').toContain('Metadata endpoint first, transcript on demand.')
    })

    it('does not project transcript content from raw runtime event payloads in the browser', async () => {
      _params = { number: '51', sessionName: 'T-003.1' }
      setupEndpointMocks({
        sessions: makeSessionsForLookup(),
        metadata: makeMetadata({ id: 'proj/wr/T-003.1', sessionName: 'T-003.1' }),
        transcript: makeTranscript([
          makeTurn({
            user: {
              role: 'mohist',
              text: 'Persisted prompt text',
              kind: 'task',
              sentAt: '2024-01-01T10:00:00.000Z',
              summary: { kind: 'task', title: 'Persisted prompt', rawText: 'Persisted prompt text' },
            },
            assistant: [{
              id: 'text-1',
              type: 'text',
              text: 'Assistant reply from persisted transcript',
              startedAt: '2024-01-01T10:00:01.000Z',
              completedAt: null,
            }],
          }),
        ]),
      })

      const { container } = renderSessionPage()

      await waitFor(() => {
        expect(screen.getByText('Assistant reply from persisted transcript')).toBeInTheDocument()
      })
      expect(container.textContent ?? '').not.toContain('Server-side projection')
      expect(container.textContent ?? '').not.toContain('raw event')
    })
  })
})
