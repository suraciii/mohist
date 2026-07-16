import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { cleanup, render, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Routes, Route } from 'react-router-dom'
import { ProjectProvider } from '../src/entities/project'
import { SessionPage, type SessionPageDependencies } from '../src/pages/session/ui/SessionPage'
import { SessionDetailShell } from '../src/pages/session/ui/SessionDetailShell'
import type { SessionDataSourceResult } from '../src/pages/session/data/SessionDataSource'
import type {
  AgentSessionMetadata,
  CoderSessionSummary,
} from '../src/entities/coder-session'
import { TEST_PROJECT } from './test-utils'

let _issueData: any = null
let _coderSessionsData: CoderSessionSummary[] = []
let _metadataData: AgentSessionMetadata | null = null

const _now = new Date('2026-06-15T11:00:00.000Z')

const sessionPageDependencies: SessionPageDependencies = {
  dataSource: {
    useSessionTranscript: () => ({
      turns: _metadataData ? [] : [],
      transcriptVersion: 0,
      scrollToBottom: () => {},
      newContentAvailable: false,
      setIsNearBottom: () => {},
      isFinalizing: false,
      isThinking: false,
      isStreaming: false,
    }) as unknown as ReturnType<NonNullable<IssueSessionDataSourceDep['useSessionTranscript']>>,
    projectTurn: ((turn: unknown) => turn) as IssueSessionDataSourceDep['projectTurn'],
    useIssue: () => ({ data: _issueData }) as unknown as ReturnType<NonNullable<IssueSessionDataSourceDep['useIssue']>>,
    useCoderSessions: () => ({
      sessions: _coderSessionsData,
      isLoading: false,
      isFetching: false,
      refetch: async () => ({} as never),
    }) as unknown as ReturnType<NonNullable<IssueSessionDataSourceDep['useCoderSessions']>>,
    useSiblingSessions: () => ({
      sessions: [],
      currentIndex: -1,
      previous: null,
      next: null,
      hasPrevious: false,
      hasNext: false,
    }) as unknown as ReturnType<NonNullable<IssueSessionDataSourceDep['useSiblingSessions']>>,
    getAgentSessionMetadata: (async () => _metadataData) as IssueSessionDataSourceDep['getAgentSessionMetadata'],
    getAgentSessionTranscript: (async () => ({
      turns: [],
      partCount: 0,
      lastActivityAt: null,
    })) as IssueSessionDataSourceDep['getAgentSessionTranscript'],
  },
  shellComponents: {
    SessionTranscriptLayout: ({ turns }: { turns: unknown[] }) => (
      <div data-testid="session-transcript-layout">{(turns as unknown[]).length} turns</div>
    ),
    SessionFollowupComposer: () => (
      <div data-testid="session-followup-composer">composer</div>
    ),
    SessionRecoveryActions: () => (
      <div data-testid="session-recovery-actions">
        <button data-testid="session-recovery-compact" type="button">Compact</button>
        <button data-testid="session-recovery-reset" type="button">Reset</button>
      </div>
    ),
    ContextHealthBar: () => (
      <div data-testid="context-health-bar">health bar</div>
    ),
    CompactionLineageLink: () => null,
  },
}

type IssueSessionDataSourceDep = NonNullable<SessionPageDependencies['dataSource']>

const queryClients: QueryClient[] = []

function createQueryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
}

function makeMockMetadata(overrides: Partial<AgentSessionMetadata> = {}): AgentSessionMetadata {
  return {
    id: 'agent-session-12345678',
    sessionName: 'build',
    runtimeSessionId: 'acp-1',
    status: 'active',
    statusKind: 'live',
    model: 'minimax/MiniMax-M3',
    stage: 'build',
    title: 'Build session',
    createdAt: '2026-06-15T10:00:00.000Z',
    completedAt: null,
    lastActivityAt: '2026-06-15T11:00:00.000Z',
    lastDataAt: '2026-06-15T11:00:00.000Z',
    probeSentAt: null,
    probeDeadlineAt: null,
    failureReason: null,
    turnCount: 2,
    changedFiles: [
      { path: 'src/index.ts', operation: 'modified', additions: 5, deletions: 2 },
    ],
    metadata: { eventCount: 5, toolCount: 2 },
    usage: {
      inputTokens: 1000,
      outputTokens: 500,
      totalTokens: 1500,
      cachedReadTokens: 800,
      thoughtTokens: 200,
      costAmount: 0.01,
      costCurrency: 'USD',
      contextWindowUsed: 12000,
      contextWindowSize: 32000,
      contextUsagePercent: 37,
      healthStatus: 'green',
    },
    eventSummary: {
      resolvedModel: 'minimax/MiniMax-M3',
      failureCategory: null,
      toolCallCount: 1,
      toolErrorCount: 0,
    },
    ...overrides,
  }
}

function makeCancelableSessionData(): SessionDataSourceResult {
  return {
    isLoading: false,
    isError: false,
    notFound: false,
    sessionKey: 'build',
    runtimeSessionId: 'acp-1',
    meta: {
      sessionId: 'agent-session-12345678',
      sessionName: 'build',
      issueId: 'issue-1',
      runtimeSessionId: 'acp-1',
      executionId: null,
      title: 'Build session',
      status: 'active',
      statusKind: 'live',
      model: 'minimax/MiniMax-M3',
      stage: 'build',
      createdAt: '2026-06-15T10:00:00.000Z',
      completedAt: null,
      lastActivityAt: '2026-06-15T11:00:00.000Z',
      lastDataAt: '2026-06-15T11:00:00.000Z',
    },
    transcriptResponse: null,
    initialTurns: [],
    statusKind: 'live',
    isRunning: true,
    followupIsPending: false,
    sendFollowup: async () => {},
    cancel: { mutate: () => {}, isPending: false },
    contextWindowUsed: null,
    contextWindowSize: null,
    contextUsagePercent: null,
    healthStatus: null,
    hasRecoveryActions: false,
    recoverySessionName: null,
    runtimeSessionLineage: null,
    viewedRuntimeSessionId: null,
    buildLineageTargetPath: null,
    metadataQueryKey: [],
    transcriptQueryKey: [],
    handleRecoverySuccess: () => {},
    backPath: '/issues/123',
    backLabel: 'Issue #123',
    siblingNav: null,
    siblingSidebar: null,
    sessionTurns: [],
    transcriptVersion: 0,
    scrollToBottom: () => {},
    newContentAvailable: false,
    setIsNearBottom: () => {},
    isFinalizing: false,
    isThinking: false,
    isStreaming: false,
    displayTurns: [],
    issueNumber: 123,
  }
}

function setupDefaults() {
  _issueData = {
    id: 'issue-1',
    number: 123,
    title: 'Compact viewport spec',
    body: 'Body',
    status: 'in_progress',
    stage: 'build',
    labels: {},
    createdAt: '2026-06-15T10:00:00.000Z',
    updatedAt: '2026-06-15T10:30:00.000Z',
    projectId: TEST_PROJECT.id,
    workflowRunId: 'wr-1',
  }
  _coderSessionsData = [
    {
      id: 'sess-1',
      sessionName: 'build',
      runtimeSessionId: 'acp-1',
      executionId: 'exec-1',
      taskDescription: 'Build',
      status: 'active',
      createdAt: '2026-06-15T10:00:00.000Z',
      completedAt: null,
      model: 'minimax/MiniMax-M3',
      runtime: 'opencode',
      stage: 'build',
      title: 'Build session',
      lastDataAt: '2026-06-15T11:00:00.000Z',
      probeSentAt: null,
      probeDeadlineAt: null,
      failureReason: null,
    },
  ]
  _metadataData = makeMockMetadata()
}

function renderPage() {
  const queryClient = createQueryClient()
  queryClients.push(queryClient)
  vi.spyOn(Date, 'now').mockReturnValue(_now.getTime())

  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
        <MemoryRouter initialEntries={['/issues/123/workflow/sessions/build']}>
          <Routes>
            <Route path="/issues/:number/workflow/sessions/:sessionName" element={<SessionPage dependencies={sessionPageDependencies} />} />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  setupDefaults()
})

afterEach(() => {
  cleanup()
  for (const q of queryClients) q.clear()
  queryClients.length = 0
  vi.restoreAllMocks()
})

describe('Coder Session compact viewport — structural contract', () => {
  it('hides nonessential SessionHeader metadata items below md via hidden md:inline class', async () => {
    const { container } = renderPage()

    await waitFor(() => {
      if (!container.querySelector('[data-testid="session-header"]')) {
        throw new Error('not ready yet')
      }
    })

    const header = container.querySelector('[data-testid="session-header"]') as HTMLElement

    const hiddenBelowMd = Array.from(header.querySelectorAll('.hidden.md\\:inline'))
    expect(hiddenBelowMd.length, 'nonessential metadata should carry hidden md:inline').toBeGreaterThan(0)

    const modelNode = hiddenBelowMd.find((node) => node.textContent?.trim() === 'minimax/MiniMax-M3')
    expect(modelNode, 'model should be hidden below md').toBeDefined()

    const fileSummaryNode = hiddenBelowMd.find(
      (node) => node.textContent?.trim() === '1 file changed',
    )
    expect(fileSummaryNode, '1 file changed should be hidden below md').toBeDefined()

    const sessionIdNode = hiddenBelowMd.find((node) => node.textContent?.trim() === 'agent-se')
    expect(sessionIdNode, 'session id should be hidden below md').toBeDefined()

    const separators = hiddenBelowMd.filter((node) => node.textContent?.trim() === '·')
    expect(separators.length, 'separator dots should be hidden below md').toBeGreaterThan(0)

    const lastActivityNodes = hiddenBelowMd.filter((node) =>
      node.textContent?.trim().match(/ago|just now|never/),
    )
    expect(lastActivityNodes.length, 'last-activity text should be present and hidden below md').toBeGreaterThan(0)
  })

  it('hides the duration span below md on a terminal session', async () => {
    _metadataData = makeMockMetadata({
      status: 'completed',
      statusKind: 'completed',
      completedAt: '2026-06-15T11:00:00.000Z',
      lastActivityAt: '2026-06-15T11:00:00.000Z',
    })
    _coderSessionsData = [
      {
        ..._coderSessionsData[0],
        status: 'completed',
        completedAt: '2026-06-15T11:00:00.000Z',
      },
    ]

    const { container } = renderPage()

    await waitFor(() => {
      if (!container.querySelector('[data-testid="session-header"]')) {
        throw new Error('not ready yet')
      }
    })

    const header = container.querySelector('[data-testid="session-header"]') as HTMLElement
    const hiddenBelowMd = Array.from(header.querySelectorAll('.hidden.md\\:inline'))
    const durationNode = hiddenBelowMd.find((node) => node.textContent?.trim() === '1h 00m')
    expect(durationNode, 'duration should be hidden below md on terminal session').toBeDefined()
  })

  it('keeps the session name (h1), StatusBadge, and stage chip visible without hidden class', async () => {
    const { container } = renderPage()

    await waitFor(() => {
      if (!container.querySelector('[data-testid="session-header"]')) {
        throw new Error('not ready yet')
      }
    })

    const h1 = container.querySelector('h1') as HTMLElement
    expect(h1).not.toBeNull()
    expect(h1.className).not.toMatch(/\bhidden\b/)
    expect(h1.textContent?.trim().length ?? 0).toBeGreaterThan(0)

    const statusBadge = container.querySelector('[data-testid="session-status-badge"]') as HTMLElement
    expect(statusBadge.className).not.toMatch(/\bhidden\b/)

    const stageChip = container.querySelector('[data-testid="session-stage-chip"]') as HTMLElement
    expect(stageChip.className).not.toMatch(/\bhidden\b/)
  })

  it('adds min-h-[120px] md:min-h-0 to the transcript scroll container', async () => {
    const { container } = renderPage()

    await waitFor(() => {
      if (!container.querySelector('[data-testid="session-transcript-scroll-container"]')) {
        throw new Error('not ready yet')
      }
    })

    const scrollContainer = container.querySelector('[data-testid="session-transcript-scroll-container"]') as HTMLElement
    expect(scrollContainer.className).toContain('min-h-[120px]')
    expect(scrollContainer.className).toContain('md:min-h-0')
    expect(scrollContainer.className).toContain('flex-1')
    expect(scrollContainer.className).toContain('overflow-y-auto')
  })

  it('keeps the recovery bar wrapper padding at py-2 with md:py-3', async () => {
    const { container } = renderPage()

    await waitFor(() => {
      if (!container.querySelector('[data-testid="session-recovery-bar"]')) {
        throw new Error('not ready yet')
      }
    })

    const recoveryBar = container.querySelector('[data-testid="session-recovery-bar"]') as HTMLElement
    expect(recoveryBar.className).toContain('py-2')
    expect(recoveryBar.className).toContain('md:py-3')
  })

  it('keeps the recovery bar inner content always horizontal (flex-row), not stacked (flex-col)', async () => {
    const { container } = renderPage()

    await waitFor(() => {
      if (!container.querySelector('[data-testid="session-recovery-compact"]')) {
        throw new Error('not ready yet')
      }
    })

    const recoveryBar = container.querySelector('[data-testid="session-recovery-bar"]') as HTMLElement
    const innerRow = Array.from(recoveryBar.querySelectorAll('div'))
      .find((node) => (node as HTMLElement).className.includes('flex-row') && (node as HTMLElement).className.includes('gap-2'))
    expect(innerRow, 'recovery bar must contain a flex-row container').toBeDefined()
    expect((innerRow as HTMLElement).className).toContain('flex-row')
    expect((innerRow as HTMLElement).className).not.toMatch(/\bflex-col\b/)

    const compact = container.querySelector('[data-testid="session-recovery-compact"]')
    const reset = container.querySelector('[data-testid="session-recovery-reset"]')
    expect(compact).not.toBeNull()
    expect(reset).not.toBeNull()
  })

  it('hides SessionUsageSummary secondary token breakdowns below md while keeping totals, context, and cost visible', async () => {
    const { container } = renderPage()

    await waitFor(() => {
      if (!container.querySelector('[data-testid="session-usage-summary"]')) {
        throw new Error('not ready yet')
      }
    })

    const summary = container.querySelector('[data-testid="session-usage-summary"]') as HTMLElement
    expect(summary.className).toContain('py-1')
    expect(summary.className).toContain('md:py-2')

    const secondaryTestIds = [
      'usage-summary-input',
      'usage-summary-output',
      'usage-summary-cached',
      'usage-summary-thought',
    ]
    for (const testId of secondaryTestIds) {
      const node = summary.querySelector(`[data-testid="${testId}"]`) as HTMLElement | null
      expect(node, `${testId} should be present in DOM`).not.toBeNull()
      expect(node!.className, `${testId} should be hidden below md`).toMatch(/\bhidden md:inline\b|\bhidden md:flex\b/)
    }

    const total = container.querySelector('[data-testid="usage-summary-total"]') as HTMLElement
    expect(total).not.toBeNull()
    expect(total.className).not.toMatch(/\bhidden\b/)

    const cost = container.querySelector('[data-testid="usage-summary-cost"]') as HTMLElement
    expect(cost).not.toBeNull()
    expect(cost.className).not.toMatch(/\bhidden\b/)

    const context = container.querySelector('[data-testid="usage-summary-context"]') as HTMLElement
    expect(context).not.toBeNull()
    expect(context.className).not.toMatch(/\bhidden\b/)
  })

  it('keeps session-cancel-trigger visible when a cancel dependency is provided', () => {
    const { container } = render(
      <MemoryRouter>
        <SessionDetailShell
          data={makeCancelableSessionData()}
          components={sessionPageDependencies.shellComponents}
        />
      </MemoryRouter>,
    )

    const cancelTrigger = container.querySelector('[data-testid="session-cancel-trigger"]') as HTMLElement
    expect(cancelTrigger).not.toBeNull()
    expect(cancelTrigger.className).not.toMatch(/\bhidden\b/)
  })

  it('preserves all existing region anchors', async () => {
    const { container } = renderPage()

    await waitFor(() => {
      if (!container.querySelector('[data-testid="session-recovery-compact"]')) {
        throw new Error('not ready yet')
      }
    })

    const expected = [
      'session-header',
      'session-transcript-scroll-container',
      'session-sticky-title',
      'session-recovery-bar',
      'session-recovery-compact',
      'session-recovery-reset',
      'session-followup-composer',
      'session-sibling-navigation-slot',
    ]
    for (const testId of expected) {
      expect(
        container.querySelector(`[data-testid="${testId}"]`),
        `expected ${testId} to be present`,
      ).not.toBeNull()
    }
  })

  it('keeps the desktop metadata cluster layout classes unchanged (flex-col sm:flex-row)', async () => {
    const { container } = renderPage()

    await waitFor(() => {
      if (!container.querySelector('[data-testid="session-header"]')) {
        throw new Error('not ready yet')
      }
    })

    const stageChip = container.querySelector('[data-testid="session-stage-chip"]') as HTMLElement
    const cluster = stageChip.parentElement as HTMLElement
    expect(cluster.className).toContain('flex-col')
    expect(cluster.className).toContain('sm:flex-row')
  })

  it('keeps the recovery bar inside the transcript scroll container (region order preserved)', async () => {
    const { container } = renderPage()

    await waitFor(() => {
      if (!container.querySelector('[data-testid="session-recovery-bar"]')) {
        throw new Error('not ready yet')
      }
    })

    const scrollContainer = container.querySelector('[data-testid="session-transcript-scroll-container"]') as HTMLElement
    const recoveryBar = scrollContainer.querySelector('[data-testid="session-recovery-bar"]')
    expect(recoveryBar).not.toBeNull()
  })

  it('keeps the sticky title inside the transcript scroll container (region order preserved)', async () => {
    const { container } = renderPage()

    await waitFor(() => {
      if (!container.querySelector('[data-testid="session-sticky-title"]')) {
        throw new Error('not ready yet')
      }
    })

    const scrollContainer = container.querySelector('[data-testid="session-transcript-scroll-container"]') as HTMLElement
    const stickyTitle = scrollContainer.querySelector('[data-testid="session-sticky-title"]')
    expect(stickyTitle).not.toBeNull()
  })
})
