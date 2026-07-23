import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Routes, Route } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { SessionPage, type SessionPageDependencies } from './SessionPage'
import { installIntersectionObserver } from './SessionPageStickyTestSupport'

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

let intersectionObserver: ReturnType<typeof installIntersectionObserver>

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
    SessionFollowupComposer: ({ disabled }: { disabled?: boolean }) => (
      <div data-testid="session-followup-composer" data-disabled={disabled ? 'true' : 'false'} />
    ),
    ContextHealthBar: ({
      contextWindowUsed,
      contextWindowSize,
      healthStatus,
    }: {
      contextWindowUsed?: number | null
      contextWindowSize?: number | null
      healthStatus?: string | null
    }) => (
      <div
        data-testid="context-health-bar"
        data-used={contextWindowUsed ?? ''}
        data-size={contextWindowSize ?? ''}
        data-health-status={healthStatus ?? ''}
      >
        context health bar
      </div>
    ),
  },
}


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
      runtimeSessionId: 'runtime-1',
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
  _metadataData = {
    id: 'agent-session-1',
    sessionName: 'session-1',
    runtimeSessionId: 'runtime-1',
    title: 'Test session',
    status: 'completed',
    statusKind: 'completed',
    // Issue 484: the sticky title shows an activity-derived status badge.
    activity: 'idle',
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
      healthStatus: 'green',
    },
    eventSummary: {
      resolvedModel: 'minimax/MiniMax-M3',
      toolCallCount: 2,
      toolErrorCount: 0,
    },
  }
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

describe('SessionPage sticky recovery bar', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    intersectionObserver = installIntersectionObserver()
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
    expect(className).toContain('top-9')
  })

  it('gives the sticky recovery bar a background and a z-index above the transcript', async () => {
    const { container } = await renderPage()
    const recoveryBar = container.querySelector('[data-testid="session-transcript-scroll-container"] [data-testid="session-recovery-bar"]')
    expect(recoveryBar).not.toBeNull()

    const className = recoveryBar!.className
    expect(className).toContain('bg-background')
    expect(className).toMatch(/\bz-\d+/)
    expect(className).toContain('top-9')
  })

  it('keeps the context-health bar and the Compact/Reset actions reachable inside the sticky recovery region', async () => {
    const { container } = await renderPage()
    const recoveryBar = container.querySelector('[data-testid="session-transcript-scroll-container"] [data-testid="session-recovery-bar"]')
    expect(recoveryBar).not.toBeNull()

    const contextHealth = recoveryBar!.querySelector('[data-testid="context-health-bar"]')
    expect(contextHealth).not.toBeNull()
    expect(contextHealth!.getAttribute('data-used')).toBe('12000')
    expect(contextHealth!.getAttribute('data-size')).toBe('32000')
    expect(contextHealth!.getAttribute('data-health-status')).toBe('green')

    const compact = recoveryBar!.querySelector('[data-testid="session-recovery-compact"]')
    const reset = recoveryBar!.querySelector('[data-testid="session-recovery-reset"]')
    expect(compact).not.toBeNull()
    expect(reset).not.toBeNull()
  })

  it('does not render the sticky title before the first observer callback', async () => {
    const { container, unmount } = await renderPage()
    const scrollContainer = container.querySelector('[data-testid="session-transcript-scroll-container"]')
    const header = container.querySelector('[data-testid="session-header"]')
    expect(scrollContainer).not.toBeNull()
    expect(header).not.toBeNull()
    expect(scrollContainer!.querySelector('[data-testid="session-sticky-title"]')).toBeNull()
    expect(container.querySelectorAll('[data-testid="session-status-badge"]')).toHaveLength(1)

    const record = intersectionObserver.getRecord()
    expect(record.options.root).toBe(scrollContainer)
    expect(record.options.threshold).toBe(0)
    expect(record.observer.observedTargets).toEqual([header])
    expect(scrollContainer!.contains(header!)).toBe(true)

    unmount()
    expect(record.observer.disconnected).toBe(true)
  })

  it('mounts the sticky title with identity, status, and turns when the observer reports the header out of view', async () => {
    const { container } = await renderPage()
    const scrollContainer = container.querySelector('[data-testid="session-transcript-scroll-container"]')
    const header = container.querySelector('[data-testid="session-header"]')
    expect(scrollContainer).not.toBeNull()
    expect(header).not.toBeNull()
    await waitFor(() => {
      expect(container.querySelector('[data-testid="session-header-turn-count"]')?.getAttribute('data-turn-count')).toBe('2')
    })

    intersectionObserver.report(header!, 0)

    const stickyTitle = scrollContainer!.querySelector('[data-testid="session-sticky-title"]')
    expect(stickyTitle).not.toBeNull()
    expect(stickyTitle!.className).toContain('sticky')
    expect(stickyTitle!.className).toContain('top-0')
    expect(stickyTitle!.className).toContain('bg-background')
    expect(stickyTitle!.textContent).toContain('session-1')
    // Activity model: a finished session reports activity 'idle'.
    expect(stickyTitle!.textContent).toContain('Idle')
    expect(stickyTitle!.textContent).toContain('2 turns')
    expect(stickyTitle!.querySelectorAll('[data-testid="session-status-badge"]')).toHaveLength(1)
    expect(stickyTitle!.querySelector('button')).toBeNull()
    expect(stickyTitle!.querySelector('[data-testid^="session-header-"]')).toBeNull()
  })

  it('keeps the sticky title hidden when the observer initially reports the header in view', async () => {
    const { container } = await renderPage()
    const scrollContainer = container.querySelector('[data-testid="session-transcript-scroll-container"]')
    const header = container.querySelector('[data-testid="session-header"]')
    expect(scrollContainer).not.toBeNull()
    expect(header).not.toBeNull()

    intersectionObserver.report(header!, 1)

    expect(scrollContainer!.querySelector('[data-testid="session-sticky-title"]')).toBeNull()
  })

  it('unmounts the sticky title when the observer reports the header has re-entered', async () => {
    const { container } = await renderPage()
    const scrollContainer = container.querySelector('[data-testid="session-transcript-scroll-container"]')
    const header = container.querySelector('[data-testid="session-header"]')
    expect(scrollContainer).not.toBeNull()
    expect(header).not.toBeNull()

    intersectionObserver.report(header!, 0)
    expect(scrollContainer!.querySelector('[data-testid="session-sticky-title"]')).not.toBeNull()

    intersectionObserver.report(header!, 0.5)
    expect(scrollContainer!.querySelector('[data-testid="session-sticky-title"]')).toBeNull()

    intersectionObserver.report(header!, 0)
    expect(scrollContainer!.querySelector('[data-testid="session-sticky-title"]')).not.toBeNull()
  })

  it('keeps the outer session header non-sticky — only the title strip and recovery bar are sticky inside the scroll container', async () => {
    const { container } = await renderPage()

    const scrollContainer = container.querySelector('[data-testid="session-transcript-scroll-container"]')
    const header = container.querySelector('[data-testid="session-header"]')
    expect(scrollContainer).not.toBeNull()
    expect(header).not.toBeNull()
    intersectionObserver.report(header!, 0)

    const stickyTitle = scrollContainer!.querySelector('[data-testid="session-sticky-title"]')
    expect(stickyTitle).not.toBeNull()
    expect(stickyTitle!.className).toContain('sticky')

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

  it('keeps the header first in the transcript scroll container and offsets the sticky title above the recovery bar', async () => {
    const { container } = await renderPage()
    const scrollContainer = container.querySelector('[data-testid="session-transcript-scroll-container"]')
    const header = container.querySelector('[data-testid="session-header"]')
    expect(scrollContainer).not.toBeNull()
    expect(header).not.toBeNull()
    intersectionObserver.report(header!, 0)

    const children = Array.from(scrollContainer!.children)
    expect(children.length).toBeGreaterThan(0)
    const firstChild = children[0] as HTMLElement
    expect(firstChild.getAttribute('data-testid')).toBe('session-header')

    const stickyTitle = scrollContainer!.querySelector('[data-testid="session-sticky-title"]') as HTMLElement | null
    expect(stickyTitle).not.toBeNull()
    expect(firstChild.compareDocumentPosition(stickyTitle!) & Node.DOCUMENT_POSITION_FOLLOWING).not.toBe(0)

    const recoveryBar = scrollContainer!.querySelector('[data-testid="session-recovery-bar"]') as HTMLElement | null
    expect(recoveryBar).not.toBeNull()
    expect(recoveryBar!.className).toContain('top-9')
  })

  it('keeps the recovery bar visible in the page scroll context after the transcript has scrolled (scroll-stick)', async () => {
    const originalGetBoundingClientRect = Element.prototype.getBoundingClientRect
    vi.spyOn(Element.prototype, 'getBoundingClientRect').mockImplementation(function mockRect(this: Element) {
      const testId = (this as HTMLElement).getAttribute?.('data-testid') ?? ''
      if (testId === 'session-transcript-scroll-container') {
        return { top: 120, bottom: 480, left: 0, right: 800, width: 800, height: 360, x: 0, y: 120, toJSON: () => ({}) } as DOMRect
      }
      if (testId === 'session-sticky-title') {
        return { top: 120, bottom: 156, left: 0, right: 800, width: 800, height: 36, x: 0, y: 120, toJSON: () => ({}) } as DOMRect
      }
      if (testId === 'session-recovery-bar') {
        return { top: 156, bottom: 216, left: 0, right: 800, width: 800, height: 60, x: 0, y: 156, toJSON: () => ({}) } as DOMRect
      }
      if (testId === 'session-transcript-layout') {
        return { top: 216, bottom: 2400, left: 0, right: 800, width: 800, height: 2184, x: 0, y: 216, toJSON: () => ({}) } as DOMRect
      }
      return originalGetBoundingClientRect.call(this)
    })

    const { container } = await renderPage()
    const scrollContainer = container.querySelector('[data-testid="session-transcript-scroll-container"]')
    const header = container.querySelector('[data-testid="session-header"]')
    expect(scrollContainer).not.toBeNull()
    expect(header).not.toBeNull()
    intersectionObserver.report(header!, 0)

    const recoveryBar = scrollContainer!.querySelector('[data-testid="session-recovery-bar"]')
    const stickyTitle = scrollContainer!.querySelector('[data-testid="session-sticky-title"]')
    expect(recoveryBar).not.toBeNull()
    expect(stickyTitle).not.toBeNull()
    expect(recoveryBar!.getAttribute('data-sticky')).toBe('true')

    const scrollRect = scrollContainer!.getBoundingClientRect()
    const titleRect = stickyTitle!.getBoundingClientRect()
    const barRect = recoveryBar!.getBoundingClientRect()

    expect(titleRect.top).toBe(scrollRect.top)
    expect(barRect.top).toBe(titleRect.bottom)
    expect(barRect.bottom).toBeLessThanOrEqual(scrollRect.bottom)

    const compact = recoveryBar!.querySelector('[data-testid="session-recovery-compact"]')
    const reset = recoveryBar!.querySelector('[data-testid="session-recovery-reset"]')
    expect(compact).not.toBeNull()
    expect(reset).not.toBeNull()
  })
})
