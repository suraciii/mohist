// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import { IssueDetailPage } from './IssueDetailPage'

const mockUseNavigate = vi.fn()

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>()
  return {
    ...actual,
    useNavigate: () => mockUseNavigate,
  }
})

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

function makeIssue(overrides: Record<string, unknown> = {}) {
  return {
    id: 'issue-1',
    number: 14,
    title: 'Test Issue',
    body: '',
    status: 'backlog',
    health: 'active',
    projectId: 'proj-1',
    labels: {},
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    comments: [],
    ...overrides,
  }
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

describe('IssueDetailPage status-header — three-tier anchors', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
    mockUseWorkspaceStatus.mockReturnValue({ data: undefined, isLoading: false })
  })

  afterEach(() => {
    cleanup()
  })

  it('renders the three tier containers with stable anchors', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue(),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const headerTier = await waitFor(() => screen.getByTestId('status-header-tier'))
    const readingFlow = await waitFor(() => screen.getByTestId('reading-flow'))
    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))

    expect(headerTier.contains(screen.getByTestId('status-headline'))).toBe(true)
    expect(headerTier.contains(screen.getByTestId('runtime-decision-surface'))).toBe(true)

    expect(referenceRail.className).toContain('space-y-6')
    expect(readingFlow.className).toContain('space-y-8')
  })
})

describe('IssueDetailPage status-header — stickiness of StatusHeadline', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
    mockUseWorkspaceStatus.mockReturnValue({ data: undefined, isLoading: false })
  })

  afterEach(() => {
    cleanup()
  })

  it('pins the StatusHeadline as the only sticky element at the top of the scroll container', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        status: 'in_progress',
        workflowStage: 'build',
        workflowStatus: 'running',
        health: 'active',
        recovery: {
          currentWorkItem: { type: 'task', id: 't1', title: 'Build decision surface' },
          latestAttemptState: 'running',
          workflowSummaryState: 'running',
          allowedActions: ['stop', 'inspect'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const headline = await waitFor(() => screen.getByTestId('status-headline'))
    expect(headline.dataset.sticky).toBe('true')
    expect(headline.className).toContain('sticky')
    expect(headline.className).toContain('top-0')

    const scrollContainer = screen.getByTestId('issue-detail-page-container')
    const firstChild = scrollContainer.firstElementChild
    expect(firstChild).toBeTruthy()
    const firstInteractive = firstChild?.querySelector('[data-testid="status-headline"]')
    expect(firstInteractive).toBe(headline)

    const stickyElements = Array.from(scrollContainer.querySelectorAll('[data-sticky="true"]'))
    expect(stickyElements).toHaveLength(1)
    expect(stickyElements[0]).toBe(headline)
  })

  it('does not pin the runtime decision surface (only the headline is sticky)', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        status: 'in_progress',
        workflowStage: 'build',
        workflowStatus: 'running',
        health: 'active',
        recovery: {
          currentWorkItem: { type: 'task', id: 't1', title: 'Build decision surface' },
          latestAttemptState: 'running',
          workflowSummaryState: 'running',
          allowedActions: ['stop', 'inspect'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    expect(surface.dataset.sticky ?? 'false').toBe('false')
    expect(surface.className).not.toContain('sticky')
  })
})

describe('IssueDetailPage status-header — single glanceable region', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
    mockUseWorkspaceStatus.mockReturnValue({ data: undefined, isLoading: false })
  })

  afterEach(() => {
    cleanup()
  })

  it('aggregates situation, stage, progress, and current task together in one region', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        status: 'in_progress',
        workflowStage: 'build',
        workflowStatus: 'running',
        health: 'active',
        workflowStageProgress: {
          stage: 'build',
          total: 5,
          completed: 2,
          running: 1,
          failed: 0,
          currentTaskTitle: 'Build decision surface',
        },
        recovery: {
          currentWorkItem: { type: 'task', id: 't1', title: 'Build decision surface' },
          latestAttemptState: 'running',
          workflowSummaryState: 'running',
          allowedActions: ['stop'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const headline = await waitFor(() => screen.getByTestId('status-headline'))
    expect(headline.contains(screen.getByTestId('status-headline-summary'))).toBe(true)
    expect(headline.contains(screen.getByTestId('status-headline-stage-progress'))).toBe(true)
    expect(headline.contains(screen.getByTestId('status-headline-current-task'))).toBe(true)

    const stage = screen.getByTestId('status-headline-stage-progress')
    expect(stage.dataset.stage).toBe('build')
    expect(stage.textContent).toMatch(/2\s*\/\s*5/)

    const task = screen.getByTestId('status-headline-current-task')
    expect(task.textContent).toContain('Build decision surface')
  })

  it('shows the situation alone without fabricating a stage or progress figure when no stage/progress exists (backlog)', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        status: 'backlog',
        workflowStage: null,
        workflowStatus: null,
        workflowRunId: null,
        health: 'active',
        blocker: { kind: 'draft' },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const headline = await waitFor(() => screen.getByTestId('status-headline'))
    expect(headline.contains(screen.getByTestId('status-headline-summary'))).toBe(true)
    expect(screen.queryByTestId('status-headline-stage-progress')).toBeNull()
    expect(screen.queryByTestId('status-headline-current-task')).toBeNull()
  })

  it('reflects the done situation with no active workflow controls for an archived done issue', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        id: 'issue-264',
        number: 264,
        status: 'done',
        workflowStage: 'done',
        workflowStatus: 'completed',
        archivedAt: '2026-06-25T10:00:00Z',
        health: 'done',
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const headline = await waitFor(() => screen.getByTestId('status-headline'))
    expect(headline.dataset.summary).toBe('done')
    expect(headline.textContent ?? '').toMatch(/Done/i)

    const surface = screen.getByTestId('runtime-decision-surface')
    expect(surface.dataset.summary).toBe('done')
    expect(within(surface).queryByTestId('runtime-action-start')).toBeNull()
    expect(within(surface).queryByTestId('runtime-action-stop')).toBeNull()
    expect(within(surface).queryByTestId('runtime-action-approve')).toBeNull()
    expect(within(surface).queryByTestId('runtime-action-retry')).toBeNull()
    expect(within(surface).queryByTestId('runtime-action-resume')).toBeNull()
    expect(within(surface).queryByTestId('runtime-action-rerun')).toBeNull()
  })
})

describe('IssueDetailPage status-header — adjudicated situation variants', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
    mockUseWorkspaceStatus.mockReturnValue({ data: undefined, isLoading: false })
  })

  afterEach(() => {
    cleanup()
  })

  it('reflects the running situation in the headline when the workflow is running', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        status: 'in_progress',
        workflowStage: 'build',
        workflowStatus: 'running',
        health: 'active',
        recovery: {
          currentWorkItem: { type: 'task', id: 't1', title: 'Build it' },
          latestAttemptState: 'running',
          workflowSummaryState: 'running',
          allowedActions: ['stop'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const headline = await waitFor(() => screen.getByTestId('status-headline'))
    expect(headline.dataset.summary).toBe('running')
    expect(headline.textContent ?? '').toMatch(/Running/i)
  })

  it('reflects the approval-required situation in the headline when approval is awaiting', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
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

    const headline = await waitFor(() => screen.getByTestId('status-headline'))
    expect(headline.dataset.summary).toBe('approval-required')
    expect(headline.textContent ?? '').toMatch(/Approval required/i)
  })

  it('reflects the done situation for an archived done issue', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        id: 'issue-264',
        number: 264,
        status: 'done',
        workflowStage: 'done',
        workflowStatus: 'completed',
        archivedAt: '2026-06-25T10:00:00Z',
        health: 'done',
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const headline = await waitFor(() => screen.getByTestId('status-headline'))
    expect(headline.dataset.summary).toBe('done')
  })
})

describe('IssueDetailPage status-header — single-badge invariant', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
    mockUseWorkspaceStatus.mockReturnValue({ data: undefined, isLoading: false })
  })

  afterEach(() => {
    cleanup()
  })

  it('renders exactly one runtime status pill in the identity row', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        status: 'in_progress',
        workflowStage: 'build',
        workflowStatus: 'running',
        health: 'active',
        priority: 'p1',
        recovery: {
          currentWorkItem: { type: 'task', id: 't1', title: 'Build it' },
          latestAttemptState: 'running',
          workflowSummaryState: 'running',
          allowedActions: ['stop'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const runtimeGroup = await waitFor(() => screen.getByTestId('status-badges-runtime'))
    const pills = within(runtimeGroup).getAllByTestId('runtime-status-pill')
    expect(pills).toHaveLength(1)
    expect(pills[0]).toHaveAttribute('data-summary', 'running')
  })

  it('does not render a duplicate runtime summary label or icon row inside the runtime decision surface', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        status: 'in_progress',
        workflowStage: 'build',
        workflowStatus: 'running',
        health: 'active',
        recovery: {
          currentWorkItem: { type: 'task', id: 't1', title: 'Build it' },
          latestAttemptState: 'running',
          workflowSummaryState: 'running',
          allowedActions: ['stop'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    expect(within(surface).queryByTestId('runtime-summary-label')).toBeNull()
    expect(within(surface).queryByTestId('runtime-summary-running')).toBeNull()
    expect(within(surface).queryByTestId('runtime-current-task')).toBeNull()

    const page = screen.getByTestId('issue-detail-page-container')
    const allPills = page.querySelectorAll('[data-testid="runtime-status-pill"]')
    expect(allPills).toHaveLength(1)
  })
})

describe('IssueDetailPage status-header — action surface anchoring', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
    mockUseWorkspaceStatus.mockReturnValue({ data: undefined, isLoading: false })
  })

  afterEach(() => {
    cleanup()
  })

  it('anchors all seven runtime actions inside the status-header tier, not the reading flow or reference rail', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        status: 'in_progress',
        workflowStage: 'build',
        workflowStatus: 'running',
        health: 'active',
        recovery: {
          currentWorkItem: { type: 'task', id: 't1', title: 'Build it' },
          latestAttemptState: 'running',
          workflowSummaryState: 'running',
          allowedActions: ['stop', 'retry', 'rerun', 'resume'],
        },
        approvalState: {
          status: 'awaiting',
          stage: 'check',
          requestedAt: '2026-01-01T00:00:00.000Z',
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    expect(surface.contains(screen.getByTestId('runtime-actions'))).toBe(true)

    const headerTier = screen.getByTestId('status-header-tier')
    expect(headerTier.contains(surface)).toBe(true)

    const readingFlow = screen.getByTestId('reading-flow')
    const referenceRail = screen.getByTestId('reference-rail')

    expect(readingFlow.contains(surface)).toBe(false)
    expect(referenceRail.contains(surface)).toBe(false)

    for (const kind of ['approve', 'send-back', 'retry', 'resume', 'rerun', 'stop', 'start']) {
      const actionIds = Array.from(screen.getByTestId('issue-detail-page-container').querySelectorAll(`[data-testid="runtime-action-${kind}"]`))
      expect(actionIds.length).toBeLessThanOrEqual(1)
      for (const node of actionIds) {
        expect(headerTier.contains(node)).toBe(true)
        expect(readingFlow.contains(node)).toBe(false)
        expect(referenceRail.contains(node)).toBe(false)
      }
    }
  })
})

describe('IssueDetailPage status-header — heaviest visual weight', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
    mockUseWorkspaceStatus.mockReturnValue({ data: undefined, isLoading: false })
  })

  afterEach(() => {
    cleanup()
  })

  it('carries the sticky + fill + icon + border combination uniquely in the headline', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        status: 'in_progress',
        workflowStage: 'build',
        workflowStatus: 'running',
        health: 'active',
        recovery: {
          currentWorkItem: { type: 'task', id: 't1', title: 'Build it' },
          latestAttemptState: 'running',
          workflowSummaryState: 'running',
          allowedActions: ['stop'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const headline = await waitFor(() => screen.getByTestId('status-headline'))
    expect(headline.dataset.sticky).toBe('true')
    expect(headline.className).toContain('sticky')
    expect(headline.className).toMatch(/bg-(info|warning|danger|success)-subtle/)
    expect(headline.querySelector('svg')).toBeTruthy()
    expect(headline.className).toMatch(/border-/)

    const readingFlow = screen.getByTestId('reading-flow')
    const referenceRail = screen.getByTestId('reference-rail')

    expect(readingFlow.querySelector('[data-sticky="true"]')).toBeNull()
    expect(referenceRail.querySelector('[data-sticky="true"]')).toBeNull()
  })
})