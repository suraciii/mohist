import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, render, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Routes, Route } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { SessionPage, type SessionPageDependencies } from './SessionPage'
import type { AgentSessionMetadata } from '../../../entities/coder-session'

let _issueData: unknown = null
let _coderSessionsData: unknown[] = []
let _metadataData: unknown = null
let _transcriptData: { turns: unknown[]; partCount: number; lastActivityAt: string } = {
  turns: [
    { id: 'turn-1', index: 0, kind: 'prompt', role: 'user', content: { text: 'Build it' }, parts: [] },
    { id: 'turn-2', index: 1, kind: 'response', role: 'assistant', content: { text: 'Done' }, parts: [] },
  ],
  partCount: 2,
  lastActivityAt: '2026-06-15T10:29:55.000Z',
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
const cancelMutate = vi.fn()

const sessionPageDependencies: SessionPageDependencies = {
  dataSource: {
    useSessionTranscript: () => mocks.transcriptReturn as never,
    projectTurn: (turn) => turn as never,
    useIssue: () => ({ data: _issueData }) as never,
    useCoderSessions: () => ({ sessions: _coderSessionsData, isLoading: false }) as never,
    useSiblingSessions: () => ({
      sessions: [],
      currentIndex: -1,
      previous: null,
      next: null,
      hasPrevious: false,
      hasNext: false,
    }),
    getAgentSessionMetadata: async () => _metadataData as never,
    getAgentSessionTranscript: async () => _transcriptData as never,
    useFollowupMutation: () => ({ mutateAsync: vi.fn(async () => ({ status: 'sent' })), isPending: false }) as never,
    useCancelSessionMutation: () => ({ mutate: cancelMutate, isPending: false }) as never,
  },
  shellComponents: {
    SessionTranscriptLayout: ({ turns }: { turns: any[] }) => (
      <div data-testid="session-transcript-layout">{turns.length} turns</div>
    ),
    SessionRecoveryActions: ({ bare }: { bare?: boolean }) => (
      <div data-testid="session-recovery-actions" data-bare={bare ? 'true' : 'false'} />
    ),
    SessionFollowupComposer: ({ disabled }: { disabled?: boolean }) => (
      <div data-testid="session-followup-composer" data-disabled={disabled ? 'true' : 'false'} />
    ),
    ContextHealthBar: () => <div data-testid="context-health-bar" />,
    CompactionLineageLink: () => <div data-testid="compaction-lineage-link" />,
  },
}


const queryClients: QueryClient[] = []

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

function setupRunningIssueMocks() {
  _issueData = {
    id: '123',
    number: 123,
    title: 'Test issue',
    body: 'Body',
    status: 'in_progress',
    stage: 'build',
    labels: {},
    createdAt: '2026-06-15T10:00:00.000Z',
    updatedAt: '2026-06-15T10:30:00.000Z',
    projectId: 'proj-1',
    workflowRunId: 'wr-1',
  }
  _coderSessionsData = [
    {
      id: 'session-1',
      sessionName: 'session-1',
      workflowRunId: 'wr-1',
      runtimeSessionId: 'runtime-1',
      runtime: 'opencode',
      projectId: 'proj-1',
      issueNumber: 123,
      runnerId: 'runner-1',
      status: 'active',
      stage: 'build',
      model: 'minimax/MiniMax-M3',
      workDir: null,
      processPid: null,
      createdAt: '2026-06-15T10:00:00.000Z',
      startedAt: '2026-06-15T10:00:05.000Z',
      completedAt: null,
      lastDataAt: '2026-06-15T10:00:30.000Z',
      failureReason: null,
      exitCode: 0,
    },
  ]
  mocks.transcriptReturn = {
    turns: [
      { id: 'turn-1', index: 0, kind: 'prompt', role: 'user', content: { text: 'Build it' } },
      { id: 'turn-2', index: 1, kind: 'response', role: 'assistant', content: { text: 'Done' } },
    ],
    transcriptVersion: 0,
    scrollToBottom: vi.fn(),
    newContentAvailable: false,
    setIsNearBottom: vi.fn(),
    isFinalizing: false,
    isThinking: false,
    isStreaming: false,
  }
}

async function renderIssueSessionPage(initialEntry = '/issues/123/workflow/sessions/session-1') {
  const queryClient = createQueryClient()
  const result = render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj-1" initialProjects={[{
        id: 'proj-1',
        name: 'Test',
        createdAt: '2026-01-01T00:00:00Z',
        updatedAt: '2026-01-01T00:00:00Z',
        repositories: [],
      }]}>
        <MemoryRouter initialEntries={[initialEntry]}>
          <Routes>
            <Route
              path="/issues/:number/workflow/sessions/:sessionName"
              element={<SessionPage dependencies={sessionPageDependencies} />}
            />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
  await waitFor(() => {
    if (!result.container.querySelector('[data-testid="session-transcript-scroll-container"]')) {
      throw new Error('not ready yet')
    }
  })
  return result
}

function baseRunningMetadata(overrides: Partial<AgentSessionMetadata> = {}): AgentSessionMetadata {
  return {
    id: 'agent-session-1',
    sessionName: 'session-1',
    runtimeSessionId: 'runtime-1',
    runtime: 'opencode',
    title: 'Test session',
    status: 'active',
    statusKind: 'live',
    // Issue 484: the page drives isRunning from activity ('active'),
    // not from the legacy status/statusKind fields.
    activity: 'active',
    stage: 'build',
    model: 'minimax/MiniMax-M3',
    createdAt: '2026-06-15T10:00:00.000Z',
    completedAt: null,
    lastActivityAt: '2026-06-15T10:00:30.000Z',
    lastDataAt: '2026-06-15T10:00:30.000Z',
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
    },
    eventSummary: {
      resolvedModel: 'minimax/MiniMax-M3',
      toolCallCount: 1,
      toolErrorCount: 0,
    },
    ...overrides,
  }
}

describe('SessionPage workflow cancel control', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    setupRunningIssueMocks()
    _metadataData = baseRunningMetadata()
    cancelMutate.mockClear()
  })

  afterEach(() => {
    cleanup()
    for (const queryClient of queryClients) queryClient.clear()
    queryClients.length = 0
  })

  it('renders a secondary cancel action outside the metadata row', async () => {
    const { container } = await renderIssueSessionPage()

    const trigger = container.querySelector<HTMLButtonElement>('[data-testid="session-cancel-trigger"]')!
    const metadataRow = container.querySelector('[data-testid="session-header-metadata-row"]')!
    const secondaryActions = container.querySelector('[data-testid="session-header-secondary-actions"]')!
    const triggerClasses = trigger.className.split(/\s+/)

    expect(trigger).toHaveAttribute('aria-label', 'Cancel Turn')
    expect(trigger).toHaveTextContent('Cancel Turn')
    expect(container.querySelector('[data-testid="session-stop-trigger"]')).toBeInTheDocument()
    expect(triggerClasses).toContain('hover:bg-muted')
    expect(triggerClasses).not.toContain('bg-destructive/10')
    expect(triggerClasses).not.toContain('text-destructive')
    expect(triggerClasses.some((className) => className.startsWith('bg-danger'))).toBe(false)
    expect(metadataRow).not.toContainElement(trigger)
    expect(secondaryActions).toContainElement(trigger)
    expect(secondaryActions).toHaveClass('flex', 'justify-end')
    expect(secondaryActions.previousElementSibling).toBe(metadataRow)
    expect(container.querySelector('[data-testid="session-cancel-alert"]')).toBeNull()
  })

  it('keeps cancellation Tab-reachable and confirms through the workflow session name', async () => {
    const user = userEvent.setup()
    const { container } = await renderIssueSessionPage()
    const trigger = container.querySelector<HTMLButtonElement>('[data-testid="session-cancel-trigger"]')!
    const tabbableElements = Array.from(
      container.querySelectorAll<HTMLElement>('a[href], button:not([disabled]), [tabindex="0"]'),
    )
    const triggerIndex = tabbableElements.indexOf(trigger)

    expect(triggerIndex).toBeGreaterThan(0)
    tabbableElements[triggerIndex - 1]!.focus()
    await user.tab()
    expect(trigger).toHaveFocus()

    await user.keyboard('{Enter}')
    expect(document.querySelector('[data-testid="session-cancel-alert"]')).not.toBeNull()
    await user.click(document.querySelector('[data-testid="session-cancel-alert-confirm"]')!)

    expect(cancelMutate).toHaveBeenCalledWith(
      { issueNumber: 123, sessionName: 'session-1', turnId: 'turn-2', operation: 'cancel' },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    )

    await act(async () => {
      const options = cancelMutate.mock.calls[0]?.[1] as { onSuccess: (result: { state: string }) => void }
       options.onSuccess({ state: 'cancelled' })
    })
    await waitFor(() => {
      expect(container.querySelector('[data-testid="session-cancel-result"]')).toHaveTextContent('cancelled')
    })
  })

  it('targets the current Turn when requesting a stop and exposes the unknown verification entry', async () => {
    const user = userEvent.setup()
    const { container } = await renderIssueSessionPage()

    await user.click(container.querySelector<HTMLButtonElement>('[data-testid="session-stop-trigger"]')!)
    await user.click(document.querySelector('[data-testid="session-cancel-alert-confirm"]')!)

    expect(cancelMutate).toHaveBeenCalledWith(
      { issueNumber: 123, sessionName: 'session-1', turnId: 'turn-2', operation: 'stop' },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    )

    await act(async () => {
      const options = cancelMutate.mock.calls[0]?.[1] as { onSuccess: (result: { state: string }) => void }
      options.onSuccess({ state: 'unknown' })
    })
    await waitFor(() => {
      expect(container.querySelector('[data-testid="session-cancel-result"]')).toHaveTextContent('unknown')
      expect(container.querySelector('[data-testid="session-cancel-result"]')).toHaveTextContent('Verification: Session view')
    })
  })

  it('hides cancellation when the workflow session has no physical runtime binding', async () => {
    _metadataData = baseRunningMetadata({ runtimeSessionId: null })
    _coderSessionsData = _coderSessionsData.map((session) => ({ ...(session as object), runtimeSessionId: null }))

    const { container } = await renderIssueSessionPage()

    expect(container.querySelector('[data-testid="session-cancel-trigger"]')).toBeNull()
  })

  it('disables followup while retaining recovery actions when the runtime backend is absent', async () => {
    _metadataData = baseRunningMetadata({ runtime: null })

    const { container } = await renderIssueSessionPage()

    await waitFor(() => {
      expect(container.querySelector('[data-testid="session-followup-composer"]')).toHaveAttribute('data-disabled', 'true')
    })
    expect(container.querySelector('[data-testid="session-recovery-actions"]')).not.toBeNull()
  })

  // Issue 484: the historical runtime `?rt=` selector was removed from the
  // product code (the data source no longer reads `rt` and the shell no
  // longer renders a read-only historical view). This scenario is obsolete
  // under the activity model and has been removed:
  //  - "makes a historical runtime view read-only for followup and cancel"
})
