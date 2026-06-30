// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Routes, Route } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { SessionPage } from './SessionPage'

const mocks = vi.hoisted(() => ({
  useIssueData: undefined as any,
  useCoderSessionsReturn: {
    sessions: [] as any[],
    isLoading: false,
  },
  siblingReturn: {
    previous: null,
    next: null,
    sessions: [] as any[],
  },
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
  routeParams: {
    number: '123',
    sessionName: 'session-1',
  },
}))

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>()
  return {
    ...actual,
    useParams: () => mocks.routeParams,
  }
})

vi.mock('../../../entities/issue', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/issue')>()
  return {
    ...actual,
    useIssue: () => ({ data: mocks.useIssueData }),
  }
})

vi.mock('../../../entities/coder-session', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/coder-session')>()
  return {
    ...actual,
    useCoderSessions: () => mocks.useCoderSessionsReturn,
    getAgentSessionMetadata: vi.fn().mockResolvedValue({
      id: 'agent-session-1',
      sessionName: 'session-1',
      acpSessionId: 'acp-1',
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
    }),
    getAgentSessionTranscript: vi.fn().mockResolvedValue({
      turns: [
        { id: 'turn-1', index: 0, kind: 'prompt', role: 'user', content: { text: 'Build it' }, parts: [] },
        { id: 'turn-2', index: 1, kind: 'response', role: 'assistant', content: { text: 'Done' }, parts: [] },
      ],
      lastActivityAt: '2026-06-15T10:29:55.000Z',
    }),
  }
})

vi.mock('../../../widgets/issue-workflow', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../widgets/issue-workflow')>()
  return {
    ...actual,
    useSiblingSessions: () => mocks.siblingReturn,
  }
})

vi.mock('../../../widgets/session-transcript', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../widgets/session-transcript')>()
  return {
    ...actual,
    useSessionTranscript: () => mocks.transcriptReturn,
    projectTurn: (turn: any) => turn,
    SessionTranscriptLayout: ({ turns }: { turns: any[] }) => (
      <div data-testid="session-transcript-layout">{turns.length} turns</div>
    ),
  }
})

vi.mock('../../../widgets/coder-session', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../widgets/coder-session')>()
  return {
    ...actual,
    SessionRecoveryActions: ({
      issueNumber,
      sessionName,
      bare,
    }: {
      issueNumber: number
      sessionName: string
      bare?: boolean
    }) => (
      <div
        data-testid="session-recovery-actions"
        data-issue-number={issueNumber}
        data-session-name={sessionName}
        data-bare={bare ? 'true' : 'false'}
      >
        <button data-testid="session-recovery-compact" type="button">Compact</button>
        <button data-testid="session-recovery-reset" type="button">Reset</button>
      </div>
    ),
    SessionFollowupComposer: ({ disabled }: { disabled: boolean }) => (
      <div data-testid="session-followup-composer" data-disabled={disabled ? 'true' : 'false'} />
    ),
  }
})

vi.mock('../../../widgets/session-health', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../widgets/session-health')>()
  return {
    ...actual,
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
  }
})

vi.mock('../../../shared/lib/useDocumentTitle', () => ({
  useDocumentTitle: () => {},
}))

function createQueryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } })
}

async function renderPage() {
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
        <MemoryRouter initialEntries={['/issues/123/workflow/sessions/session-1']}>
          <Routes>
            <Route
              path="/issues/:number/workflow/sessions/:sessionName"
              element={<SessionPage />}
            />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
  // The metadata/transcript queries are async; wait for them to resolve
  // before rendering assertions so the page reaches the main transcript state.
  await waitFor(() => {
    if (!result.container.querySelector('[data-testid="session-transcript-scroll-container"]')) {
      throw new Error('not ready yet')
    }
  })
  return result
}

function setupDefaultMocks() {
  mocks.useIssueData = {
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
  mocks.useCoderSessionsReturn = {
    sessions: [
      {
        id: 'session-1',
        sessionName: 'session-1',
        workflowRunId: 'wr-1',
        acpSessionId: 'acp-1',
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
    ],
    isLoading: false,
  }
  mocks.siblingReturn = { previous: null, next: null, sessions: [] }
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
  mocks.routeParams = { number: '123', sessionName: 'session-1' }
}

describe('SessionPage sticky recovery bar (issue-245 T-004)', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    setupDefaultMocks()
  })

  afterEach(() => {
    cleanup()
  })

  it('renders the transcript scroll container for the main transcript state', async () => {
    const { container } = await renderPage()
    const scrollContainer = container.querySelector('[data-testid="session-transcript-scroll-container"]')
    expect(scrollContainer).not.toBeNull()
    expect(scrollContainer!.className).toContain('overflow-y-auto')
  })

  it('places the recovery bar inside the transcript scroll container', async () => {
    const { container } = await renderPage()
    const scrollContainer = container.querySelector('[data-testid="session-transcript-scroll-container"]')
    expect(scrollContainer).not.toBeNull()
    const recoveryBarInScroll = scrollContainer!.querySelector('[data-testid="session-recovery-bar"]')
    expect(recoveryBarInScroll).not.toBeNull()
  })

  it('marks the recovery bar inside the scroll container as sticky', async () => {
    const { container } = await renderPage()
    const scrollContainer = container.querySelector('[data-testid="session-transcript-scroll-container"]')
    expect(scrollContainer).not.toBeNull()
    const recoveryBar = scrollContainer!.querySelector('[data-testid="session-recovery-bar"]')
    expect(recoveryBar).not.toBeNull()

    const className = recoveryBar!.className
    expect(className).toContain('sticky')
    expect(className).toContain('top-0')
  })

  it('gives the sticky recovery bar a background and a z-index above the transcript', async () => {
    const { container } = await renderPage()
    const recoveryBar = container.querySelector('[data-testid="session-transcript-scroll-container"] [data-testid="session-recovery-bar"]')
    expect(recoveryBar).not.toBeNull()

    const className = recoveryBar!.className
    expect(className).toContain('bg-white')
    expect(className).toMatch(/\bz-\d+/)
  })

  it('keeps the context-health bar and the Compact/Reset actions reachable inside the sticky recovery region', async () => {
    const { container } = await renderPage()
    const recoveryBar = container.querySelector('[data-testid="session-transcript-scroll-container"] [data-testid="session-recovery-bar"]')
    expect(recoveryBar).not.toBeNull()

    const contextHealth = recoveryBar!.querySelector('[data-testid="context-health-bar"]')
    expect(contextHealth).not.toBeNull()
    expect(contextHealth!.getAttribute('data-used')).toBe('12000')
    expect(contextHealth!.getAttribute('data-size')).toBe('32000')

    const compact = recoveryBar!.querySelector('[data-testid="session-recovery-compact"]')
    const reset = recoveryBar!.querySelector('[data-testid="session-recovery-reset"]')
    expect(compact).not.toBeNull()
    expect(reset).not.toBeNull()
  })

  it('renders the sticky title strip with identity info and usage摘要 inside the scroll container', async () => {
    const { container } = await renderPage()
    const scrollContainer = container.querySelector('[data-testid="session-transcript-scroll-container"]')
    expect(scrollContainer).not.toBeNull()

    await waitFor(() => {
      const title = scrollContainer!.querySelector('[data-testid="session-sticky-title"]')
      if (!title) throw new Error('sticky title not rendered yet')
      // Wait until the transcript data resolves so turnCount is 2
      if (!title.textContent?.includes('2 turns')) throw new Error('turn count not resolved yet')
    })

    const stickyTitle = scrollContainer!.querySelector('[data-testid="session-sticky-title"]')
    expect(stickyTitle).not.toBeNull()

    const className = stickyTitle!.className
    expect(className).toContain('sticky')
    expect(className).toContain('top-0')
    expect(className).toContain('bg-white')

    expect(stickyTitle!.textContent).toContain('session-1')
    expect(stickyTitle!.textContent).toContain('Completed')
    expect(stickyTitle!.textContent).toContain('2 turns')
    expect(stickyTitle!.textContent).toContain('1.5k tokens')
    expect(stickyTitle!.textContent).toContain('38% ctx')
  })

  it('keeps the outer session header non-sticky — only the title strip and recovery bar are sticky inside the scroll container', async () => {
    const { container } = await renderPage()

    const scrollContainer = container.querySelector('[data-testid="session-transcript-scroll-container"]')
    expect(scrollContainer).not.toBeNull()

    // The sticky title strip sits inside the scroll container.
    const stickyTitle = scrollContainer!.querySelector('[data-testid="session-sticky-title"]')
    expect(stickyTitle).not.toBeNull()
    expect(stickyTitle!.className).toContain('sticky')

    // The outer SessionHeader (a sibling of the scroll container) must not
    // carry any sticky positioning class.
    const stickyHeaderElements = Array.from(
      container.querySelectorAll('[class*="sticky"]'),
    ).filter((el) => !scrollContainer!.contains(el))
    expect(stickyHeaderElements.length).toBe(0)
  })

  it('keeps the active||pending disable behavior on Compact/Reset intact (completed session stays enabled)', async () => {
    const { container } = await renderPage()
    const recoveryBar = container.querySelector('[data-testid="session-transcript-scroll-container"] [data-testid="session-recovery-bar"]')
    expect(recoveryBar).not.toBeNull()

    const recoveryActions = recoveryBar!.querySelector('[data-testid="session-recovery-actions"]')
    expect(recoveryActions).not.toBeNull()
    expect(recoveryActions!.getAttribute('data-bare')).toBe('true')

    const compact = recoveryBar!.querySelector('[data-testid="session-recovery-compact"]')
    const reset = recoveryBar!.querySelector('[data-testid="session-recovery-reset"]')
    expect(compact).not.toBeNull()
    expect(reset).not.toBeNull()
    expect((compact as HTMLButtonElement).disabled).toBe(false)
    expect((reset as HTMLButtonElement).disabled).toBe(false)
  })

  it('the sticky title strip is the first child of the transcript scroll container, above the recovery bar and transcript', async () => {
    const { container } = await renderPage()
    const scrollContainer = container.querySelector('[data-testid="session-transcript-scroll-container"]')
    expect(scrollContainer).not.toBeNull()

    const children = Array.from(scrollContainer!.children)
    expect(children.length).toBeGreaterThan(0)
    const firstChild = children[0] as HTMLElement
    expect(firstChild.getAttribute('data-testid')).toBe('session-sticky-title')
  })

  it('keeps the recovery bar visible in the page scroll context after the transcript has scrolled (scroll-stick)', async () => {
    const originalGetBoundingClientRect = Element.prototype.getBoundingClientRect

    try {
      // Simulate a scrolled transcript: getBoundingClientRect for elements
      // inside the scroll container reflects the scrolled coordinates. In a
      // real browser, a position: sticky element with `top: 0` keeps its
      // bounding rect's top pinned to the scroll container's visible top;
      // without sticky positioning it would scroll out of view (top < container.top).
      Element.prototype.getBoundingClientRect = vi.fn(function mockRect(this: Element) {
        const testId = (this as HTMLElement).getAttribute?.('data-testid') ?? ''
        if (testId === 'session-transcript-scroll-container') {
          return { top: 120, bottom: 480, left: 0, right: 800, width: 800, height: 360, x: 0, y: 120, toJSON: () => ({}) } as DOMRect
        }
        if (testId === 'session-recovery-bar') {
          // Sticky: stays at the top of the scroll container's visible area
          // (top === scrollContainer.top), regardless of how far the transcript
          // content has scrolled past it.
          return { top: 120, bottom: 180, left: 0, right: 800, width: 800, height: 60, x: 0, y: 120, toJSON: () => ({}) } as DOMRect
        }
        // Transcript content (turns) sits below the recovery bar — scrolled
        // out of the visible area at the top of the container.
        if (testId === 'session-transcript-layout') {
          return { top: 180, bottom: 2400, left: 0, right: 800, width: 800, height: 2220, x: 0, y: 180, toJSON: () => ({}) } as DOMRect
        }
        // Default: leave behaviour unchanged.
        return originalGetBoundingClientRect.call(this)
      })

      const { container } = await renderPage()
      const scrollContainer = container.querySelector('[data-testid="session-transcript-scroll-container"]')
      expect(scrollContainer).not.toBeNull()

      const recoveryBar = scrollContainer!.querySelector('[data-testid="session-recovery-bar"]')
      expect(recoveryBar).not.toBeNull()
      expect(recoveryBar!.getAttribute('data-sticky')).toBe('true')

      const scrollRect = scrollContainer!.getBoundingClientRect()
      const barRect = recoveryBar!.getBoundingClientRect()

      // The recovery bar must remain visible inside the scroll container's
      // visible area, and its sticky `top: 0` pins it to the top edge of the
      // scroll container — independent of how far the transcript body has
      // scrolled (the transcript content starts at scrollContainer.bottom in
      // this mocked view, i.e. past the visible area).
      expect(barRect.top).toBe(scrollRect.top)
      expect(barRect.bottom).toBeLessThanOrEqual(scrollRect.bottom)

      // The Compact / Reset affordances inside the bar remain reachable even
      // when the transcript body is scrolled past.
      const compact = recoveryBar!.querySelector('[data-testid="session-recovery-compact"]')
      const reset = recoveryBar!.querySelector('[data-testid="session-recovery-reset"]')
      expect(compact).not.toBeNull()
      expect(reset).not.toBeNull()
    } finally {
      Element.prototype.getBoundingClientRect = originalGetBoundingClientRect
    }
  })
})
