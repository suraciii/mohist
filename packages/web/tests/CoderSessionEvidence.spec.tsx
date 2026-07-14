import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { http, HttpResponse } from 'msw'
import { TEST_PROJECT, screen, waitFor } from './test-utils'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ProjectProvider } from '../src/entities/project/model/ProjectContext'
import { SessionPage } from '../src/pages/session/ui/SessionPage'
import { GenericSessionPage } from '../src/pages/session/ui/GenericSessionPage'
import { useMswServer } from './support/msw'
import { setScopedValue } from './support/scoped-property'
import { render, cleanup } from '@testing-library/react'
interface MockSession {
  issue: Record<string, unknown> | null
  sessions: any[]
  metadata: Record<string, unknown> | null
  turns: any[]
  pending: boolean
}

const mocks: MockSession = {
  issue: null,
  sessions: [],
  metadata: null,
  turns: [],
  pending: false,
}

const queryClients: QueryClient[] = []
const metadataCalls: string[] = []
const transcriptCalls: string[] = []
let sessionListResponses: any[][] | null = null

function createQueryClient() {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  })
  queryClients.push(queryClient)
  return queryClient
}

useMswServer(
  http.get('*/api/projects/:projectId/issues/:issueNumber/coder-sessions', () => {
    if (mocks.pending) return new Promise<never>(() => {})
    return HttpResponse.json({ success: true, data: sessionListResponses?.shift() ?? mocks.sessions })
  }),
  http.get('*/api/projects/:projectId/issues/:issueNumber/sessions/:sessionName/transcript', ({ params }) => {
    transcriptCalls.push(String(params.sessionName))
    if (mocks.pending) return new Promise<never>(() => {})
    return HttpResponse.json({
      success: true,
      data: {
        turns: mocks.turns,
        partCount: mocks.turns.reduce((total, t) => total + (t.assistant?.length ?? 0), 0),
        lastActivityAt: mocks.turns.at(-1)?.completedAt ?? mocks.turns.at(-1)?.startedAt ?? null,
      },
    })
  }),
  http.get('*/api/projects/:projectId/issues/:issueNumber/sessions/:sessionName', ({ params }) => {
    metadataCalls.push(String(params.sessionName))
    if (mocks.pending) return new Promise<never>(() => {})
    return HttpResponse.json({ success: true, data: mocks.metadata })
  }),
  http.get('*/api/projects/:projectId/issues/:issueNumber', () =>
    HttpResponse.json({ success: true, data: mocks.issue })),
  http.get('*/api/workflow-runs/:workflowRunId/sessions', () =>
    HttpResponse.json({ success: true, data: [] })),
  http.get('*/api/projects/:projectId/agent-sessions/:sessionId', () => {
      const summary: Record<string, unknown> = mocks.metadata
      ? {
          sessionId: (mocks.metadata.sessionName as string | null) ?? (mocks.metadata.id as string | null) ?? 'sess-1',
          agentId: (mocks.metadata.sessionName as string | null) ?? 'agent-1',
          agentName: (mocks.metadata.sessionName as string | null) ?? 'Agent',
          status: (mocks.metadata.status as string) ?? 'completed',
          createdAt: (mocks.metadata.createdAt as string) ?? '2026-06-15T10:00:00.000Z',
          lastActivityAt: (mocks.metadata.lastActivityAt as string | null) ?? null,
          resolvedModel: (mocks.metadata.model as string | null) ?? null,
          failureCategory: ((mocks.metadata.eventSummary as Record<string, unknown> | undefined)?.failureCategory as string | null) ?? null,
          toolCallCount: ((mocks.metadata.eventSummary as Record<string, unknown> | undefined)?.toolCallCount as number | null) ?? null,
          toolErrorCount: ((mocks.metadata.eventSummary as Record<string, unknown> | undefined)?.toolErrorCount as number | null) ?? null,
          contextRefs: null,
          usage: (mocks.metadata.usage as Record<string, unknown> | null) ?? null,
        }
      : {
          sessionId: 'sess-1',
          agentId: 'agent-1',
          agentName: 'Test Agent',
          status: 'completed',
          createdAt: '2026-06-15T10:00:00.000Z',
          lastActivityAt: '2026-06-15T10:30:00.000Z',
          resolvedModel: 'gpt-4',
          failureCategory: null,
          toolCallCount: 0,
          toolErrorCount: 0,
          contextRefs: null,
          usage: null,
        }
    return HttpResponse.json({ success: true, data: summary })
  }),
  http.get('*/api/projects/:projectId/agent-sessions/:sessionId/transcript', () =>
    HttpResponse.json({ success: true, data: { turns: mocks.turns, partCount: 0, lastActivityAt: null } })),
)

function renderPage(initialEntry: string) {
  const queryClient = createQueryClient()
  setScopedValue(Element.prototype, 'scrollTo', vi.fn())
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
        <MemoryRouter initialEntries={[initialEntry]}>
          <Routes>
            <Route path="/issues/:number/session/:sessionId" element={<SessionPage />} />
            <Route path="/issues/:number/workflow/sessions/:sessionName" element={<SessionPage />} />
            <Route path="/agent-sessions/:sessionId" element={<GenericSessionPage />} />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

function baseCompletedMetadata(overrides: Record<string, unknown> = {}) {
  return {
    id: 'agent-session-1',
    sessionName: 'build',
    acpSessionId: 'acp-1',
    title: 'Build session',
    status: 'completed',
    statusKind: 'completed',
    stage: 'build',
    model: 'minimax/MiniMax-M3',
    createdAt: '2026-06-15T10:00:00.000Z',
    completedAt: '2026-06-15T11:00:00.000Z',
    lastActivityAt: '2026-06-15T10:30:00.000Z',
    lastDataAt: '2026-06-15T10:30:00.000Z',
    changedFiles: [],
    metadata: { partCount: 2, toolCount: 1 },
    usage: {
      inputTokens: 1000,
      outputTokens: 500,
      totalTokens: 1500,
      costAmount: 0.01,
      costCurrency: 'USD',
      contextWindowUsed: 12000,
      contextWindowSize: 32000,
      contextUsagePercent: 37.5,
      healthStatus: 'green',
    },
    eventSummary: {
      resolvedModel: 'minimax/MiniMax-M3',
      toolCallCount: 1,
      toolErrorCount: 0,
    },
    ...overrides,
  }
}

function baseTurn(overrides: Record<string, unknown> = {}) {
  return {
    id: 'turn-1',
    index: 0,
    kind: 'prompt',
    role: 'user',
    content: { text: 'Build it' },
    parts: [],
    startedAt: '2026-06-15T10:00:00.000Z',
    completedAt: '2026-06-15T10:00:01.000Z',
    user: { role: 'mohist', text: 'Build it', kind: 'task', sentAt: '2026-06-15T10:00:00.000Z' },
    assistant: [],
    ...overrides,
  }
}

function setupDefaultMocks() {
  mocks.issue = {
    id: '123',
    number: 123,
    title: 'Test issue',
    body: 'Body',
    status: 'in_progress',
    stage: 'build',
    labels: {},
    createdAt: '2026-06-15T10:00:00.000Z',
    updatedAt: '2026-06-15T10:30:00.000Z',
    projectId: 'test-project',
    workflowRunId: 'wr-1',
  }
  mocks.sessions = [
    {
      id: 'agent-session-1',
      sessionName: 'build',
      workflowRunId: 'wr-1',
      acpSessionId: 'acp-1',
      projectId: 'test-project',
      issueNumber: 123,
      runnerId: 'runner-1',
      status: 'completed',
      stage: 'build',
      model: 'minimax/MiniMax-M3',
      workDir: null,
      processPid: null,
      createdAt: '2026-06-15T10:00:00.000Z',
      startedAt: '2026-06-15T10:00:05.000Z',
      completedAt: '2026-06-15T11:00:00.000Z',
      lastDataAt: '2026-06-15T10:30:00.000Z',
      failureReason: null,
      exitCode: 0,
    },
  ]
  mocks.metadata = baseCompletedMetadata()
  mocks.turns = [baseTurn(), { ...baseTurn(), id: 'turn-2', index: 1, kind: 'response', role: 'assistant', content: { text: 'Done' } }]
  mocks.pending = false
}

beforeEach(() => {
  setupDefaultMocks()
  metadataCalls.length = 0
  transcriptCalls.length = 0
  sessionListResponses = null
  setScopedValue(Element.prototype, 'scrollTo', vi.fn())
})

afterEach(() => {
  cleanup()
  for (const q of queryClients) q.clear()
  queryClients.length = 0
})
describe('Coder Session evidence view — region contract', () => {
  it('renders the task-identity and current-status region above the transcript scroll container', async () => {
    const { container } = renderPage('/issues/123/workflow/sessions/build')

    await waitFor(() => {
      if (!container.querySelector('[data-testid="session-transcript-scroll-container"]')) {
        throw new Error('not ready yet')
      }
    })

    const header = container.querySelector('[data-testid="session-header"]')
    const scrollContainer = container.querySelector('[data-testid="session-transcript-scroll-container"]')
    expect(header).not.toBeNull()
    expect(scrollContainer).not.toBeNull()
    expect(header!.compareDocumentPosition(scrollContainer!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  })

  it('omits the errors region when no failure evidence is present', async () => {
    const { container } = renderPage('/issues/123/workflow/sessions/build')

    await waitFor(() => {
      if (!container.querySelector('[data-testid="session-transcript-scroll-container"]')) {
        throw new Error('not ready yet')
      }
    })

    expect(container.querySelector('[data-testid="session-errors-region"]')).toBeNull()
  })

  it('renders the errors region when statusKind is failed and exposes data-failure-category / data-tool-error-count', async () => {
    mocks.metadata = baseCompletedMetadata({
      status: 'failed',
      statusKind: 'failed',
      eventSummary: {
        resolvedModel: 'minimax/MiniMax-M3',
        toolCallCount: 3,
        toolErrorCount: 2,
        failureCategory: 'context_limit',
      },
    })

    const { container } = renderPage('/issues/123/workflow/sessions/build')

    await waitFor(() => {
      if (!container.querySelector('[data-testid="session-errors-region"]')) {
        throw new Error('errors region not rendered yet')
      }
    })

    const errorsRegion = container.querySelector('[data-testid="session-errors-region"]')
    expect(errorsRegion).not.toBeNull()
    expect(errorsRegion!.getAttribute('data-failure-category')).toBe('context_limit')
    expect(errorsRegion!.getAttribute('data-tool-error-count')).toBe('2')
    expect(errorsRegion!.textContent).toContain('context_limit')
    expect(errorsRegion!.textContent).toContain('2')
  })

  it('renders the errors region when toolErrorCount > 0 even if statusKind is not failed', async () => {
    mocks.metadata = baseCompletedMetadata({
      status: 'completed',
      statusKind: 'completed',
      eventSummary: {
        resolvedModel: 'minimax/MiniMax-M3',
        toolCallCount: 2,
        toolErrorCount: 1,
        failureCategory: null,
      },
    })

    const { container } = renderPage('/issues/123/workflow/sessions/build')

    await waitFor(() => {
      if (!container.querySelector('[data-testid="session-errors-region"]')) {
        throw new Error('errors region not rendered yet')
      }
    })

    const errorsRegion = container.querySelector('[data-testid="session-errors-region"]')
    expect(errorsRegion!.getAttribute('data-failure-category')).toBe('')
    expect(errorsRegion!.getAttribute('data-tool-error-count')).toBe('1')
  })

  it('renders the errors region when a failure category is recorded on a non-failed session', async () => {
    mocks.metadata = baseCompletedMetadata({
      status: 'completed',
      statusKind: 'completed',
      eventSummary: {
        resolvedModel: 'minimax/MiniMax-M3',
        toolCallCount: 2,
        toolErrorCount: 0,
        failureCategory: 'compaction',
      },
    })

    const { container } = renderPage('/issues/123/workflow/sessions/build')

    await waitFor(() => {
      if (!container.querySelector('[data-testid="session-errors-region"]')) {
        throw new Error('errors region not rendered yet')
      }
    })

    const errorsRegion = container.querySelector('[data-testid="session-errors-region"]')
    expect(errorsRegion!.getAttribute('data-failure-category')).toBe('compaction')
    expect(errorsRegion!.getAttribute('data-tool-error-count')).toBe('0')
  })

  it('places the errors region between usage summary and transcript scroll container', async () => {
    mocks.metadata = baseCompletedMetadata({
      status: 'failed',
      statusKind: 'failed',
      eventSummary: {
        resolvedModel: 'minimax/MiniMax-M3',
        toolCallCount: 1,
        toolErrorCount: 1,
        failureCategory: 'timeout',
      },
    })

    const { container } = renderPage('/issues/123/workflow/sessions/build')

    await waitFor(() => {
      if (!container.querySelector('[data-testid="session-transcript-scroll-container"]')) {
        throw new Error('scroll container not rendered yet')
      }
    })

    const usage = container.querySelector('[data-testid="session-usage-summary"]')
    const errorsRegion = container.querySelector('[data-testid="session-errors-region"]')
    const scrollContainer = container.querySelector('[data-testid="session-transcript-scroll-container"]')
    expect(usage).not.toBeNull()
    expect(errorsRegion).not.toBeNull()
    expect(scrollContainer).not.toBeNull()

    expect(usage!.compareDocumentPosition(errorsRegion!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    expect(errorsRegion!.compareDocumentPosition(scrollContainer!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  })

  it('renders failureReason only when a non-null value is available', async () => {
    mocks.metadata = baseCompletedMetadata({
      status: 'failed',
      statusKind: 'failed',
      eventSummary: {
        resolvedModel: 'minimax/MiniMax-M3',
        toolCallCount: 1,
        toolErrorCount: 1,
        failureCategory: 'context_limit',
      },
      failureReason: 'context window exceeded',
    })

    const { container } = renderPage('/issues/123/workflow/sessions/build')

    await waitFor(() => {
      if (!container.querySelector('[data-testid="session-errors-region-reason"]')) {
        throw new Error('failure reason not rendered yet')
      }
    })

    expect(screen.getByTestId('session-errors-region-reason').textContent).toContain('context window exceeded')
  })

  it('omits the failure-reason span when failureReason is null', async () => {
    mocks.metadata = baseCompletedMetadata({
      status: 'failed',
      statusKind: 'failed',
      eventSummary: {
        resolvedModel: 'minimax/MiniMax-M3',
        toolCallCount: 1,
        toolErrorCount: 1,
        failureCategory: 'context_limit',
      },
      failureReason: null,
    })

    const { container } = renderPage('/issues/123/workflow/sessions/build')

    await waitFor(() => {
      if (!container.querySelector('[data-testid="session-errors-region"]')) {
        throw new Error('errors region not rendered yet')
      }
    })

    expect(container.querySelector('[data-testid="session-errors-region-reason"]')).toBeNull()
  })

  it('omits the usage summary when no usage fields are present and keeps the remaining region order', async () => {
    mocks.metadata = baseCompletedMetadata({ usage: null })

    const { container } = renderPage('/issues/123/workflow/sessions/build')

    await waitFor(() => {
      if (!container.querySelector('[data-testid="session-transcript-scroll-container"]')) {
        throw new Error('not ready yet')
      }
    })

    const header = container.querySelector('[data-testid="session-header"]')
    const usage = container.querySelector('[data-testid="session-usage-summary"]')
    const errorsRegion = container.querySelector('[data-testid="session-errors-region"]')
    const scrollContainer = container.querySelector('[data-testid="session-transcript-scroll-container"]')

    expect(header).not.toBeNull()
    expect(usage).toBeNull()
    expect(errorsRegion).toBeNull()
    expect(scrollContainer).not.toBeNull()

    expect(header!.compareDocumentPosition(scrollContainer!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  })

  it('keeps the errors region above the transcript even when no usage summary is present', async () => {
    mocks.metadata = baseCompletedMetadata({
      usage: null,
      status: 'failed',
      statusKind: 'failed',
      eventSummary: {
        resolvedModel: 'minimax/MiniMax-M3',
        toolCallCount: 1,
        toolErrorCount: 1,
        failureCategory: 'timeout',
      },
    })
    mocks.turns = [
      baseTurn(),
      {
        ...baseTurn(),
        id: 'turn-2',
        index: 1,
        kind: 'response',
        role: 'assistant',
        content: { text: 'attempt' },
        user: { role: 'mohist', text: 'attempt', kind: 'task', sentAt: '2026-06-15T10:00:00.000Z' },
        assistant: [],
      },
    ]

    const { container } = renderPage('/issues/123/workflow/sessions/build')

    await waitFor(() => {
      const errorsRegion = container.querySelector('[data-testid="session-errors-region"]')
      const scrollContainer = container.querySelector('[data-testid="session-transcript-scroll-container"]')
      if (!errorsRegion || !scrollContainer) throw new Error('not ready yet')
    })

    const errorsRegion = container.querySelector('[data-testid="session-errors-region"]')
    const scrollContainer = container.querySelector('[data-testid="session-transcript-scroll-container"]')
    expect(errorsRegion).not.toBeNull()
    expect(scrollContainer).not.toBeNull()
    expect(errorsRegion!.compareDocumentPosition(scrollContainer!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  })
})

describe('Coder Session evidence view — navigation entry points', () => {
  it('exposes a project-scoped back link to the issue for issue-bound sessions', async () => {
    const { container } = renderPage('/issues/123/workflow/sessions/build')

    await waitFor(() => {
      if (!container.querySelector('[data-testid="session-back-link"]')) {
        throw new Error('back link not rendered yet')
      }
    })

    expect(screen.getByTestId('session-back-link').getAttribute('href')).toBe('/Test%20Project/issues/123')
  })

  it('exposes a workflow-context entry point for issue-bound sessions', async () => {
    const { container } = renderPage('/issues/123/workflow/sessions/build')

    await waitFor(() => {
      if (!container.querySelector('[data-testid="session-workflow-context-link"]')) {
        throw new Error('workflow context link not rendered yet')
      }
    })

    expect(screen.getByTestId('session-workflow-context-link').getAttribute('href')).toBe('/Test%20Project/issues/123')
  })

  it('returns to the project-scoped Activity page when ?from=activity is present (issue-bound)', async () => {
    const { container } = renderPage('/issues/123/workflow/sessions/build?from=activity')

    await waitFor(() => {
      if (!container.querySelector('[data-testid="session-back-link"]')) {
        throw new Error('back link not rendered yet')
      }
    })

    const backLink = screen.getByTestId('session-back-link')
    expect(backLink.getAttribute('href')).toBe('/Test%20Project/activity')
    expect(backLink.textContent).toContain('Activity')
  })

  it('returns to the project-scoped Activity page for generic sessions with ?from=activity', async () => {
    mocks.metadata = null
    mocks.sessions = []

    const { container } = renderPage('/agent-sessions/sess-1?from=activity')

    await waitFor(() => {
      if (!container.querySelector('[data-testid="session-back-link"]')) {
        throw new Error('back link not rendered yet')
      }
    })

    expect(screen.getByTestId('session-back-link').getAttribute('href')).toBe('/Test%20Project/activity')
  })

  it('does not fabricate a workflow-context link for generic sessions without an issue binding', async () => {
    mocks.metadata = null
    mocks.sessions = []

    const { container } = renderPage('/agent-sessions/sess-1')

    await waitFor(() => {
      if (!container.querySelector('[data-testid="session-back-link"]')) {
        throw new Error('back link not rendered yet')
      }
    })

    expect(container.querySelector('[data-testid="session-workflow-context-link"]')).toBeNull()
  })

  it('preserves the legacy back-link behavior (no from=) for issue-bound sessions', async () => {
    const { container } = renderPage('/issues/123/workflow/sessions/build')

    await waitFor(() => {
      if (!container.querySelector('[data-testid="session-back-link"]')) {
        throw new Error('back link not rendered yet')
      }
    })

    const backLink = screen.getByTestId('session-back-link')
    expect(backLink.textContent).toContain('Issue #123')
  })
})

describe('Coder Session evidence view — issue-session ID resolution', () => {
  it('resolves the legacy /issues/:number/session/:sessionId route to the canonical session name before detail queries', async () => {
    mocks.sessions = [
      {
        id: 'legacy-id-1',
        sessionName: 'build',
        workflowRunId: 'wr-1',
        acpSessionId: 'acp-1',
        projectId: 'test-project',
        issueNumber: 123,
        runnerId: 'runner-1',
        status: 'completed',
        stage: 'build',
        model: 'minimax/MiniMax-M3',
        workDir: null,
        processPid: null,
        createdAt: '2026-06-15T10:00:00.000Z',
        startedAt: '2026-06-15T10:00:05.000Z',
        completedAt: '2026-06-15T11:00:00.000Z',
        lastDataAt: '2026-06-15T10:30:00.000Z',
        failureReason: null,
        exitCode: 0,
      },
    ]

    renderPage('/issues/123/session/legacy-id-1')

    await waitFor(() => {
      if (metadataCalls.length === 0) throw new Error('metadata not fetched yet')
    })

    // The legacy route must resolve to the canonical sessionName before any
    // detail query is fired. Allow a small grace window for a stale legacy call
    // to settle, but the last call must use the canonical name.
    expect(metadataCalls[metadataCalls.length - 1]).toBe('build')
    expect(transcriptCalls[transcriptCalls.length - 1] ?? '').toBe('build')
  })

  it('refreshes a fresh cached list before resolving a newly-created Activity session ID', async () => {
    mocks.metadata = baseCompletedMetadata({ id: 'new-session-id', sessionName: 'build' })
    sessionListResponses = [
      [],
      [
        {
          id: 'new-session-id',
          sessionName: 'build',
          workflowRunId: 'wr-1',
          acpSessionId: 'acp-1',
          projectId: 'test-project',
          issueNumber: 123,
          runnerId: 'runner-1',
          status: 'completed',
          stage: 'build',
          model: 'minimax/MiniMax-M3',
          workDir: null,
          processPid: null,
          createdAt: '2026-06-15T10:00:00.000Z',
          startedAt: '2026-06-15T10:00:05.000Z',
          completedAt: '2026-06-15T11:00:00.000Z',
          lastDataAt: '2026-06-15T10:30:00.000Z',
          failureReason: null,
          exitCode: 0,
        },
      ],
    ]

    renderPage('/issues/123/session/new-session-id?from=activity')

    await waitFor(() => {
      expect(metadataCalls).toContain('build')
    })
    expect(metadataCalls).not.toContain('new-session-id')
    expect(transcriptCalls).not.toContain('new-session-id')
  })
})

describe('Coder Session evidence view — shared theme tokens', () => {
  it('uses the shared danger token families for status surfaces in light mode', async () => {
    mocks.metadata = baseCompletedMetadata({
      status: 'failed',
      statusKind: 'failed',
      eventSummary: {
        resolvedModel: 'minimax/MiniMax-M3',
        toolCallCount: 1,
        toolErrorCount: 1,
        failureCategory: 'context_limit',
      },
    })

    const { container } = renderPage('/issues/123/workflow/sessions/build')

    await waitFor(() => {
      if (!container.querySelector('[data-testid="session-status-badge"]')) {
        throw new Error('status badge not rendered yet')
      }
    })

    const badge = container.querySelector('[data-testid="session-status-badge"]')
    expect(badge!.className).toContain('bg-danger-subtle')
    expect(badge!.className).toContain('text-danger')
    expect(badge!.className).toContain('border-danger-border')

    const errorsRegion = container.querySelector('[data-testid="session-errors-region"]')
    expect(errorsRegion!.className).toContain('bg-danger-subtle')
    expect(errorsRegion!.className).toContain('text-danger')
  })

  it('uses the shared success token families for completed status surfaces in light mode', async () => {
    mocks.metadata = baseCompletedMetadata({
      status: 'completed',
      statusKind: 'completed',
      eventSummary: {
        resolvedModel: 'minimax/MiniMax-M3',
        toolCallCount: 1,
        toolErrorCount: 0,
      },
    })

    const { container } = renderPage('/issues/123/workflow/sessions/build')

    await waitFor(() => {
      if (!container.querySelector('[data-testid="session-status-badge"]')) {
        throw new Error('status badge not rendered yet')
      }
    })

    const badge = container.querySelector('[data-testid="session-status-badge"]')
    expect(badge!.className).toContain('bg-success-subtle')
    expect(badge!.className).toContain('text-success')
  })

  it('uses the shared info token families for live status surfaces in dark mode', async () => {
    document.documentElement.classList.add('dark')
    try {
      mocks.metadata = baseCompletedMetadata({
        status: 'active',
        statusKind: 'live',
        completedAt: null,
      })

      const { container } = renderPage('/issues/123/workflow/sessions/build')

      await waitFor(() => {
        if (!container.querySelector('[data-testid="session-status-badge"]')) {
          throw new Error('status badge not rendered yet')
        }
      })

      const badge = container.querySelector('[data-testid="session-status-badge"]')
      expect(badge!.className).toContain('bg-info-subtle')
      expect(badge!.className).toContain('text-info')
    } finally {
      document.documentElement.classList.remove('dark')
    }
  })

  it.each([
    ['stale', 'warning'],
    ['probing', 'info'],
    ['finalizing', 'warning'],
  ])('maps statusKind=%s status badge to the shared %s token family', async (statusKind, expectedTone) => {
    document.documentElement.classList.add('dark')
    try {
      mocks.metadata = baseCompletedMetadata({
        status: statusKind,
        statusKind,
        completedAt: null,
      })

      const { container } = renderPage('/issues/123/workflow/sessions/build')

      await waitFor(() => {
        if (!container.querySelector('[data-testid="session-status-badge"]')) {
          throw new Error('status badge not rendered yet')
        }
      })

      const badge = container.querySelector('[data-testid="session-status-badge"]')
      const tone = badge?.getAttribute('data-tone')
      expect(tone).toBe(expectedTone)
      expect(badge!.className).toContain(`bg-${expectedTone}-subtle`)
      expect(badge!.className).toContain(`text-${expectedTone}`)
      expect(badge!.className).toContain(`border-${expectedTone}-border`)
    } finally {
      document.documentElement.classList.remove('dark')
    }
  })
})

describe('Coder Session evidence view — action preservation', () => {
  it('keeps the follow-up composer anchor for issue-bound sessions', async () => {
    mocks.metadata = baseCompletedMetadata({
      status: 'active',
      statusKind: 'live',
      completedAt: null,
    })
    mocks.sessions = [
      {
        ...mocks.sessions[0],
        status: 'active',
        completedAt: null,
      },
    ]

    const { container } = renderPage('/issues/123/workflow/sessions/build')

    await waitFor(() => {
      if (!container.querySelector('[data-testid="session-transcript-scroll-container"]')) {
        throw new Error('not ready yet')
      }
    })

    expect(container.querySelector('[data-testid="session-followup-composer"]')).not.toBeNull()
  })

  it('keeps the recovery-bar inside the transcript scroll container', async () => {
    mocks.metadata = baseCompletedMetadata({
      usage: {
        ...baseCompletedMetadata().usage as Record<string, unknown>,
        contextUsagePercent: 85,
        healthStatus: 'red',
      },
    })

    const { container } = renderPage('/issues/123/workflow/sessions/build')

    await waitFor(() => {
      if (!container.querySelector('[data-testid="session-transcript-scroll-container"]')) {
        throw new Error('not ready yet')
      }
    })

    const scrollContainer = container.querySelector('[data-testid="session-transcript-scroll-container"]')
    const recoveryBar = scrollContainer!.querySelector('[data-testid="session-recovery-bar"]')
    expect(recoveryBar).not.toBeNull()
  })

  it('keeps the sticky title inside the transcript scroll container', async () => {
    const { container } = renderPage('/issues/123/workflow/sessions/build')

    await waitFor(() => {
      if (!container.querySelector('[data-testid="session-sticky-title"]')) {
        throw new Error('not ready yet')
      }
    })

    const scrollContainer = container.querySelector('[data-testid="session-transcript-scroll-container"]')
    const stickyTitle = scrollContainer!.querySelector('[data-testid="session-sticky-title"]')
    expect(stickyTitle).not.toBeNull()
  })
})

describe('Coder Session evidence view — failure category accent', () => {
  it('renders failure category chip with shared danger tokens', async () => {
    mocks.metadata = baseCompletedMetadata({
      eventSummary: {
        resolvedModel: 'minimax/MiniMax-M3',
        toolCallCount: 1,
        toolErrorCount: 0,
        failureCategory: 'context_limit',
      },
    })

    const { container } = renderPage('/issues/123/workflow/sessions/build')
    await waitFor(() => {
      if (!container.querySelector('[data-testid="session-errors-region-category"]')) {
        throw new Error('failure category chip not rendered yet')
      }
    })

    const chip = container.querySelector('[data-testid="session-errors-region-category"]')
    expect(chip!.className).toContain('bg-danger-subtle')
    expect(chip!.className).toContain('text-danger')
  })
})
