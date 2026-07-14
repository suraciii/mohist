import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, waitFor } from '@testing-library/react'
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
  },
  shellComponents: {
    SessionTranscriptLayout: ({ turns }: { turns: any[] }) => (
      <div data-testid="session-transcript-layout">{turns.length} turns</div>
    ),
    SessionRecoveryActions: ({ bare }: { bare?: boolean }) => (
      <div data-testid="session-recovery-actions" data-bare={bare ? 'true' : 'false'}>
        <button data-testid="session-recovery-compact" type="button">Compact</button>
        <button data-testid="session-recovery-reset" type="button">Reset</button>
      </div>
    ),
    SessionFollowupComposer: ({ disabled }: { disabled?: boolean }) => (
      <div data-testid="session-followup-composer" data-disabled={disabled ? 'true' : 'false'} />
    ),
    ContextHealthBar: ({
      contextWindowUsed,
      contextWindowSize,
    }: {
      contextWindowUsed?: number | null
      contextWindowSize?: number | null
    }) => (
      <div
        data-testid="context-health-bar"
        data-used={contextWindowUsed ?? ''}
        data-size={contextWindowSize ?? ''}
      >
        context health bar
      </div>
    ),
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

interface RenderPageOptions {
  initialEntry?: string
}

async function renderPage(options: RenderPageOptions = {}) {
  const queryClient = createQueryClient()
  const entry = options.initialEntry ?? '/issues/123/workflow/sessions/session-1'
  const result = render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj-1" initialProjects={[{
        id: 'proj-1',
        name: 'Test',
        createdAt: '2026-01-01T00:00:00Z',
        updatedAt: '2026-01-01T00:00:00Z',
        repositories: [],
      }]}>
        <MemoryRouter initialEntries={[entry]}>
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

function setupDefaultMocks() {
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
      acpSessionId: 'rt-latest',
      projectId: 'proj-1',
      issueNumber: 123,
      runnerId: 'runner-1',
      status: 'completed',
      stage: 'build',
      model: 'minimax/MiniMax-M3',
      workDir: null,
      processPid: null,
      createdAt: '2026-06-15T10:00:00.000Z',
      startedAt: '2026-06-15T10:00:05.000Z',
      completedAt: '2026-06-15T10:30:00.000Z',
      lastDataAt: '2026-06-15T10:29:55.000Z',
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

function baseMetadata(overrides: Partial<AgentSessionMetadata> = {}): AgentSessionMetadata {
  return {
    id: 'agent-session-1',
    sessionName: 'session-1',
    acpSessionId: 'rt-latest',
    title: 'Test session',
    status: 'completed',
    statusKind: 'completed',
    stage: 'build',
    model: 'minimax/MiniMax-M3',
    createdAt: '2026-06-15T10:00:00.000Z',
    completedAt: '2026-06-15T10:30:00.000Z',
    lastActivityAt: '2026-06-15T10:29:55.000Z',
    lastDataAt: '2026-06-15T10:29:55.000Z',
    changedFiles: [],
    metadata: { partCount: 4, toolCount: 2 },
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
      toolCallCount: 2,
      toolErrorCount: 0,
    },
    ...overrides,
  }
}

describe('SessionPage lineage link wiring', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    setupDefaultMocks()
  })

  afterEach(() => {
    cleanup()
    for (const queryClient of queryClients) queryClient.clear()
    queryClients.length = 0
  })

  it('renders no lineage link when the metadata carries no runtimeSessionLineage (historical session)', async () => {
    _metadataData = baseMetadata()

    const { container } = await renderPage()
    const recoveryBar = container.querySelector('[data-testid="session-transcript-scroll-container"] [data-testid="session-recovery-bar"]')
    expect(recoveryBar).not.toBeNull()
    expect(recoveryBar!.querySelector('[data-testid="compaction-lineage-link"]')).toBeNull()
  })

  it('renders no lineage link when the lineage is a single entry (no compaction relationship)', async () => {
    _metadataData = baseMetadata({
      runtimeSessionLineage: [
        { agentRuntimeSessionId: 'rt-latest', boundAt: '2026-06-15T10:00:00.000Z' },
      ],
    })

    const { container } = await renderPage()
    const recoveryBar = container.querySelector('[data-testid="session-transcript-scroll-container"] [data-testid="session-recovery-bar"]')
    expect(recoveryBar).not.toBeNull()
    expect(recoveryBar!.querySelector('[data-testid="compaction-lineage-link"]')).toBeNull()
  })

  it('renders a predecessor link inside the recovery region when the lineage has 2 entries (common case, page shows latest)', async () => {
    _metadataData = baseMetadata({
      runtimeSessionLineage: [
        { agentRuntimeSessionId: 'rt-prev', boundAt: '2026-06-15T09:00:00.000Z' },
        { agentRuntimeSessionId: 'rt-latest', boundAt: '2026-06-15T10:00:00.000Z' },
      ],
    })

    const { container } = await renderPage()
    const recoveryBar = container.querySelector('[data-testid="session-transcript-scroll-container"] [data-testid="session-recovery-bar"]')
    expect(recoveryBar).not.toBeNull()

    const lineageLink = recoveryBar!.querySelector('[data-testid="compaction-lineage-link"]')
    expect(lineageLink).not.toBeNull()
    const predecessor = recoveryBar!.querySelector('[data-testid="compaction-lineage-link-predecessor"]')
    expect(predecessor).not.toBeNull()
    expect(predecessor!.getAttribute('data-target-runtime-session-id')).toBe('rt-prev')
    expect(recoveryBar!.querySelector('[data-testid="compaction-lineage-link-successor"]')).toBeNull()
  })

  it('uses the ?rt=<runtimeSessionId> anchor scheme within the existing session route', async () => {
    _metadataData = baseMetadata({
      runtimeSessionLineage: [
        { agentRuntimeSessionId: 'rt-prev', boundAt: '2026-06-15T09:00:00.000Z' },
        { agentRuntimeSessionId: 'rt-latest', boundAt: '2026-06-15T10:00:00.000Z' },
      ],
    })

    const { container } = await renderPage()
    const predecessor = container.querySelector('[data-testid="compaction-lineage-link-predecessor"]')
    expect(predecessor).not.toBeNull()
    const href = predecessor!.getAttribute('href')
    expect(href).not.toBeNull()
    expect(href).toContain('/workflow/sessions/session-1')
    expect(href).toContain('rt=rt-prev')
    expect(href).toMatch(/^.*\?rt=rt-prev$/)
  })

  it('preserves the Activity return context on lineage links', async () => {
    _metadataData = baseMetadata({
      runtimeSessionLineage: [
        { agentRuntimeSessionId: 'rt-prev', boundAt: '2026-06-15T09:00:00.000Z' },
        { agentRuntimeSessionId: 'rt-latest', boundAt: '2026-06-15T10:00:00.000Z' },
      ],
    })

    const { container } = await renderPage({
      initialEntry: '/issues/123/workflow/sessions/session-1?from=activity',
    })

    expect(container.querySelector('[data-testid="compaction-lineage-link-predecessor"]'))
      .toHaveAttribute('href', expect.stringContaining('rt=rt-prev&from=activity'))
  })

  it('renders both predecessor and successor links when the user is viewing a non-latest runtime session (?rt= param)', async () => {
    _metadataData = baseMetadata({
      runtimeSessionLineage: [
        { agentRuntimeSessionId: 'rt-1', boundAt: '2026-06-15T08:00:00.000Z' },
        { agentRuntimeSessionId: 'rt-2', boundAt: '2026-06-15T09:00:00.000Z' },
        { agentRuntimeSessionId: 'rt-latest', boundAt: '2026-06-15T10:00:00.000Z' },
      ],
    })

    const { container } = await renderPage({
      initialEntry: '/issues/123/workflow/sessions/session-1?rt=rt-2',
    })

    const lineageLink = container.querySelector('[data-testid="compaction-lineage-link"]')
    expect(lineageLink).not.toBeNull()
    expect(lineageLink!.getAttribute('data-viewed-index')).toBe('1')

    const predecessor = container.querySelector('[data-testid="compaction-lineage-link-predecessor"]')
    expect(predecessor).toHaveAttribute('data-target-runtime-session-id', 'rt-1')

    const successor = container.querySelector('[data-testid="compaction-lineage-link-successor"]')
    expect(successor).toHaveAttribute('data-target-runtime-session-id', 'rt-latest')
  })

  it('places the lineage link inside the sticky recovery region of the transcript scroll container', async () => {
    _metadataData = baseMetadata({
      runtimeSessionLineage: [
        { agentRuntimeSessionId: 'rt-prev', boundAt: '2026-06-15T09:00:00.000Z' },
        { agentRuntimeSessionId: 'rt-latest', boundAt: '2026-06-15T10:00:00.000Z' },
      ],
    })

    const { container } = await renderPage()
    const scrollContainer = container.querySelector('[data-testid="session-transcript-scroll-container"]')
    expect(scrollContainer).not.toBeNull()
    const recoveryBar = scrollContainer!.querySelector('[data-testid="session-recovery-bar"]')
    expect(recoveryBar).not.toBeNull()
    expect(recoveryBar!.getAttribute('data-sticky')).toBe('true')

    const lineageLinkInsideRecovery = recoveryBar!.querySelector('[data-testid="compaction-lineage-link"]')
    expect(lineageLinkInsideRecovery).not.toBeNull()

    expect(recoveryBar!.querySelector('[data-testid="context-health-bar"]')).not.toBeNull()
    expect(recoveryBar!.querySelector('[data-testid="session-recovery-actions"]')).not.toBeNull()
  })

  it('falls back to the latest runtime session when the URL ?rt= does not match any chain entry', async () => {
    _metadataData = baseMetadata({
      runtimeSessionLineage: [
        { agentRuntimeSessionId: 'rt-prev', boundAt: '2026-06-15T09:00:00.000Z' },
        { agentRuntimeSessionId: 'rt-latest', boundAt: '2026-06-15T10:00:00.000Z' },
      ],
    })

    const { container } = await renderPage({
      initialEntry: '/issues/123/workflow/sessions/session-1?rt=rt-does-not-exist',
    })

    const lineageLink = container.querySelector('[data-testid="compaction-lineage-link"]')
    expect(lineageLink).not.toBeNull()
    expect(lineageLink!.getAttribute('data-viewed-index')).toBe('1')

    const predecessor = container.querySelector('[data-testid="compaction-lineage-link-predecessor"]')
    expect(predecessor).toHaveAttribute('data-target-runtime-session-id', 'rt-prev')
    expect(container.querySelector('[data-testid="compaction-lineage-link-successor"]')).toBeNull()
  })
})
