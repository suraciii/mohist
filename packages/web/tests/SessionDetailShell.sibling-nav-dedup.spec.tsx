import { act, render } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { MemoryRouter } from 'react-router-dom'
import { Link } from 'react-router-dom'
import { ChevronLeftIcon, ChevronRightIcon } from 'lucide-react'
import { SessionDetailShell } from '../src/pages/session/ui/SessionDetailShell'
import type { SessionDataSourceResult } from '../src/pages/session/data/SessionDataSource'
import { setMatchesForTest } from '../src/shared/lib/use-media-query'
import { ProjectProvider } from '../src/entities/project/model/ProjectContext'
import { TEST_PROJECT } from './test-utils'

const FIXED_NOW = new Date('2026-06-15T12:00:00.000Z')

function makeDataWithSiblings(): SessionDataSourceResult {
  const siblingNav = (
    <div data-testid="session-sibling-navigation">
      <Link
        to="/issues/123/workflow/sessions/plan"
        data-testid="session-sibling-prev"
      >
        <ChevronLeftIcon className="h-3.5 w-3.5" aria-hidden="true" />
        <span>prev: plan</span>
      </Link>
      <Link
        to="/issues/123/workflow/sessions/check"
        data-testid="session-sibling-next"
      >
        <span>next: check</span>
        <ChevronRightIcon className="h-3.5 w-3.5" aria-hidden="true" />
      </Link>
    </div>
  )

  const siblingSidebar = (
    <aside
      className="hidden xl:flex w-64 shrink-0 flex-col border-l border-border bg-background"
      data-testid="session-sibling-sidebar"
      aria-label="Sibling sessions"
    >
      <div>plan</div>
      <div>build</div>
      <div>check</div>
    </aside>
  )

  return {
    isLoading: false,
    isError: false,
    notFound: false,
    sessionKey: 'build',
    runtimeSessionId: 'acp-1',
    meta: {
      sessionId: 'session-build',
      sessionName: 'build',
      runtimeSessionId: 'acp-1',
      executionId: null,
      title: 'Build session',
      status: 'active',
      statusKind: 'live',
      model: 'minimax/MiniMax-M3',
      stage: 'build',
      createdAt: '2026-06-15T10:00:00.000Z',
      completedAt: null,
      lastActivityAt: '2026-06-15T11:30:00.000Z',
      lastDataAt: '2026-06-15T11:30:00.000Z',
    },
    transcriptResponse: null,
    initialTurns: [],
    statusKind: 'live',
    isRunning: true,
    canFollowup: true,
    followupIsPending: false,
    sendFollowup: async () => {},
    cancel: null,
    stop: null,
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
    siblingNav,
    siblingSidebar,
    sessionTurns: [],
    transcriptVersion: 0,
    scrollToBottom: () => {},
    newContentAvailable: false,
    setIsNearBottom: () => {},
    isFinalizing: false,
    isThinking: false,
    isStreaming: false,
    displayTurns: [],
    emptyStateKind: null,
    historicalRuntimeTarget: null,
    historicalRuntimeId: null,
    issueNumber: 123,
  }
}

function makeEmptyShellComponents() {
  return {
    SessionTranscriptLayout: () => <div data-testid="session-transcript-layout-stub" />,
    SessionFollowupComposer: () => <div data-testid="session-followup-composer-stub" />,
    SessionRecoveryActions: () => <div data-testid="session-recovery-actions-stub" />,
    ContextHealthBar: () => <div data-testid="context-health-bar-stub" />,
    CompactionLineageLink: () => <div data-testid="compaction-lineage-link-stub" />,
  }
}

function renderShell(viewport: 'narrow' | 'wide') {
  setMatchesForTest(viewport === 'wide')

  return render(
    <MemoryRouter>
      <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
        <SessionDetailShell
          data={makeDataWithSiblings()}
          components={makeEmptyShellComponents()}
        />
      </ProjectProvider>
    </MemoryRouter>,
  )
}

describe('SessionDetailShell — sibling navigation dedup', () => {
  beforeEach(() => {
    setMatchesForTest(null)
    vi.spyOn(Date, 'now').mockReturnValue(FIXED_NOW.getTime())
  })

  it('omits the sibling nav slot on wide viewports and keeps the sidebar CSS wired for xl+ visibility', () => {
    const { container } = renderShell('wide')

    expect(container.querySelector('[data-testid="session-sibling-navigation-slot"]')).toBeNull()
    expect(container.querySelector('[data-testid="session-sibling-prev"]')).toBeNull()
    expect(container.querySelector('[data-testid="session-sibling-next"]')).toBeNull()

    const scrollContainer = container.querySelector('[data-testid="session-transcript-scroll-container"]') as HTMLElement | null
    expect(scrollContainer).not.toBeNull()
    const shellRoot = scrollContainer!.parentElement?.parentElement
    expect(shellRoot, 'shell root must exist').toBeTruthy()
    expect(shellRoot!.className).toMatch(/\bxl:flex-row\b/)

    const sidebar = container.querySelector('[data-testid="session-sibling-sidebar"]') as HTMLElement | null
    expect(sidebar).not.toBeNull()
    expect(sidebar!.className).toMatch(/\bhidden\b/)
    expect(sidebar!.className).toMatch(/\bxl:flex\b/)
  })

  it('omits the sibling nav slot on the first browser render when matchMedia is wide', () => {
    const matchMedia = vi.fn().mockImplementation((query: string) => ({
      matches: true,
      media: query,
      onchange: null,
      addListener: vi.fn(),
      removeListener: vi.fn(),
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      dispatchEvent: vi.fn(),
    }))
    vi.stubGlobal('matchMedia', matchMedia)

    try {
      const { container } = render(
        <MemoryRouter>
          <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
            <SessionDetailShell
              data={makeDataWithSiblings()}
              components={makeEmptyShellComponents()}
            />
          </ProjectProvider>
        </MemoryRouter>,
      )

      expect(container.querySelector('[data-testid="session-sibling-navigation-slot"]')).toBeNull()
      expect(matchMedia).toHaveBeenCalledWith('(min-width: 1280px)')
    } finally {
      vi.unstubAllGlobals()
    }
  })

  it('renders the narrow-viewport fallback slot with data-viewport=narrow and keeps the sidebar CSS-hidden by default', () => {
    const { container } = renderShell('narrow')

    const slot = container.querySelector('[data-testid="session-sibling-navigation-slot"]') as HTMLElement | null
    expect(slot).not.toBeNull()
    expect(slot!.getAttribute('data-viewport')).toBe('narrow')
    expect(slot!.querySelector('[data-testid="session-sibling-prev"]')).not.toBeNull()
    expect(slot!.querySelector('[data-testid="session-sibling-next"]')).not.toBeNull()

    const sidebar = container.querySelector('[data-testid="session-sibling-sidebar"]') as HTMLElement | null
    expect(sidebar).not.toBeNull()
    expect(sidebar!.className).toMatch(/\bhidden\b/)
    expect(sidebar!.className).toMatch(/\bxl:flex\b/)
  })

  it('never renders the slot in both viewport states simultaneously when the seam flips', async () => {
    setMatchesForTest(true)
    const { container } = render(
      <MemoryRouter>
        <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
          <SessionDetailShell
            data={makeDataWithSiblings()}
            components={makeEmptyShellComponents()}
          />
        </ProjectProvider>
      </MemoryRouter>,
    )

    expect(container.querySelector('[data-testid="session-sibling-navigation-slot"]')).toBeNull()
    const sidebarOnWide = container.querySelector('[data-testid="session-sibling-sidebar"]')
    expect(sidebarOnWide).not.toBeNull()

    await act(async () => {
      setMatchesForTest(false)
    })

    const slotOnNarrow = container.querySelector('[data-testid="session-sibling-navigation-slot"]') as HTMLElement | null
    expect(slotOnNarrow).not.toBeNull()
    expect(slotOnNarrow!.getAttribute('data-viewport')).toBe('narrow')

    await act(async () => {
      setMatchesForTest(true)
    })

    expect(container.querySelector('[data-testid="session-sibling-navigation-slot"]')).toBeNull()
  })
})
