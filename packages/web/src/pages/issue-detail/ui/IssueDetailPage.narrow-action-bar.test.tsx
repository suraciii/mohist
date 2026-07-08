// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import { IssueDetailPage } from './IssueDetailPage'

const mockUseIssueDiff = vi.fn()
const mockUseIssueCommits = vi.fn()
const mockUseWorkflowTimeline = vi.fn()
const mockUseWorkflowYaml = vi.fn()
const mockUseAgentStatus = vi.fn()
const mockUseIssue = vi.fn()
const mockUseWorkspaceStatus = vi.fn()

vi.mock('../../../entities/issue', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/issue')>()
  return {
    ...actual,
    useIssue: (...args: unknown[]) => mockUseIssue(...args),
    useIssueDiff: (...args: unknown[]) => mockUseIssueDiff(...args),
    useIssueCommits: (...args: unknown[]) => mockUseIssueCommits(...args),
    useWorkflowTimeline: (...args: unknown[]) => mockUseWorkflowTimeline(...args),
    useWorkflowYaml: (...args: unknown[]) => mockUseWorkflowYaml(...args),
    useWorkspaceStatus: (...args: unknown[]) => mockUseWorkspaceStatus(...args),
    useIssueEvents: () => ({ data: undefined, isLoading: false }),
    getIssueWorkflowVariables: vi.fn(() => Promise.resolve({ vars: {}, stages: {} })),
    patchIssueWorkflowDefinitionVar: vi.fn(() => Promise.resolve({ vars: {}, stages: {} })),
    patchIssueWorkflowStageDefinitionVar: vi.fn(() => Promise.resolve({ vars: {}, stages: {} })),
  }
})

vi.mock('../../../entities/settings', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/settings')>()
  return {
    ...actual,
    useWorkflowProfiles: () => ({ data: [] }),
    useAvailableModelIds: () => ({ data: [] }),
    useOpencodeModel: () => ({ data: null }),
    useModelVariants: () => ({ data: [] }),
    useEffectiveDefaultWorkflowProfile: () => ({ data: null }),
  }
})

vi.mock('../../../widgets/issue-event-timeline/ui/EventTimelinePanel', () => ({
  EventTimelinePanel: vi.fn((props: { issueNumber: number; issueId?: string | null; workflowStatus?: string | null; enabled?: boolean }) => (
    <div
      data-testid="event-timeline-panel-mock"
      data-issue-number={props.issueNumber}
      data-issue-id={props.issueId ?? ''}
      data-workflow-status={props.workflowStatus ?? ''}
      data-enabled={props.enabled === undefined ? '' : String(props.enabled)}
    />
  )),
}))

vi.mock('../../../entities/agent', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/agent')>()
  return {
    ...actual,
    useAgentStatus: (...args: unknown[]) => mockUseAgentStatus(...args),
  }
})

const projects: Project[] = [
  {
    id: 'proj-1',
    name: 'Project 1',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    repositories: [],
  },
]

function mockMatchMedia(narrow: boolean, width = narrow ? 375 : 1280) {
  const mql = {
    matches: narrow,
    media: '(max-width: 1023.98px)',
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    addListener: vi.fn(),
    removeListener: vi.fn(),
    dispatchEvent: vi.fn(),
    onchange: null,
  }
  vi.stubGlobal('matchMedia', vi.fn(() => mql))
  Object.defineProperty(window, 'innerWidth', { configurable: true, value: width })
}

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/issues/14']}>
        <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
          <Routes>
            <Route path="/issues/:number" element={<IssueDetailPage />} />
          </Routes>
        </ProjectProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

function baseIssue(overrides: Record<string, unknown> = {}) {
  return {
    id: 'issue-base',
    number: 14,
    title: 'Test issue',
    body: '',
    status: 'backlog',
    workflowStage: null,
    workflowStatus: null,
    health: 'active',
    projectId: 'proj-1',
    labels: {},
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    comments: [],
    isDraft: false,
    canStart: true,
    blocker: null,
    ...overrides,
  }
}

describe('IssueDetailPage narrow-viewport MobileActionBar matrix', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockMatchMedia(true)
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
    mockUseWorkspaceStatus.mockReturnValue({ data: undefined, isLoading: false })
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
  })

  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
  })

  it('running surfaces Stop in the bottom bar and strips RuntimeDecisionSurface from the header tier', async () => {
    mockUseIssue.mockReturnValue({
      data: baseIssue({
        status: 'in_progress',
        workflowStage: 'build',
        workflowStatus: 'running',
        health: 'active',
        recovery: {
          currentWorkItem: { type: 'task', id: 't1', title: 'Build surface' },
          latestAttemptState: 'running',
          workflowSummaryState: 'running',
          allowedActions: ['stop'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('status-headline')).toBeTruthy())

    const headerTier = screen.getByTestId('status-header-tier')
    expect(headerTier.querySelector('[data-testid="runtime-decision-surface"]')).toBeNull()
    expect(headerTier.querySelector('[data-testid="runtime-action-stop"]')).toBeNull()
    expect(headerTier.querySelector('[data-testid="runtime-stop-confirmation-copy"]')).toBeNull()

    const bar = screen.getByTestId('mobile-action-bar')
    expect(bar.dataset.actionKind).toBe('stop')
    expect(bar.dataset.summary).toBe('running')
    expect(within(bar).getByTestId('mobile-action-stop')).toBeInTheDocument()
  })

  it('approval-required surfaces Approve in the bottom bar', async () => {
    mockUseIssue.mockReturnValue({
      data: baseIssue({
        status: 'in_progress',
        workflowStage: 'check',
        health: 'paused',
        approvalState: {
          status: 'awaiting',
          stage: 'check',
          requestedAt: '2026-01-01T00:00:00.000Z',
        },
        recovery: {
          currentWorkItem: null,
          latestAttemptState: null,
          workflowSummaryState: 'awaiting-approval',
          allowedActions: ['approve', 'reject'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('status-headline')).toBeTruthy())

    const headerTier = screen.getByTestId('status-header-tier')
    expect(headerTier.querySelector('[data-testid="runtime-decision-surface"]')).toBeNull()

    const bar = screen.getByTestId('mobile-action-bar')
    expect(bar.dataset.actionKind).toBe('approve')
    expect(within(bar).getByTestId('mobile-action-approve')).toBeInTheDocument()
  })

  it('failed surfaces a primary action (Retry) in the bottom bar', async () => {
    mockUseIssue.mockReturnValue({
      data: baseIssue({
        status: 'in_progress',
        workflowStage: 'build',
        workflowStatus: 'failed',
        health: 'active',
        recovery: {
          currentWorkItem: null,
          latestAttemptState: 'failed',
          workflowSummaryState: 'failed',
          allowedActions: ['retry', 'resume', 'rerun', 'start', 'stop'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('status-headline')).toBeTruthy())

    const headerTier = screen.getByTestId('status-header-tier')
    expect(headerTier.querySelector('[data-testid="runtime-decision-surface"]')).toBeNull()

    const bar = screen.getByTestId('mobile-action-bar')
    expect(bar.dataset.summary).toBe('failed')
    expect(bar.dataset.actionKind).toBe('retry')
    expect(within(bar).getByTestId('mobile-action-retry')).toBeInTheDocument()
  })

  it('blocked surfaces a primary action (Retry) in the bottom bar', async () => {
    mockUseIssue.mockReturnValue({
      data: baseIssue({
        status: 'in_progress',
        workflowStage: 'build',
        workflowStatus: 'interrupted',
        health: 'interrupted',
        blockedReason: 'Workflow was interrupted.',
        recovery: {
          currentWorkItem: null,
          latestAttemptState: 'interrupted',
          workflowSummaryState: 'interrupted',
          allowedActions: ['retry', 'resume', 'rerun', 'stop'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('status-headline')).toBeTruthy())

    const bar = screen.getByTestId('mobile-action-bar')
    expect(bar.dataset.summary).toBe('blocked')
    expect(bar.dataset.actionKind).toBe('retry')
    expect(within(bar).getByTestId('mobile-action-retry')).toBeInTheDocument()
  })

  it('queued backlog (ready to start) surfaces Start in the bottom bar', async () => {
    mockUseIssue.mockReturnValue({
      data: baseIssue({
        status: 'backlog',
        workflowStage: null,
        workflowStatus: null,
        workflowRunId: null,
        health: 'active',
        canStart: true,
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('status-headline')).toBeTruthy())

    const bar = screen.getByTestId('mobile-action-bar')
    expect(bar.dataset.actionKind).toBe('start')
    expect(within(bar).getByTestId('mobile-action-start')).toBeInTheDocument()
  })

  it('draft backlog surfaces disabled Start with the draft blocker reason', async () => {
    mockUseIssue.mockReturnValue({
      data: baseIssue({
        status: 'backlog',
        workflowStage: null,
        workflowStatus: null,
        workflowRunId: null,
        health: 'active',
        isDraft: true,
        canStart: true,
        blocker: { kind: 'draft' },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('mobile-action-bar')).toBeTruthy())

    const start = screen.getByTestId('mobile-action-start')
    expect(start).toBeDisabled()
    expect(start).toHaveTextContent('Start')
    expect(start).toHaveAttribute('title', 'Issue is still a draft. Mark it ready before starting.')
  })

  it('prerequisite-blocked backlog surfaces disabled Start with the prerequisite reason', async () => {
    mockUseIssue.mockReturnValue({
      data: baseIssue({
        status: 'backlog',
        workflowStage: null,
        workflowStatus: null,
        workflowRunId: null,
        health: 'active',
        canStart: true,
        blocker: {
          kind: 'waiting-for',
          issue: { number: 9, title: 'Prepare spec' },
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('mobile-action-bar')).toBeTruthy())

    const start = screen.getByTestId('mobile-action-start')
    expect(start).toBeDisabled()
    expect(start).toHaveAttribute('title', 'Waiting for #9 Prepare spec')
  })

  it('runner-blocked backlog surfaces disabled Start with the runner reason', async () => {
    mockUseAgentStatus.mockReturnValue({
      data: {
        activeAgents: [],
        capacity: { max: 1 },
        runnerAvailable: false,
        runnerMessage: 'No runner is connected. Start a runner before this issue can run.',
      },
    })
    mockUseIssue.mockReturnValue({
      data: baseIssue({
        status: 'backlog',
        workflowStage: null,
        workflowStatus: null,
        workflowRunId: null,
        health: 'active',
        canStart: true,
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('mobile-action-bar')).toBeTruthy())

    const start = screen.getByTestId('mobile-action-start')
    expect(start).toBeDisabled()
    expect(start).toHaveAttribute('title', 'No runner is connected. Start a runner before this issue can run.')
  })

  it('failed state preserves the Start new workflow primary label', async () => {
    mockUseIssue.mockReturnValue({
      data: baseIssue({
        status: 'in_progress',
        workflowStage: 'build',
        workflowStatus: 'failed',
        health: 'active',
        recovery: {
          currentWorkItem: null,
          latestAttemptState: 'failed',
          workflowSummaryState: 'failed',
          allowedActions: ['start'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('mobile-action-bar')).toBeTruthy())

    const bar = screen.getByTestId('mobile-action-bar')
    expect(bar.dataset.summary).toBe('failed')
    expect(bar.dataset.actionKind).toBe('start')
    expect(within(bar).getByTestId('mobile-action-start')).toHaveTextContent('Start new workflow')
  })

  it('done state renders no bottom bar and strips RuntimeDecisionSurface from the header tier', async () => {
    mockUseIssue.mockReturnValue({
      data: baseIssue({
        status: 'done',
        workflowStage: 'done',
        workflowStatus: 'done',
        health: 'done',
        recovery: {
          currentWorkItem: null,
          latestAttemptState: 'completed',
          workflowSummaryState: 'completed',
          allowedActions: [],
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('status-headline')).toBeTruthy())

    const headerTier = screen.getByTestId('status-header-tier')
    expect(headerTier.querySelector('[data-testid="runtime-decision-surface"]')).toBeNull()
    expect(screen.queryByTestId('mobile-action-bar')).toBeNull()
  })

  it('archived state renders no bottom bar and strips RuntimeDecisionSurface from the header tier', async () => {
    mockUseIssue.mockReturnValue({
      data: baseIssue({
        status: 'done',
        workflowStage: 'done',
        workflowStatus: 'done',
        health: 'done',
        archivedAt: '2026-01-02T00:00:00Z',
        recovery: {
          currentWorkItem: null,
          latestAttemptState: 'completed',
          workflowSummaryState: 'completed',
          allowedActions: [],
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('status-headline')).toBeTruthy())

    const headerTier = screen.getByTestId('status-header-tier')
    expect(headerTier.querySelector('[data-testid="runtime-decision-surface"]')).toBeNull()
    expect(screen.queryByTestId('mobile-action-bar')).toBeNull()
  })

  it('narrow viewport reserves bottom padding only when a primary action exists (no-padding when no bar)', async () => {
    mockUseIssue.mockReturnValue({
      data: baseIssue({
        status: 'done',
        workflowStage: 'done',
        workflowStatus: 'done',
        health: 'done',
        recovery: {
          currentWorkItem: null,
          latestAttemptState: 'completed',
          workflowSummaryState: 'completed',
          allowedActions: [],
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('status-headline')).toBeTruthy())

    const column = screen.getByTestId('issue-detail-content-column')
    expect(column.dataset.barReserved).toBe('false')
    expect(column.className).not.toContain('pb-[calc(8rem')
    expect(screen.queryByTestId('mobile-action-bar')).toBeNull()
  })

  it('narrow viewport reserves extra bottom padding when a primary action bar is present', async () => {
    mockUseIssue.mockReturnValue({
      data: baseIssue({
        status: 'in_progress',
        workflowStage: 'build',
        workflowStatus: 'running',
        health: 'active',
        recovery: {
          currentWorkItem: { type: 'task', id: 't1', title: 'Build' },
          latestAttemptState: 'running',
          workflowSummaryState: 'running',
          allowedActions: ['stop'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('mobile-action-bar')).toBeTruthy())

    const column = screen.getByTestId('issue-detail-content-column')
    expect(column.dataset.barReserved).toBe('true')
    expect(column.className).toContain('pb-[calc(8rem')
  })

  it('stop on narrow opens the bottom-sliding drawer and the sticky StatusHeadline remains visible', async () => {
    mockUseIssue.mockReturnValue({
      data: baseIssue({
        status: 'in_progress',
        workflowStage: 'build',
        workflowStatus: 'running',
        health: 'active',
        recovery: {
          currentWorkItem: { type: 'task', id: 't1', title: 'Build surface' },
          latestAttemptState: 'running',
          workflowSummaryState: 'running',
          allowedActions: ['stop', 'force-stop'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('mobile-action-bar')).toBeTruthy())

    fireEvent.click(screen.getByTestId('mobile-action-stop'))

    const drawer = await waitFor(() => screen.getByTestId('confirmation-drawer'))
    expect(drawer).toHaveAttribute('role', 'dialog')
    expect(drawer).toHaveAttribute('aria-modal', 'true')
    expect(drawer.className).toMatch(/bottom-0/)
    expect(screen.getByTestId('mobile-stop-confirmation')).toBeInTheDocument()
    expect(screen.getByTestId('mobile-confirmation-body')).toHaveTextContent('preserve progress')

    const headline = screen.getByTestId('status-headline')
    expect(headline).toHaveAttribute('data-sticky', 'true')
    expect(headline.className).toMatch(/\bz-20\b/)
    expect(drawer.className).toMatch(/\bz-50\b/)

    fireEvent.keyDown(document, { key: 'Escape' })
    await waitFor(() => expect(screen.queryByTestId('confirmation-drawer')).toBeNull())
  })
})

describe('IssueDetailPage narrow-viewport 768-1024px band (flush-bottom bar)', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
    mockUseWorkspaceStatus.mockReturnValue({ data: undefined, isLoading: false })
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
  })

  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
  })

  it('at ~900px (narrow page, no global nav) the bar anchors flush to the bottom with no nav-offset', async () => {
    mockMatchMedia(true, 900)

    mockUseIssue.mockReturnValue({
      data: baseIssue({
        status: 'in_progress',
        workflowStage: 'build',
        workflowStatus: 'running',
        health: 'active',
        recovery: {
          currentWorkItem: { type: 'task', id: 't1', title: 'Build surface' },
          latestAttemptState: 'running',
          workflowSummaryState: 'running',
          allowedActions: ['stop'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('mobile-action-bar')).toBeTruthy())

    const bar = screen.getByTestId('mobile-action-bar')
    expect(bar.className).toMatch(/\bbottom-\[calc\(/)

    expect(bar.className).toMatch(/\bmd:bottom-0\b/)
    expect(bar.className).not.toMatch(/md:bottom-\[calc\(/)

    const narrowMatch = /\bbottom-\[calc\(([\s\S]+?)\)\]/.exec(bar.className)
    expect(narrowMatch).toBeTruthy()
    expect(narrowMatch?.[1]).toMatch(/3\.5rem/)
    expect(narrowMatch?.[1]).toMatch(/env\(safe-area-inset-bottom\)/)

    expect(bar.className).toMatch(/\bfixed\b/)
    expect(bar.className).toMatch(/\binset-x-0\b/)
  })

  it('does not reserve padding for the global nav on the content column when the bar is present', async () => {
    mockMatchMedia(true, 900)

    mockUseIssue.mockReturnValue({
      data: baseIssue({
        status: 'in_progress',
        workflowStage: 'build',
        workflowStatus: 'running',
        health: 'active',
        recovery: {
          currentWorkItem: { type: 'task', id: 't1', title: 'Build surface' },
          latestAttemptState: 'running',
          workflowSummaryState: 'running',
          allowedActions: ['stop'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('mobile-action-bar')).toBeTruthy())

    const column = screen.getByTestId('issue-detail-content-column')
    expect(column.dataset.barReserved).toBe('true')
    expect(column.className).toContain('pb-[calc(8rem')
  })
})

describe('IssueDetailPage desktop viewport restores RuntimeDecisionSurface and no mobile-only elements', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockMatchMedia(false)
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
    mockUseWorkspaceStatus.mockReturnValue({ data: undefined, isLoading: false })
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
  })

  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
  })

  it('desktop renders RuntimeDecisionSurface in the header tier and neither MobileActionBar nor ConfirmationDrawer in the DOM', async () => {
    mockUseIssue.mockReturnValue({
      data: baseIssue({
        status: 'in_progress',
        workflowStage: 'build',
        workflowStatus: 'running',
        health: 'active',
        recovery: {
          currentWorkItem: { type: 'task', id: 't1', title: 'Build surface' },
          latestAttemptState: 'running',
          workflowSummaryState: 'running',
          allowedActions: ['stop'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('runtime-decision-surface')).toBeTruthy())

    const headerTier = screen.getByTestId('status-header-tier')
    expect(headerTier.contains(screen.getByTestId('runtime-decision-surface'))).toBe(true)
    expect(headerTier.contains(screen.getByTestId('runtime-action-stop'))).toBe(true)

    expect(screen.queryByTestId('mobile-action-bar')).toBeNull()
    expect(screen.queryByTestId('confirmation-drawer')).toBeNull()

    fireEvent.click(screen.getByTestId('runtime-action-stop'))

    await waitFor(() => expect(screen.getByTestId('runtime-stop-confirmation-copy')).toBeTruthy())
    expect(screen.queryByTestId('confirmation-drawer')).toBeNull()
  })

  it('desktop does not reserve extra bottom padding for the bar', async () => {
    mockUseIssue.mockReturnValue({
      data: baseIssue({
        status: 'in_progress',
        workflowStage: 'build',
        workflowStatus: 'running',
        health: 'active',
        recovery: {
          currentWorkItem: { type: 'task', id: 't1', title: 'Build surface' },
          latestAttemptState: 'running',
          workflowSummaryState: 'running',
          allowedActions: ['stop'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('runtime-decision-surface')).toBeTruthy())

    const column = screen.getByTestId('issue-detail-content-column')
    expect(column.dataset.barReserved).toBe('false')
    expect(column.className).not.toContain('pb-[calc(8rem')
  })
})
