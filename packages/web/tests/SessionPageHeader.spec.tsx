import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { TEST_PROJECT, screen, waitFor, within } from './test-utils'
import type { ReactElement } from 'react'
import { http, HttpResponse } from 'msw'
import { SessionPage } from '../src/pages/session/ui/SessionPage'
import type { SessionTurn, CoderSessionDetail, SessionMetadata, AgentSessionMetadata } from '../src/entities/coder-session'
import { useMswServer } from './support/msw'
import { renderWithQueryClient as renderPageWithQueryClient, makeTurn, convertLegacyToAgentMetadata, queryClients } from './session-page-test-utils'
import { setScopedValue } from './support/scoped-property'

const sessionPageMocks = {
  sessions: [] as any[],
  sessionsLoading: false,
  issue: null as any,
  detail: null as CoderSessionDetail | null,
  metadata: null as AgentSessionMetadata | null,
  turns: null as SessionTurn[] | null,
  detailError: null as Error | null,
  detailPending: false,
  params: { number: '123', sessionName: 'session-123' },
  workflowRunSessions: [] as Array<{
    id: string
    sessionName: string
    status: string
    createdAt: string
  }>,
}

type SessionApiCall = {
  kind: 'metadata' | 'transcript'
  issueNumber: string
  sessionName: string
  projectId: string
}

let sessionApiCalls: SessionApiCall[] = []

useMswServer(
  http.get('*/api/projects/:projectId/issues/:issueNumber/coder-sessions', () => {
    if (sessionPageMocks.sessionsLoading) return new Promise<never>(() => {})
    return HttpResponse.json({ success: true, data: sessionPageMocks.sessions })
  }),
  http.get('*/api/projects/:projectId/issues/:issueNumber/sessions/:sessionName/transcript', ({ params }) => {
    sessionApiCalls.push({
      kind: 'transcript',
      issueNumber: String(params.issueNumber),
      sessionName: String(params.sessionName),
      projectId: String(params.projectId),
    })
    if (sessionPageMocks.detailPending) return new Promise<never>(() => {})
    if (sessionPageMocks.detailError) {
      return HttpResponse.json({ success: false, error: sessionPageMocks.detailError.message }, { status: 500 })
    }
    const turns = sessionPageMocks.turns ?? []
    return HttpResponse.json({
      success: true,
      data: {
        turns,
        partCount: turns.reduce((total, turn) => total + turn.assistant.length, 0),
        lastActivityAt: turns.at(-1)?.completedAt ?? turns.at(-1)?.startedAt ?? null,
      },
    })
  }),
  http.get('*/api/projects/:projectId/issues/:issueNumber/sessions/:sessionName', ({ params }) => {
    sessionApiCalls.push({
      kind: 'metadata',
      issueNumber: String(params.issueNumber),
      sessionName: String(params.sessionName),
      projectId: String(params.projectId),
    })
    if (sessionPageMocks.detailPending) return new Promise<never>(() => {})
    if (sessionPageMocks.detailError) {
      return HttpResponse.json({ success: false, error: sessionPageMocks.detailError.message }, { status: 500 })
    }
    return HttpResponse.json({ success: true, data: sessionPageMocks.metadata })
  }),
  http.get('*/api/projects/:projectId/issues/:issueNumber', () =>
    HttpResponse.json({ success: true, data: sessionPageMocks.issue })),
  http.get('*/api/workflow-runs/:workflowRunId/sessions', () =>
    HttpResponse.json({ success: true, data: sessionPageMocks.workflowRunSessions })),
)

function renderWithQueryClient(_ui: ReactElement, initialEntry?: string) {
  const { number, sessionName } = sessionPageMocks.params
  const route = initialEntry ?? `/issues/${encodeURIComponent(number)}/workflow/sessions/${encodeURIComponent(sessionName)}`
  return renderPageWithQueryClient(<SessionPage />, route)
}

beforeEach(() => {
  setScopedValue(navigator, 'clipboard', { writeText: vi.fn().mockResolvedValue(undefined) })
  vi.clearAllMocks()
  sessionApiCalls = []
  sessionPageMocks.sessions = []
  sessionPageMocks.sessionsLoading = false
  sessionPageMocks.issue = null
  sessionPageMocks.detail = null
  sessionPageMocks.metadata = null
  sessionPageMocks.turns = null
  sessionPageMocks.detailError = null
  sessionPageMocks.detailPending = false
  sessionPageMocks.params = { number: '123', sessionName: 'session-123' }
  sessionPageMocks.workflowRunSessions = []
  setScopedValue(Element.prototype, 'scrollTo', vi.fn())
})

afterEach(() => {
  vi.useRealTimers()
  for (const queryClient of queryClients) queryClient.clear()
  queryClients.length = 0
})

describe('SessionPage header and states', () => {
  function makeMockSession() {
    return {
      id: 'session-123',
      runtimeSessionId: 'acp-123',
      executionId: 'exec-123',
      taskDescription: 'Test task',
      status: 'completed',
      createdAt: '2024-01-01T10:00:00.000Z',
      completedAt: '2024-01-01T11:00:00.000Z',
      model: 'claude-3-5-sonnet',
      runtime: null,
      stage: 'build',
      title: 'Test Session',
    }
  }

  function makeMockMetadata(overrides: Partial<SessionMetadata> = {}): SessionMetadata {
    return {
      sessionId: 'session-123',
      runtimeSessionId: 'acp-123',
      executionId: 'exec-123',
      title: 'Test Session',
      status: 'completed',
      statusKind: 'completed',
      model: 'claude-3-5-sonnet',
      stage: 'build',
      createdAt: '2024-01-01T10:00:00.000Z',
      completedAt: '2024-01-01T11:00:00.000Z',
      lastActivityAt: '2024-01-01T10:05:00.000Z',
      eventCount: 10,
      toolCount: 5,
      turnCount: 2,
      ...overrides,
    }
  }

  function makeMockDetail(overrides: Partial<{ id: string; metadata: SessionMetadata; turns: SessionTurn[]; incomplete: boolean; status: string; completedAt: string | null }> = {}): CoderSessionDetail {
    return {
      id: 'session-123',
      runtimeSessionId: 'acp-123',
      executionId: 'exec-123',
      taskDescription: 'Test task',
      status: 'completed',
      createdAt: '2024-01-01T10:00:00.000Z',
      completedAt: '2024-01-01T11:00:00.000Z',
      model: 'claude-3-5-sonnet',
      runtime: null,
      stage: 'build',
      title: 'Test Session',
      metadata: makeMockMetadata(),
      turns: [],
      incomplete: false,
      ...overrides,
    }
  }

  function setupSessionPage({
    sessions = [makeMockSession()],
    issue = null,
    detail = makeMockDetail(),
    metadata = null,
    turns = null,
    sessionsLoading = false,
    detailError = null,
    detailPending = false,
  }: {
    sessions?: any[]
    issue?: any
    detail?: CoderSessionDetail | null
    metadata?: AgentSessionMetadata | null
    turns?: SessionTurn[] | null
    sessionsLoading?: boolean
    detailError?: Error | null
    detailPending?: boolean
  } = {}) {
    sessionPageMocks.sessions = sessions
    sessionPageMocks.issue = issue
    sessionPageMocks.detail = detail
    if (metadata) {
      sessionPageMocks.metadata = metadata
    } else if (detail) {
      sessionPageMocks.metadata = convertLegacyToAgentMetadata(detail)
    } else {
      sessionPageMocks.metadata = null
    }
    if (turns) {
      sessionPageMocks.turns = turns
    } else if (detail?.turns && detail.turns.length > 0) {
      sessionPageMocks.turns = detail.turns
    } else {
      sessionPageMocks.turns = []
    }
    sessionPageMocks.sessionsLoading = sessionsLoading
    sessionPageMocks.detailError = detailError
    sessionPageMocks.detailPending = detailPending
  }

  describe('header displays session metadata', () => {
    it('shows issue link, stage, model, last activity, and status badge from metadata', async () => {
      const detail = makeMockDetail({
        metadata: makeMockMetadata({
          status: 'completed',
          statusKind: 'completed',
          stage: 'build',
          model: 'claude-3-5-sonnet',
          turnCount: 3,
          lastActivityAt: '2024-01-01T10:05:00.000Z',
        }),
      })
      setupSessionPage({ detail, issue: { number: 123, title: 'Test Issue' } })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('Issue #123')).toBeInTheDocument()
      })
      expect(screen.getByText('Test Issue')).toBeInTheDocument()
      expect(screen.getByText('Build')).toBeInTheDocument()
      expect(screen.getByText('claude-3-5-sonnet')).toBeInTheDocument()
    })

    it('shows transcript turn count derived from persisted transcript when transcript is rendered', async () => {
      const detail = makeMockDetail({
        metadata: makeMockMetadata({
          status: 'completed',
          statusKind: 'completed',
          stage: 'build',
          model: 'claude-3-5-sonnet',
          turnCount: 3,
          lastActivityAt: '2024-01-01T10:05:00.000Z',
        }),
      })
      const turns: SessionTurn[] = [
        makeTurn({ id: 'turn-1', user: { role: 'mohist', text: 'first', kind: 'task', sentAt: '2024-01-01T10:00:00.000Z' } }),
        makeTurn({ id: 'turn-2', startedAt: '2024-01-01T10:01:00.000Z', user: { role: 'mohist', text: 'second', kind: 'task', sentAt: '2024-01-01T10:01:00.000Z' } }),
        makeTurn({ id: 'turn-3', startedAt: '2024-01-01T10:02:00.000Z', user: { role: 'mohist', text: 'third', kind: 'task', sentAt: '2024-01-01T10:02:00.000Z' } }),
      ]
      setupSessionPage({ detail, turns })

      renderWithQueryClient(<SessionPage />)

      const header = await screen.findByTestId('session-header')
      await within(header).findByText('3 turns')
    })

    it('shows changed-files summary in header when metadata has changedFiles', async () => {
      const detail = makeMockDetail({
        metadata: makeMockMetadata({
          changedFiles: [
            { path: 'src/index.ts', operation: 'modified', additions: 5, deletions: 2 },
          ],
        }),
      })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText(/1 file changed/i)).toBeInTheDocument()
      })
    })

    it('requests metadata before raw events when opening a session route', async () => {
      sessionPageMocks.params = { number: '51', sessionName: 'T-003.1' } as any
      const detail = makeMockDetail({
        id: 'proj/wr/T-003.1',
        metadata: makeMockMetadata({ sessionId: 'proj/wr/T-003.1', sessionName: 'T-003.1' }),
      })
      setupSessionPage({
        sessions: [{ ...makeMockSession(), id: 'proj/wr/T-003.1', sessionName: 'T-003.1' }],
        detail,
        metadata: { ...convertLegacyToAgentMetadata(detail), sessionName: 'T-003.1' },
      })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(sessionApiCalls.map((call) => call.kind)).toEqual(['metadata', 'transcript'])
      })
      const callOrder = sessionApiCalls.map((call) => call.kind)
      expect(callOrder.indexOf('metadata')).toBeLessThan(callOrder.indexOf('transcript'))
      expect(sessionApiCalls).toEqual([
        { kind: 'metadata', issueNumber: '51', sessionName: 'T-003.1', projectId: TEST_PROJECT.id },
        { kind: 'transcript', issueNumber: '51', sessionName: 'T-003.1', projectId: TEST_PROJECT.id },
      ])
    })

    it('does not request raw events when metadata has not yet loaded', async () => {
      sessionPageMocks.params = { number: '77', sessionName: 'late' } as any
      const detail = makeMockDetail({
        id: 'proj/wr/late',
        metadata: makeMockMetadata({ sessionId: 'proj/wr/late', sessionName: 'late' }),
      })
      setupSessionPage({
        sessions: [{ ...makeMockSession(), id: 'proj/wr/late', sessionName: 'late' }],
        detail,
        detailPending: true,
      })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText(/loading/i)).toBeInTheDocument()
      })

      expect(sessionApiCalls).toEqual([
        { kind: 'metadata', issueNumber: '77', sessionName: 'late', projectId: TEST_PROJECT.id },
      ])
    })

    it('loads workflow session metadata by route session name', async () => {
      sessionPageMocks.params = { number: '123', sessionName: 'plan' } as any
      const detail = makeMockDetail({
        id: 'proj_1/wr_1/plan',
        metadata: makeMockMetadata({ sessionId: 'proj_1/wr_1/plan', sessionName: 'plan' }),
      })
      setupSessionPage({
        sessions: [{ ...makeMockSession(), id: 'proj_1/wr_1/plan', sessionName: 'plan' }],
        detail,
        metadata: { ...convertLegacyToAgentMetadata(detail), sessionName: 'plan' },
      })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(sessionApiCalls).toEqual([
          { kind: 'metadata', issueNumber: '123', sessionName: 'plan', projectId: TEST_PROJECT.id },
          { kind: 'transcript', issueNumber: '123', sessionName: 'plan', projectId: TEST_PROJECT.id },
        ])
      })
    })

    it('shows duration for completed sessions', async () => {
      const detail = makeMockDetail({
        metadata: makeMockMetadata({
          completedAt: '2024-01-01T11:00:00.000Z',
          status: 'completed',
          statusKind: 'completed',
        }),
      })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('1h 00m')).toBeInTheDocument()
      })
    })

    it('does not show duration for running sessions', async () => {
      const detail = makeMockDetail({
        metadata: makeMockMetadata({
          status: 'running',
          statusKind: 'live',
          completedAt: null,
        }),
      })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.queryByText(/duration/i)).not.toBeInTheDocument()
      })
    })
  })

  describe('status kind display', () => {
    it('shows live status badge for running sessions with recent activity', async () => {
      const detail = makeMockDetail({
        metadata: makeMockMetadata({
          status: 'running',
          statusKind: 'live',
          lastActivityAt: new Date().toISOString(),
        }),
      })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      const header = await screen.findByTestId('session-header')
      await within(header).findByText('Running')
    })

    it('shows stale status badge for running sessions with old activity', async () => {
      const detail = makeMockDetail({
        metadata: makeMockMetadata({
          status: 'running',
          statusKind: 'stale',
          lastActivityAt: '2024-01-01T10:00:00.000Z',
        }),
      })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      const header = await screen.findByTestId('session-header')
      await within(header).findByText('Stale')
    })

    it('shows finalizing status badge when session is finalizing', async () => {
      const detail = makeMockDetail({
        metadata: makeMockMetadata({
          status: 'running',
          statusKind: 'finalizing',
          completedAt: '2024-01-01T11:00:00.000Z',
        }),
      })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      const header = await screen.findByTestId('session-header')
      await within(header).findByText('Finalizing')
    })

    it('shows failed status badge for failed sessions', async () => {
      const detail = makeMockDetail({
        metadata: makeMockMetadata({
          status: 'failed',
          statusKind: 'failed',
          completedAt: '2024-01-01T11:00:00.000Z',
        }),
      })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)
      const header = await screen.findByTestId('session-header')
      const badge = await within(header).findByTestId('session-status-badge')
      expect(badge).toHaveAttribute('data-status-kind', 'failed')
      expect(badge.textContent).toContain('Session failed')
    })
  })

  describe('loading and error state rendering', () => {
    it('shows loading state while sessions are loading', async () => {
      setupSessionPage({ sessionsLoading: true })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText(/loading/i)).toBeInTheDocument()
      })
    })

    it('shows loading state while detail is loading', async () => {
      setupSessionPage({ detailPending: true })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText(/loading/i)).toBeInTheDocument()
      })
    })

    it('shows API error state when detail query fails', async () => {
      setupSessionPage({ detailError: new Error('API Error') })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText(/error/i)).toBeInTheDocument()
      })
    })

    it('shows waiting for activity state when session is running but no turns yet', async () => {
      const detail = makeMockDetail({
        metadata: makeMockMetadata({
          status: 'running',
          statusKind: 'live',
        }),
        turns: [],
      })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText(/waiting/i)).toBeInTheDocument()
      })
    })

    it('shows empty state when session has no recorded activity', async () => {
      const detail = makeMockDetail({
        metadata: makeMockMetadata({
          status: 'completed',
          statusKind: 'completed',
        }),
        turns: [],
      })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText(/no activity/i)).toBeInTheDocument()
      })
    })

    it('treats completed session with no events as empty state', async () => {
      const detail = makeMockDetail({
        metadata: makeMockMetadata({
          status: 'completed',
          statusKind: 'completed',
        }),
        turns: [],
        incomplete: true,
      })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText(/no activity/i)).toBeInTheDocument()
      })
    })
  })

  describe('main transcript branch renders shared session header', () => {
    it('renders SessionHeader above the transcript with title, status badge, stage, issue link, and turn count', async () => {
      const turns: SessionTurn[] = [
        makeTurn({
          id: 'turn-1',
          user: {
            role: 'mohist',
            text: 'First turn prompt',
            kind: 'task',
            sentAt: '2024-01-01T10:00:00.000Z',
          },
        }),
        makeTurn({
          id: 'turn-2',
          startedAt: '2024-01-01T10:01:00.000Z',
          user: {
            role: 'mohist',
            text: 'Second turn prompt',
            kind: 'task',
            sentAt: '2024-01-01T10:01:00.000Z',
          },
        }),
      ]
      const detail = makeMockDetail({
        metadata: makeMockMetadata({
          status: 'completed',
          statusKind: 'completed',
          stage: 'build',
          model: 'claude-3-5-sonnet',
          turnCount: 2,
        }),
      })
      setupSessionPage({ detail, turns, issue: { number: 123, title: 'Test Issue' } })

      renderWithQueryClient(<SessionPage />)

      // Wait until the header is rendered (metadata resolved).
      await screen.findByText('Issue #123')

      // Same SessionHeader shared with the empty/waiting/incomplete branches.
      expect(screen.getByText('Test Issue')).toBeInTheDocument()
      expect(screen.getByText('Build')).toBeInTheDocument()
      // "Completed" appears in both the SessionHeader and the sticky title strip.
      const completedBadges = screen.getAllByText('Completed')
      expect(completedBadges.length).toBeGreaterThanOrEqual(1)
      // h1 always renders a non-empty session title.
      const h1 = document.querySelector('h1')
      expect(h1).not.toBeNull()
      expect(h1?.textContent?.trim().length ?? 0).toBeGreaterThan(0)

      const issueLink = screen.getByRole('link', { name: /Issue #123/ })
      await within(issueLink.closest('.border-b') as HTMLElement).findByText('2 turns')

      // Issue back-link resolves to the issue page (not a session sub-route).
      expect(issueLink.getAttribute('href')).toBe('/Test%20Project/issues/123')

      // The main branch must NOT show the legacy sticky compact summary.
      expect(screen.queryByText(/Jump to bottom/i)).not.toBeInTheDocument()
    })

    it('renders the recovery bar inside the transcript scroll container on the main branch', async () => {
      const turns = [makeTurn({ id: 'turn-1' })]
      const detail = makeMockDetail({
        metadata: makeMockMetadata({
          status: 'completed',
          statusKind: 'completed',
        }),
      })
      setupSessionPage({ detail, turns })

      renderWithQueryClient(<SessionPage />)

      await screen.findByText('Issue #123')

      expect(screen.getByTestId('session-transcript-scroll-container')).toContainElement(screen.getByTestId('session-recovery-bar'))
    })

    it('renders the same header on the main branch as on the empty branch', async () => {
      const baseMetadata = makeMockMetadata({
        status: 'completed',
        statusKind: 'completed',
        stage: 'build',
        model: 'claude-3-5-sonnet',
      })

      const mainDetail = makeMockDetail({ metadata: baseMetadata })
      setupSessionPage({
        detail: mainDetail,
        turns: [makeTurn({ id: 'turn-1' })],
      })

      const { unmount } = renderWithQueryClient(<SessionPage />)
      await screen.findByTestId('session-transcript-scroll-container')
      await within(screen.getByRole('link', { name: /Issue #123/ }).closest('.border-b') as HTMLElement).findByText('1 turn')
      expect(screen.getByText('Build')).toBeInTheDocument()
      const completedBadges = screen.getAllByText('Completed')
      expect(completedBadges.length).toBeGreaterThanOrEqual(1)
      expect(screen.queryByText(/Jump to bottom/i)).not.toBeInTheDocument()
      expect(screen.queryByText(/No activity recorded/i)).not.toBeInTheDocument()
      unmount()

      const emptyDetail = makeMockDetail({
        metadata: baseMetadata,
        turns: [],
      })
      setupSessionPage({
        detail: emptyDetail,
        turns: [],
      })

      renderWithQueryClient(<SessionPage />)

      await screen.findByText('Issue #123')
      expect(screen.getByText('Build')).toBeInTheDocument()
      const emptyCompletedBadges = screen.getAllByText('Completed')
      expect(emptyCompletedBadges.length).toBeGreaterThanOrEqual(1)
      expect(screen.getByText(/No activity recorded/i)).toBeInTheDocument()
      expect(screen.getByTestId('session-transcript-scroll-container')).toContainElement(screen.getByTestId('session-recovery-bar'))
    })

    it('main branch session header back-link uses whitespace-nowrap and never wraps', async () => {
      const turns = [makeTurn({ id: 'turn-1' })]
      const detail = makeMockDetail({
        metadata: makeMockMetadata({
          status: 'completed',
          statusKind: 'completed',
        }),
      })
      setupSessionPage({ detail, turns, issue: { number: 123, title: 'Test Issue' } })

      renderWithQueryClient(<SessionPage />)

      await screen.findByText('Issue #123')

      const issueLink = screen.getByRole('link', { name: /Issue #123/ })
      expect(issueLink.className).toContain('whitespace-nowrap')
      expect(issueLink.className).toContain('shrink-0')
    })

    it('main branch session header metadata cluster stacks vertically below sm', async () => {
      const turns = [makeTurn({ id: 'turn-1' })]
      const detail = makeMockDetail({
        metadata: makeMockMetadata({
          status: 'completed',
          statusKind: 'completed',
          stage: 'build',
          model: 'claude-3-5-sonnet',
        }),
      })
      setupSessionPage({ detail, turns, issue: { number: 123, title: 'Test Issue' } })

      renderWithQueryClient(<SessionPage />)

      await screen.findByText('Issue #123')

      // The metadata cluster lives inside the SessionHeader and is the second flex child.
      // It must declare the mobile vertical stack AND the sm+ horizontal row layout
      // so it never wraps/overlaps on narrow viewports.
      const metadataCluster = screen.getByText('Build').parentElement as HTMLElement
      expect(metadataCluster.className).toContain('flex-col')
      expect(metadataCluster.className).toContain('sm:flex-row')
    })

    it('main branch session header issue title truncates with min-w-0', async () => {
      const longTitle = 'A'.repeat(120)
      const turns = [makeTurn({ id: 'turn-1' })]
      const detail = makeMockDetail({
        metadata: makeMockMetadata({
          status: 'completed',
          statusKind: 'completed',
        }),
      })
      setupSessionPage({ detail, turns, issue: { number: 123, title: longTitle } })

      renderWithQueryClient(<SessionPage />)

      await screen.findByText('Issue #123')

      const issueTitle = screen.getByText(longTitle)
      expect(issueTitle.className).toContain('truncate')
      expect(issueTitle.className).toContain('min-w-0')
    })
  })

  describe('sibling session navigation and sidebar', () => {
    function setWorkflowRunSessions(entries: Array<{ id: string; sessionName: string; status: string; createdAt: string }>) {
      sessionPageMocks.workflowRunSessions = entries
    }

    it('renders prev/next controls for a mid-sequence session and links to the chronologically adjacent siblings', async () => {
      sessionPageMocks.params = { number: '55', sessionName: 'build' } as any
      setWorkflowRunSessions([{ id: 's-plan', sessionName: 'plan', status: 'completed', createdAt: '2026-06-15T08:00:00.000Z' }, { id: 's-build', sessionName: 'build', status: 'running', createdAt: '2026-06-15T10:00:00.000Z' }, { id: 's-check', sessionName: 'check', status: 'completed', createdAt: '2026-06-15T12:00:00.000Z' }])
      setupSessionPage({
        detail: makeMockDetail({ id: 'session-build', metadata: makeMockMetadata({ status: 'running', statusKind: 'live', stage: 'build', sessionName: 'build', sessionId: 'session-build' }) }),
        turns: [makeTurn({ id: 'turn-1' })],
        sessions: [{ ...makeMockSession(), id: 'session-build', sessionName: 'build' }],
        issue: { number: 55, title: 'Issue 55', workflowRunId: 'wr-1' },
      })

      renderWithQueryClient(<SessionPage />)

      await screen.findByText('Issue #55')

      const prev = await screen.findByTestId('session-sibling-prev')
      const next = await screen.findByTestId('session-sibling-next')

      expect(prev.getAttribute('href')).toBe('/Test%20Project/issues/55/workflow/sessions/plan')
      expect(next.getAttribute('href')).toBe('/Test%20Project/issues/55/workflow/sessions/check')
      expect(prev.getAttribute('title')).toBe('Previous session: plan')
      expect(next.getAttribute('title')).toBe('Next session: check')
    })

    it('keeps Activity as the back destination after opening a sibling', async () => {
      sessionPageMocks.params = { number: '55', sessionName: 'build' } as any
      setWorkflowRunSessions([{ id: 's-plan', sessionName: 'plan', status: 'completed', createdAt: '2026-06-15T08:00:00.000Z' }, { id: 's-build', sessionName: 'build', status: 'running', createdAt: '2026-06-15T10:00:00.000Z' }])
      setupSessionPage({
        detail: makeMockDetail({ id: 'session-build', metadata: makeMockMetadata({ sessionName: 'build', sessionId: 'session-build' }) }),
        turns: [makeTurn({ id: 'turn-1' })],
        sessions: [{ ...makeMockSession(), id: 'session-build', sessionName: 'build' }],
        issue: { number: 55, title: 'Issue 55', workflowRunId: 'wr-1' },
      })

      renderWithQueryClient(<SessionPage />, '/issues/55/workflow/sessions/build?from=activity')

      const previous = await screen.findByTestId('session-sibling-prev')
      expect(previous).toHaveAttribute('href', '/Test%20Project/issues/55/workflow/sessions/plan?from=activity')
    })

    it('disables the previous control when the current session is the first sibling in createdAt order', async () => {
      sessionPageMocks.params = { number: '55', sessionName: 'plan' } as any
      setWorkflowRunSessions([
        { id: 's-plan', sessionName: 'plan', status: 'completed', createdAt: '2026-06-15T08:00:00.000Z' },
        { id: 's-build', sessionName: 'build', status: 'completed', createdAt: '2026-06-15T10:00:00.000Z' },
        { id: 's-check', sessionName: 'check', status: 'completed', createdAt: '2026-06-15T12:00:00.000Z' },
      ])

      const detail = makeMockDetail({
        id: 'session-plan',
        metadata: makeMockMetadata({
          status: 'completed',
          statusKind: 'completed',
          stage: 'plan',
          sessionName: 'plan',
          sessionId: 'session-plan',
        }),
      })
      setupSessionPage({
        detail,
        turns: [makeTurn({ id: 'turn-1' })],
        sessions: [{ ...makeMockSession(), id: 'session-plan', sessionName: 'plan' }],
        issue: { number: 55, title: 'Issue 55', workflowRunId: 'wr-1' },
      })

      renderWithQueryClient(<SessionPage />)

      await screen.findByText('Issue #55')

      expect(screen.queryByTestId('session-sibling-prev')).not.toBeInTheDocument()
      const disabledPrev = screen.getByTestId('session-sibling-prev-disabled')
      expect(disabledPrev.getAttribute('aria-disabled')).toBe('true')
      expect(disabledPrev.textContent).toContain('prev')

      const next = await screen.findByTestId('session-sibling-next')
      expect(next.getAttribute('href')).toBe('/Test%20Project/issues/55/workflow/sessions/build')
    })

    it('disables the next control when the current session is the last sibling in createdAt order', async () => {
      sessionPageMocks.params = { number: '55', sessionName: 'check' } as any
      setWorkflowRunSessions([
        { id: 's-plan', sessionName: 'plan', status: 'completed', createdAt: '2026-06-15T08:00:00.000Z' },
        { id: 's-build', sessionName: 'build', status: 'completed', createdAt: '2026-06-15T10:00:00.000Z' },
        { id: 's-check', sessionName: 'check', status: 'completed', createdAt: '2026-06-15T12:00:00.000Z' },
      ])

      const detail = makeMockDetail({
        id: 'session-check',
        metadata: makeMockMetadata({
          status: 'completed',
          statusKind: 'completed',
          stage: 'check',
          sessionName: 'check',
          sessionId: 'session-check',
        }),
      })
      setupSessionPage({
        detail,
        turns: [makeTurn({ id: 'turn-1' })],
        sessions: [{ ...makeMockSession(), id: 'session-check', sessionName: 'check' }],
        issue: { number: 55, title: 'Issue 55', workflowRunId: 'wr-1' },
      })

      renderWithQueryClient(<SessionPage />)

      await screen.findByText('Issue #55')

      const prev = await screen.findByTestId('session-sibling-prev')
      expect(prev.getAttribute('href')).toBe('/Test%20Project/issues/55/workflow/sessions/build')

      expect(screen.queryByTestId('session-sibling-next')).not.toBeInTheDocument()
      const disabledNext = screen.getByTestId('session-sibling-next-disabled')
      expect(disabledNext.getAttribute('aria-disabled')).toBe('true')
      expect(disabledNext.textContent).toContain('next')
    })

    it('renders the sidebar with one entry per workflow-run sibling, ordered by createdAt ascending', async () => {
      sessionPageMocks.params = { number: '55', sessionName: 'build' } as any
      setWorkflowRunSessions([
        { id: 's-check', sessionName: 'check', status: 'completed', createdAt: '2026-06-15T12:00:00.000Z' },
        { id: 's-plan', sessionName: 'plan', status: 'completed', createdAt: '2026-06-15T08:00:00.000Z' },
        { id: 's-build', sessionName: 'build', status: 'running', createdAt: '2026-06-15T10:00:00.000Z' },
      ])

      const detail = makeMockDetail({
        id: 'session-build',
        metadata: makeMockMetadata({
          status: 'running',
          statusKind: 'live',
          stage: 'build',
          sessionName: 'build',
          sessionId: 'session-build',
        }),
      })
      setupSessionPage({
        detail,
        turns: [makeTurn({ id: 'turn-1' })],
        sessions: [{ ...makeMockSession(), id: 'session-build', sessionName: 'build' }],
        issue: { number: 55, title: 'Issue 55', workflowRunId: 'wr-1' },
      })

      renderWithQueryClient(<SessionPage />)

      await screen.findByText('Issue #55')

      const sidebar = await screen.findByTestId('session-sibling-sidebar')
      const entries = within(sidebar).getAllByTestId('session-sibling-sidebar-entry')

      expect(entries).toHaveLength(3)
      const names = entries.map((node) => node.querySelector('span.font-mono')?.textContent)
      expect(names).toEqual(['plan', 'build', 'check'])

      const hrefs = entries.map((node) => node.getAttribute('href'))
      expect(hrefs).toEqual([
        '/Test%20Project/issues/55/workflow/sessions/plan',
        '/Test%20Project/issues/55/workflow/sessions/build',
        '/Test%20Project/issues/55/workflow/sessions/check',
      ])
    })

    it('highlights the currently viewed session in the sidebar and marks it with aria-current=page', async () => {
      sessionPageMocks.params = { number: '55', sessionName: 'build' } as any
      setWorkflowRunSessions([
        { id: 's-plan', sessionName: 'plan', status: 'completed', createdAt: '2026-06-15T08:00:00.000Z' },
        { id: 's-build', sessionName: 'build', status: 'running', createdAt: '2026-06-15T10:00:00.000Z' },
        { id: 's-check', sessionName: 'check', status: 'completed', createdAt: '2026-06-15T12:00:00.000Z' },
      ])

      const detail = makeMockDetail({
        id: 'session-build',
        metadata: makeMockMetadata({
          status: 'running',
          statusKind: 'live',
          stage: 'build',
          sessionName: 'build',
          sessionId: 'session-build',
        }),
      })
      setupSessionPage({
        detail,
        turns: [makeTurn({ id: 'turn-1' })],
        sessions: [{ ...makeMockSession(), id: 'session-build', sessionName: 'build' }],
        issue: { number: 55, title: 'Issue 55', workflowRunId: 'wr-1' },
      })

      renderWithQueryClient(<SessionPage />)

      await screen.findByText('Issue #55')

      const sidebar = await screen.findByTestId('session-sibling-sidebar')
      const entries = within(sidebar).getAllByTestId('session-sibling-sidebar-entry')

      const current = entries.find((node) => node.getAttribute('data-current') === 'true')
      expect(current).toBeDefined()
      expect(current?.getAttribute('aria-current')).toBe('page')
      expect(current?.textContent).toContain('current')
      expect(current?.className).toContain('bg-info-subtle')

      // Non-current entries are not marked current.
      const others = entries.filter((node) => node.getAttribute('data-current') !== 'true')
      expect(others).toHaveLength(2)
      others.forEach((node) => {
        expect(node.getAttribute('aria-current')).toBeNull()
      })
    })

    it('hides the sidebar entirely when the workflow run has no sessions but keeps prev/next always visible', async () => {
      sessionPageMocks.params = { number: '55', sessionName: 'build' } as any
      setWorkflowRunSessions([])

      const detail = makeMockDetail({
        id: 'session-build',
        metadata: makeMockMetadata({
          status: 'running',
          statusKind: 'live',
          stage: 'build',
          sessionName: 'build',
          sessionId: 'session-build',
        }),
      })
      setupSessionPage({
        detail,
        turns: [makeTurn({ id: 'turn-1' })],
        sessions: [{ ...makeMockSession(), id: 'session-build', sessionName: 'build' }],
        issue: { number: 55, title: 'Issue 55', workflowRunId: 'wr-1' },
      })

      renderWithQueryClient(<SessionPage />)

      await screen.findByText('Issue #55')

      expect(screen.queryByTestId('session-sibling-sidebar')).not.toBeInTheDocument()
      // The prev/next controls live in the header and are always rendered.
      expect(screen.getByTestId('session-sibling-prev-disabled')).toBeInTheDocument()
      expect(screen.getByTestId('session-sibling-next-disabled')).toBeInTheDocument()
    })

    it('uses the same session set as WorkflowSessionsPanel for the same workflow run', async () => {
      // The SessionPage sidebar and the WorkflowSessionsPanel both source their
      // session set from useWorkflowRunSessions(issue.workflowRunId). This test
      // verifies that even when useCoderSessions returns a different (legacy)
      // session list, the sidebar still reflects the workflow-run session set.
      sessionPageMocks.params = { number: '55', sessionName: 'build' } as any
      setWorkflowRunSessions([
        { id: 's-plan', sessionName: 'plan', status: 'completed', createdAt: '2026-06-15T08:00:00.000Z' },
        { id: 's-build', sessionName: 'build', status: 'running', createdAt: '2026-06-15T10:00:00.000Z' },
        { id: 's-check', sessionName: 'check', status: 'completed', createdAt: '2026-06-15T12:00:00.000Z' },
      ])

      const detail = makeMockDetail({
        id: 'session-build',
        metadata: makeMockMetadata({
          status: 'running',
          statusKind: 'live',
          stage: 'build',
          sessionName: 'build',
          sessionId: 'session-build',
        }),
      })
      // The legacy useCoderSessions list is intentionally empty and inconsistent
      // with the workflow run's session set. The sidebar must NOT follow it.
      setupSessionPage({
        detail,
        turns: [makeTurn({ id: 'turn-1' })],
        sessions: [],
        issue: { number: 55, title: 'Issue 55', workflowRunId: 'wr-1' },
      })

      renderWithQueryClient(<SessionPage />)

      await screen.findByText('Issue #55')

      const sidebar = await screen.findByTestId('session-sibling-sidebar')
      const entries = within(sidebar).getAllByTestId('session-sibling-sidebar-entry')
      const names = entries.map((node) => node.querySelector('span.font-mono')?.textContent)
      expect(names).toEqual(['plan', 'build', 'check'])
    })
  })
})
