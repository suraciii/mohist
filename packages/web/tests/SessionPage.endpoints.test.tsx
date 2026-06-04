import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { TEST_PROJECT, baseRender, screen, waitFor, fireEvent } from './test-utils'
import { SessionPage } from '../src/pages/session/ui/SessionPage'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ProjectProvider } from '../src/entities/project/model/ProjectContext'
import { MemoryRouter } from 'react-router-dom'
import React from 'react'
import type { AgentSessionEvent, AgentSessionMetadata } from '../src/entities/coder-session'
import { viewSessionEvents, type SessionEvent } from '../src/entities/session/model/view'

const endpointMocks = vi.hoisted(() => ({
  sessions: [] as any[],
  sessionsLoading: false,
  issue: null as any,
  metadata: null as AgentSessionMetadata | null,
  events: [] as AgentSessionEvent[],
  params: { number: '51', sessionName: 'T-003.1' } as Record<string, string>,
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
  getAgentSessionEvents: vi.fn(() => Promise.resolve({ events: endpointMocks.events })),
}))

const originalScrollTo = Element.prototype.scrollTo
const queryClients: QueryClient[] = []

beforeEach(() => {
  vi.clearAllMocks()
  endpointMocks.sessions = []
  endpointMocks.sessionsLoading = false
  endpointMocks.issue = null
  endpointMocks.metadata = null
  endpointMocks.events = []
  endpointMocks.params = { number: '51', sessionName: 'T-003.1' }
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
    title: 'Implement endpoint split',
    createdAt: '2024-01-01T10:00:00.000Z',
    completedAt: '2024-01-01T11:00:00.000Z',
    lastActivityAt: '2024-01-01T10:30:00.000Z',
    metadata: {
      eventCount: 5,
      toolCount: 2,
    },
    ...overrides,
  }
}

function makeRawEvent(overrides: Partial<AgentSessionEvent> & { type: string; payload: unknown }): AgentSessionEvent {
  const sequence = overrides.sequence ?? 0
  return {
    id: overrides.id ?? sequence,
    sequence,
    type: overrides.type,
    payload: overrides.payload,
    createdAt: overrides.createdAt ?? '2024-01-01T10:00:00.000Z',
  }
}

function setupEndpointMocks({
  sessions = [] as any[],
  metadata = makeMetadata(),
  events = [] as AgentSessionEvent[],
  issue = null as any,
  sessionsLoading = false,
}: {
  sessions?: any[]
  metadata?: AgentSessionMetadata | null
  events?: AgentSessionEvent[]
  issue?: any
  sessionsLoading?: boolean
} = {}) {
  endpointMocks.sessions = sessions
  endpointMocks.metadata = metadata
  endpointMocks.events = events
  endpointMocks.issue = issue
  endpointMocks.sessionsLoading = sessionsLoading
}

function makeSessionsForLookup() {
  return [{
    id: 'proj/wr/T-003.1',
    sessionName: 'T-003.1',
    acpSessionId: 'acp-123',
    executionId: 'exec-T-003.1',
    taskDescription: 'Implement endpoint split',
    status: 'completed',
    createdAt: '2024-01-01T10:00:00.000Z',
    completedAt: '2024-01-01T11:00:00.000Z',
    model: 'claude-3-5-sonnet',
    coderType: null,
    stage: 'build',
    title: 'Implement endpoint split',
    lastDataAt: null,
    probeSentAt: null,
    probeDeadlineAt: null,
    failureReason: null,
  }]
}

describe('T-009: SessionPage split endpoints', () => {
  describe('metadata endpoint usage', () => {
    it('initial request uses GET /api/issues/:number/sessions/:name for header metadata', async () => {
      const api = await import('../src/entities/coder-session/api/client')
      const metadataMock = api.getAgentSessionMetadata as unknown as ReturnType<typeof vi.fn>
      const eventsMock = api.getAgentSessionEvents as unknown as ReturnType<typeof vi.fn>

      endpointMocks.params = { number: '51', sessionName: 'T-003.1' }
      setupEndpointMocks({
        sessions: makeSessionsForLookup(),
        metadata: makeMetadata({ id: 'proj/wr/T-003.1', sessionName: 'T-003.1' }),
        issue: { number: 51, title: 'Split session endpoints' },
      })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(metadataMock).toHaveBeenCalledWith(51, 'T-003.1', TEST_PROJECT.id)
      })

      expect(metadataMock.mock.calls[0][0]).toBe(51)
      expect(metadataMock.mock.calls[0][1]).toBe('T-003.1')
      expect(metadataMock.mock.calls[0][2]).toBe(TEST_PROJECT.id)
      expect(eventsMock).not.toHaveBeenCalled()
    })

    it('does not require turns or workflowLogs on the metadata response', async () => {
      const api = await import('../src/entities/coder-session/api/client')
      const metadataMock = api.getAgentSessionMetadata as unknown as ReturnType<typeof vi.fn>
      const eventsMock = api.getAgentSessionEvents as unknown as ReturnType<typeof vi.fn>

      endpointMocks.params = { number: '51', sessionName: 'T-003.1' }
      setupEndpointMocks({
        sessions: makeSessionsForLookup(),
        metadata: makeMetadata({ id: 'proj/wr/T-003.1', sessionName: 'T-003.1' }),
        issue: { number: 51, title: 'Split session endpoints' },
      })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('Issue #51')).toBeInTheDocument()
        expect(screen.getByText('Implement endpoint split')).toBeInTheDocument()
      })

      const [numberArg, nameArg, projectArg] = metadataMock.mock.calls[0]
      expect(numberArg).toBe(51)
      expect(nameArg).toBe('T-003.1')
      expect(projectArg).toBe(TEST_PROJECT.id)
      expect(eventsMock).toHaveBeenCalledWith(51, 'T-003.1', TEST_PROJECT.id)
    })
  })

  describe('events endpoint usage', () => {
    it('transcript loading uses GET /api/issues/:number/sessions/:name/events', async () => {
      const api = await import('../src/entities/coder-session/api/client')
      const metadataMock = api.getAgentSessionMetadata as unknown as ReturnType<typeof vi.fn>
      const eventsMock = api.getAgentSessionEvents as unknown as ReturnType<typeof vi.fn>

      const events: AgentSessionEvent[] = [
        makeRawEvent({ id: 1, sequence: 1, type: 'mohist_prompt', payload: { text: 'Implement split', kind: 'task' } }),
        makeRawEvent({ id: 2, sequence: 2, type: 'agent_message_chunk', payload: { text: 'Doing it now.' }, createdAt: '2024-01-01T10:00:01.000Z' }),
        makeRawEvent({ id: 3, sequence: 3, type: 'agent_session_terminal', payload: { status: 'completed' }, createdAt: '2024-01-01T10:00:02.000Z' }),
      ]

      endpointMocks.params = { number: '77', sessionName: 'plan' }
      setupEndpointMocks({
        sessions: [{ ...makeSessionsForLookup()[0], id: 'proj/wr/plan', sessionName: 'plan', executionId: 'exec-plan' }],
        metadata: makeMetadata({ id: 'proj/wr/plan', sessionName: 'plan', title: 'Plan split' }),
        events,
      })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(metadataMock).toHaveBeenCalledWith(77, 'plan', TEST_PROJECT.id)
      })
      await waitFor(() => {
        expect(eventsMock).toHaveBeenCalledWith(77, 'plan', TEST_PROJECT.id)
      })

      expect(eventsMock.mock.calls[0][0]).toBe(77)
      expect(eventsMock.mock.calls[0][1]).toBe('plan')
      expect(eventsMock.mock.calls[0][2]).toBe(TEST_PROJECT.id)
    })
  })

  describe('header metadata without turns or workflowLogs', () => {
    it('renders header from metadata even when response has no turns and no workflowLogs', async () => {
      endpointMocks.params = { number: '51', sessionName: 'T-003.1' }
      const metadata = makeMetadata({ id: 'proj/wr/T-003.1', sessionName: 'T-003.1' })
      expect((metadata as unknown as Record<string, unknown>).turns).toBeUndefined()
      expect((metadata as unknown as Record<string, unknown>).workflowLogs).toBeUndefined()

      setupEndpointMocks({
        sessions: makeSessionsForLookup(),
        metadata,
        issue: { number: 51, title: 'Split session endpoints' },
      })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('Issue #51')).toBeInTheDocument()
      })
      expect(screen.getByText('Implement endpoint split')).toBeInTheDocument()
      expect(screen.getByText('Completed')).toBeInTheDocument()
      expect(screen.getByText('Build')).toBeInTheDocument()
      expect(screen.getByText('claude-3-5-sonnet')).toBeInTheDocument()
      expect(screen.getByText(/session-?1h 00m|1h 00m/)).toBeInTheDocument()
    })

    it('does not require pre-projected turn content in the metadata to render the header', async () => {
      endpointMocks.params = { number: '123', sessionName: 'plan' }
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

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('Plan-only metadata')).toBeInTheDocument()
      })
      expect(screen.getByText('Plan')).toBeInTheDocument()
    })
  })

  describe('transcript rendered from raw events via shared projection', () => {
    it('renders transcript content derived from viewSessionEvents projection of raw events', async () => {
      const events: AgentSessionEvent[] = [
        makeRawEvent({
          id: 1,
          sequence: 1,
          type: 'mohist_prompt',
          payload: { text: 'T-009 split prompt', kind: 'task' },
          createdAt: '2024-01-01T10:00:00.000Z',
        }),
        makeRawEvent({
          id: 2,
          sequence: 2,
          type: 'agent_thought_chunk',
          payload: { text: 'Considering endpoint shapes' },
          createdAt: '2024-01-01T10:00:01.000Z',
        }),
        makeRawEvent({
          id: 3,
          sequence: 3,
          type: 'agent_message_chunk',
          payload: { text: 'Metadata endpoint first, events on demand.' },
          createdAt: '2024-01-01T10:00:02.000Z',
        }),
        makeRawEvent({
          id: 4,
          sequence: 4,
          type: 'tool_call',
          payload: {
            toolCallId: 'tc-t009',
            kind: 'read',
            toolName: 'read',
            title: 'Read src/session.ts',
            rawInput: '{"file_path":"src/session.ts"}',
            status: 'started',
          },
          createdAt: '2024-01-01T10:00:03.000Z',
        }),
        makeRawEvent({
          id: 5,
          sequence: 5,
          type: 'tool_call_update',
          payload: {
            toolCallId: 'tc-t009',
            status: 'completed',
            rawOutput: 'export function split() { ... }',
          },
          createdAt: '2024-01-01T10:00:04.000Z',
        }),
        makeRawEvent({
          id: 6,
          sequence: 6,
          type: 'agent_session_terminal',
          payload: { status: 'completed' },
          createdAt: '2024-01-01T10:00:05.000Z',
        }),
      ]

      const projected: SessionEvent[] = events.map((e) => ({
        id: e.id,
        sequence: e.sequence,
        type: e.type,
        payload: e.payload,
        createdAt: e.createdAt,
      }))
      const chat = viewSessionEvents(projected, 'chat')
      expect(chat.kind).toBe('chat')
      expect(chat.turns).toHaveLength(1)
      expect(chat.turns[0].prompt.text).toBe('T-009 split prompt')
      const textPart = chat.turns[0].parts.find((p) => p.partType === 'text')
      expect(textPart?.text).toBe('Metadata endpoint first, events on demand.')
      const reasoningPart = chat.turns[0].parts.find((p) => p.partType === 'reasoning')
      expect(reasoningPart?.text).toBe('Considering endpoint shapes')
      const toolPart = chat.turns[0].parts.find((p) => p.partType === 'tool')
      expect(toolPart?.toolCallId).toBe('tc-t009')
      expect(toolPart?.toolName).toBe('read')

      endpointMocks.params = { number: '51', sessionName: 'T-003.1' }
      setupEndpointMocks({
        sessions: makeSessionsForLookup(),
        metadata: makeMetadata({ id: 'proj/wr/T-003.1', sessionName: 'T-003.1' }),
        events,
      })

      const { container } = renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('Show full prompt')).toBeInTheDocument()
      })
      fireEvent.click(screen.getByText('Show full prompt'))
      await waitFor(() => {
        expect(screen.getByText('T-009 split prompt')).toBeInTheDocument()
      })
      expect(screen.getByText('Metadata endpoint first, events on demand.')).toBeInTheDocument()
      expect(screen.getByText('Considering endpoint shapes')).toBeInTheDocument()
      expect(container.textContent ?? '').toContain('T-009 split prompt')
      expect(container.textContent ?? '').toContain('Metadata endpoint first, events on demand.')
    })

    it('ignores server-projected turns payload on events and projects raw events through viewSessionEvents', async () => {
      const events: AgentSessionEvent[] = [
        makeRawEvent({
          id: 1,
          sequence: 1,
          type: 'mohist_prompt',
          payload: { text: 'Real prompt text from raw events', kind: 'task' },
          createdAt: '2024-01-01T10:00:00.000Z',
        }),
        makeRawEvent({
          id: 2,
          sequence: 2,
          type: 'agent_message_chunk',
          payload: {
            text: 'Assistant reply from raw event',
            turns: [{ id: 'server-turn-1', assistant: [{ partType: 'text', text: 'Server-side projection' }] }],
            workflowLogs: [{ type: 'agent_message_chunk', text: 'Server-side log' }],
          },
          createdAt: '2024-01-01T10:00:01.000Z',
        }),
        makeRawEvent({
          id: 3,
          sequence: 3,
          type: 'agent_session_terminal',
          payload: { status: 'completed' },
          createdAt: '2024-01-01T10:00:02.000Z',
        }),
      ]

      endpointMocks.params = { number: '51', sessionName: 'T-003.1' }
      setupEndpointMocks({
        sessions: makeSessionsForLookup(),
        metadata: makeMetadata({ id: 'proj/wr/T-003.1', sessionName: 'T-003.1' }),
        events,
      })

      const { container } = renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('Show full prompt')).toBeInTheDocument()
      })
      fireEvent.click(screen.getByText('Show full prompt'))
      await waitFor(() => {
        expect(screen.getByText('Real prompt text from raw events')).toBeInTheDocument()
      })
      expect(screen.getByText('Assistant reply from raw event')).toBeInTheDocument()
      expect(container.textContent ?? '').not.toContain('Server-side projection')
      expect(container.textContent ?? '').not.toContain('Server-side log')
    })

    it('renders the same chat transcript after a fresh render from raw events as the live view', async () => {
      const events: AgentSessionEvent[] = [
        makeRawEvent({
          id: 1,
          sequence: 1,
          type: 'mohist_prompt',
          payload: { text: 'Reload from raw events', kind: 'task' },
          createdAt: '2024-01-01T10:00:00.000Z',
        }),
        makeRawEvent({
          id: 2,
          sequence: 2,
          type: 'agent_message_chunk',
          payload: { text: 'Raw-event assistant content' },
          createdAt: '2024-01-01T10:00:01.000Z',
        }),
        makeRawEvent({
          id: 3,
          sequence: 3,
          type: 'agent_thought_chunk',
          payload: { text: 'Raw-event reasoning content' },
          createdAt: '2024-01-01T10:00:02.000Z',
        }),
        makeRawEvent({
          id: 4,
          sequence: 4,
          type: 'agent_session_terminal',
          payload: { status: 'completed' },
          createdAt: '2024-01-01T10:00:03.000Z',
        }),
      ]

      const projected: SessionEvent[] = events.map((e) => ({
        id: e.id,
        sequence: e.sequence,
        type: e.type,
        payload: e.payload,
        createdAt: e.createdAt,
      }))
      const chat = viewSessionEvents(projected, 'chat')
      const expectedPrompt = chat.turns[0].prompt.text
      const expectedAssistantText = (chat.turns[0].parts.find((p) => p.partType === 'text') as { text: string } | undefined)?.text
      const expectedReasoningText = (chat.turns[0].parts.find((p) => p.partType === 'reasoning') as { text: string } | undefined)?.text
      expect(expectedPrompt).toBe('Reload from raw events')
      expect(expectedAssistantText).toBe('Raw-event assistant content')
      expect(expectedReasoningText).toBe('Raw-event reasoning content')

      endpointMocks.params = { number: '51', sessionName: 'T-003.1' }
      setupEndpointMocks({
        sessions: makeSessionsForLookup(),
        metadata: makeMetadata({ id: 'proj/wr/T-003.1', sessionName: 'T-003.1' }),
        events,
      })

      const { container } = renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('Show full prompt')).toBeInTheDocument()
      })
      fireEvent.click(screen.getByText('Show full prompt'))
      await waitFor(() => {
        expect(screen.getByText(expectedPrompt)).toBeInTheDocument()
      })
      expect(screen.getByText(expectedAssistantText as string)).toBeInTheDocument()
      expect(screen.getByText(expectedReasoningText as string)).toBeInTheDocument()
      expect(container.textContent ?? '').toContain(expectedPrompt)
    })
  })
})
