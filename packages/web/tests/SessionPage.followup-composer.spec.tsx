import { describe, it, expect, afterEach, beforeEach, vi } from 'vitest'
import { http, HttpResponse } from 'msw'
import { TEST_PROJECT, baseRender, screen, waitFor } from './test-utils'
import { SessionPage } from '../src/pages/session/ui/SessionPage'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ProjectProvider } from '../src/entities/project/model/ProjectContext'
import { MemoryRouter, Routes, Route } from 'react-router-dom'
import { useMswServer } from '../tests/support/msw'
import React from 'react'
import type {
  AgentSessionMetadata,
  AgentSessionTranscriptResponse,
  SessionTurn,
} from '../src/entities/coder-session'

const ISSUE = 42
const SESSION = 'T-003.1'
const ISSUES = `*/api/projects/:projectId/issues/${ISSUE}`
const SESSION_META = `*/api/projects/:projectId/issues/${ISSUE}/sessions/${SESSION}`
const SESSION_TRANSCRIPT = `*/api/projects/:projectId/issues/${ISSUE}/sessions/${SESSION}/transcript`
const CODER_SESSIONS = `*/api/projects/:projectId/issues/${ISSUE}/coder-sessions`

let issueData: unknown = null
let sessionsData: unknown[] = []
let sessionsLoading = false
let metadata: AgentSessionMetadata | null = null
let transcript: AgentSessionTranscriptResponse = { turns: [], partCount: 0, lastActivityAt: null }

function sessionHandlers() {
  return [
    http.get(ISSUES, () => HttpResponse.json({ success: true, data: issueData })),
    http.get(CODER_SESSIONS, () => {
      if (sessionsLoading) return new Promise(() => {})
      return HttpResponse.json({ success: true, data: sessionsData })
    }),
    http.get(SESSION_META, () => HttpResponse.json({ success: true, data: metadata })),
    http.get(SESSION_TRANSCRIPT, () => HttpResponse.json({ success: true, data: transcript })),
  ]
}

useMswServer(...sessionHandlers())

const originalScrollTo = Element.prototype.scrollTo
const queryClients: QueryClient[] = []

beforeEach(() => {
  issueData = null
  sessionsData = []
  sessionsLoading = false
  metadata = null
  transcript = { turns: [], partCount: 0, lastActivityAt: null }
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

    renderWithQueryClient(<SessionPage />)

    await waitFor(() => {
      expect(screen.getByTestId('session-followup-composer')).toBeInTheDocument()
    })
    expect(screen.getByTestId('session-followup-input')).toBeInTheDocument()
    expect(screen.getByTestId('session-followup-send')).toBeInTheDocument()
    expect(screen.queryByTestId('session-followup-composer')).not.toHaveAttribute('data-disabled', 'true')
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

    renderWithQueryClient(<SessionPage />)

    await waitFor(() => {
      expect(screen.getByTestId('session-followup-composer')).toBeInTheDocument()
    })
    const composer = screen.getByTestId('session-followup-composer')
    expect(composer).toHaveAttribute('data-disabled', 'true')
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

    renderWithQueryClient(<SessionPage />)

    await waitFor(() => {
      expect(screen.getByTestId('session-followup-composer')).toBeInTheDocument()
    })
    expect(screen.getByTestId('session-followup-input')).toBeInTheDocument()
    expect(screen.getByTestId('session-followup-send')).toBeInTheDocument()
  })
})
