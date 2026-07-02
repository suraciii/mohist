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

function archivedDoneIssue(overrides: Record<string, unknown> = {}) {
  return {
    id: 'issue-264',
    number: 264,
    title: 'Archive preserves workflow run history',
    body: 'Issue body content.',
    status: 'done' as const,
    workflowStage: 'done' as const,
    workflowStatus: 'completed',
    workflowRunId: 'wr_6a87cd36464a455a844cf9fad72f736e',
    archivedAt: '2026-06-25T10:00:00Z',
    health: 'done' as const,
    projectId: 'proj-1',
    labels: {},
    createdAt: '2026-06-25T03:00:00Z',
    updatedAt: '2026-06-25T10:00:00Z',
    comments: [],
    isDraft: false,
    canStart: false,
    blocker: null,
    feedback: null,
    ...overrides,
  }
}

function activeDoneIssue(overrides: Record<string, unknown> = {}) {
  return {
    id: 'issue-264',
    number: 264,
    title: 'Issue completes normally without being archived',
    body: 'Issue body content.',
    status: 'done' as const,
    workflowStage: 'done' as const,
    workflowStatus: 'completed',
    workflowRunId: 'wr_non_archived_1',
    archivedAt: undefined,
    health: 'done' as const,
    projectId: 'proj-1',
    labels: {},
    createdAt: '2026-06-25T03:00:00Z',
    updatedAt: '2026-06-25T10:00:00Z',
    comments: [],
    isDraft: false,
    canStart: false,
    blocker: null,
    feedback: null,
    ...overrides,
  }
}

function makeCompletedTimeline(workflowRunId: string) {
  return {
    workflowRunId,
    status: 'completed',
    currentStage: 'done',
    pendingWork: null,
    stages: [
      { stage: 'plan' as const, status: 'completed' as const, order: 0, startedAt: '2026-06-25T03:30:00Z', completedAt: '2026-06-25T04:00:00Z', durationMs: 30 * 60 * 1000, tasks: [], checks: [], approval: null },
      { stage: 'build' as const, status: 'completed' as const, order: 1, startedAt: '2026-06-25T04:00:00Z', completedAt: '2026-06-25T05:30:00Z', durationMs: 90 * 60 * 1000, tasks: [], checks: [], approval: null },
      { stage: 'check' as const, status: 'completed' as const, order: 2, startedAt: '2026-06-25T05:30:00Z', completedAt: '2026-06-25T06:00:00Z', durationMs: 30 * 60 * 1000, tasks: [], checks: [], approval: null },
      { stage: 'integrate' as const, status: 'completed' as const, order: 3, startedAt: '2026-06-25T06:00:00Z', completedAt: '2026-06-25T07:00:00Z', durationMs: 60 * 60 * 1000, tasks: [], checks: [], approval: null },
      { stage: 'done' as const, status: 'completed' as const, order: 4, startedAt: '2026-06-25T07:00:00Z', completedAt: '2026-06-25T07:00:00Z', durationMs: 0, tasks: [], checks: [], approval: null },
    ],
    availableActions: [],
  }
}

describe('IssueDetailPage archived Done issue — header and visibility', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
    mockUseWorkspaceStatus.mockReturnValue({ data: undefined, isLoading: false })
    mockUseWorkflowTimeline.mockReturnValue({ data: makeCompletedTimeline('wr_6a87cd36464a455a844cf9fad72f736e') })
  })

  afterEach(() => {
    cleanup()
  })

  it('shows an Archived pill in the header for an archived Done issue', async () => {
    mockUseIssue.mockReturnValue({
      data: archivedDoneIssue(),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const pill = await waitFor(() => screen.getByTestId('archived-pill'))
    expect(pill).toHaveTextContent(/Archived/i)
    expect(pill).toHaveAttribute('data-archived-at', '2026-06-25T10:00:00Z')
  })

  it('does not show an Archived pill for a non-archived Done issue', async () => {
    mockUseIssue.mockReturnValue({
      data: activeDoneIssue(),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('issue-detail-header')).toBeTruthy())
    expect(screen.queryByTestId('archived-pill')).toBeNull()
    expect(screen.queryByTestId('archived-banner')).toBeNull()
  })

  it('shows an archived banner with the archivedAt timestamp and a preservation note', async () => {
    mockUseIssue.mockReturnValue({
      data: archivedDoneIssue(),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const banner = await waitFor(() => screen.getByTestId('archived-banner'))
    expect(banner).toHaveAttribute('data-archived-at', '2026-06-25T10:00:00Z')
    expect(banner.textContent ?? '').toMatch(/preserved/i)
  })

  it('routes the back link to the Archived list when the issue is archived', async () => {
    mockUseIssue.mockReturnValue({
      data: archivedDoneIssue(),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const backLink = await waitFor(() => screen.getByTestId('back-to-archived'))
    expect(backLink).toHaveTextContent(/Back to archived/i)
    expect(screen.queryByTestId('back-to-board')).toBeNull()
  })

  it('keeps the Back to board link for a non-archived Done issue', async () => {
    mockUseIssue.mockReturnValue({
      data: activeDoneIssue(),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const backLink = await waitFor(() => screen.getByTestId('back-to-board'))
    expect(backLink).toHaveTextContent(/Back to board/i)
    expect(screen.queryByTestId('back-to-archived')).toBeNull()
  })
})

describe('IssueDetailPage archived Done issue — preserved history rendering', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
    mockUseWorkspaceStatus.mockReturnValue({ data: undefined, isLoading: false })
    mockUseWorkflowTimeline.mockReturnValue({ data: makeCompletedTimeline('wr_6a87cd36464a455a844cf9fad72f736e') })
  })

  afterEach(() => {
    cleanup()
  })

  it('renders the workflow stage bar with the preserved workflowRunId timeline for an archived issue', async () => {
    mockUseIssue.mockReturnValue({
      data: archivedDoneIssue(),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const stageBar = await waitFor(() => screen.getByTestId('workflow-stage-bar'))
    expect(stageBar).toBeTruthy()
    for (const stage of ['Plan', 'Build', 'Check', 'Integrate']) {
      expect(within(stageBar).getByText(stage)).toBeTruthy()
    }
    expect(within(stageBar).queryByText(/^Done$/)).not.toBeTruthy()
  })

  it('renders the same stage-bar layout for archived and non-archived Done issues', async () => {
    mockUseIssue.mockReturnValue({
      data: archivedDoneIssue(),
      isLoading: false,
      isError: false,
    })

    const { container: archivedContainer } = renderPage()
    const archivedBar = await waitFor(() => within(archivedContainer).getByTestId('workflow-stage-bar'))
    const archivedStageLabels = within(archivedBar).getAllByText(/Plan|Build|Check|Integrate/).map((n) => n.textContent)

    cleanup()
    mockUseIssue.mockReturnValue({
      data: activeDoneIssue(),
      isLoading: false,
      isError: false,
    })
    mockUseWorkflowTimeline.mockReturnValue({ data: makeCompletedTimeline('wr_non_archived_1') })

    const { container: activeContainer } = renderPage()
    const activeBar = await waitFor(() => within(activeContainer).getByTestId('workflow-stage-bar'))
    const activeStageLabels = within(activeBar).getAllByText(/Plan|Build|Check|Integrate/).map((n) => n.textContent)

    expect(archivedStageLabels).toEqual(activeStageLabels)
    expect(within(archivedBar).queryByText(/^Done$/)).not.toBeTruthy()
    expect(within(activeBar).queryByText(/^Done$/)).not.toBeTruthy()
  })

  it('renders the workflow-run YAML card labelled as preserved history for an archived issue', async () => {
    mockUseIssue.mockReturnValue({
      data: archivedDoneIssue(),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const trigger = await waitFor(() => screen.getByTestId('active-run-yaml-trigger'))
    expect(trigger).toHaveAttribute('data-yaml-mode', 'archived')
    expect(trigger.textContent ?? '').toMatch(/Workflow run YAML/)
    expect(trigger.textContent ?? '').not.toMatch(/Active run YAML/)
  })

  it('keeps the workflow-run YAML card labelled as active for a non-archived issue', async () => {
    mockUseIssue.mockReturnValue({
      data: activeDoneIssue(),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const trigger = await waitFor(() => screen.getByTestId('active-run-yaml-trigger'))
    expect(trigger).toHaveAttribute('data-yaml-mode', 'active')
    expect(trigger.textContent ?? '').toMatch(/Active run YAML/)
  })

  it('mounts the workflow YAML card identically for archived and non-archived Done issues', async () => {
    mockUseIssue.mockReturnValue({
      data: archivedDoneIssue(),
      isLoading: false,
      isError: false,
    })

    const { container: archivedContainer } = renderPage()
    const archivedYaml = await waitFor(() => within(archivedContainer).getByTestId('active-run-yaml-trigger'))
    expect(archivedYaml).toBeTruthy()

    cleanup()
    mockUseIssue.mockReturnValue({
      data: activeDoneIssue(),
      isLoading: false,
      isError: false,
    })
    mockUseWorkflowTimeline.mockReturnValue({ data: makeCompletedTimeline('wr_non_archived_1') })

    const { container: activeContainer } = renderPage()
    const activeYaml = await waitFor(() => within(activeContainer).getByTestId('active-run-yaml-trigger'))
    expect(activeYaml).toBeTruthy()
  })

  it('renders the same Latest Artifacts panel area for archived and non-archived Done issues', async () => {
    mockUseIssue.mockReturnValue({
      data: archivedDoneIssue(),
      isLoading: false,
      isError: false,
    })

    const { container: archivedContainer } = renderPage()
    const archivedYaml = await waitFor(() => within(archivedContainer).getByTestId('active-run-yaml-trigger'))
    expect(archivedYaml).toBeTruthy()

    cleanup()
    mockUseIssue.mockReturnValue({
      data: activeDoneIssue(),
      isLoading: false,
      isError: false,
    })
    mockUseWorkflowTimeline.mockReturnValue({ data: makeCompletedTimeline('wr_non_archived_1') })

    const { container: activeContainer } = renderPage()
    const activeYaml = await waitFor(() => within(activeContainer).getByTestId('active-run-yaml-trigger'))
    expect(activeYaml).toBeTruthy()
  })

  it('renders the Comments section for an archived Done issue exactly like a non-archived one', async () => {
    mockUseIssue.mockReturnValue({
      data: archivedDoneIssue({
        comments: [
          {
            id: 'c1',
            issueId: 'issue-264',
            body: 'Archived comment preserved.',
            createdAt: '2026-06-25T08:00:00Z',
          },
        ],
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const comments = await waitFor(() => screen.getByTestId('comments-section'))
    expect(comments.textContent ?? '').toContain('Comments (1)')
    expect(comments.textContent ?? '').toContain('Archived comment preserved.')
  })
})

describe('IssueDetailPage archived Done issue — feedback and event sections preserved', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
    mockUseWorkspaceStatus.mockReturnValue({ data: undefined, isLoading: false })
  })

  afterEach(() => {
    cleanup()
  })

  it('renders the preserved feedback history for an archived issue when feedback and matching stage exist', async () => {
    mockUseIssue.mockReturnValue({
      data: archivedDoneIssue({
        workflowStage: 'done',
        feedback: [
          {
            id: 'fb-1',
            stage: 'done',
            body: 'Please tighten the error message wording.',
            status: 'resolved',
            createdAt: '2026-06-25T05:30:00Z',
            resolution: {
              resolvedAt: '2026-06-25T05:45:00Z',
              resolutionTaskId: 'apply-fb-1',
              resolutionSummary: 'Reworded error message.',
            },
          },
        ],
      }),
      isLoading: false,
      isError: false,
    })
    mockUseWorkflowTimeline.mockReturnValue({
      data: makeCompletedTimeline('wr_6a87cd36464a455a844cf9fad72f736e'),
    })

    renderPage()

    const feedback = await waitFor(() => screen.getByTestId('feedback-fb-1'))
    expect(feedback).toHaveTextContent(/Cycle 1/)
    expect(feedback).toHaveTextContent(/Please tighten the error message wording/)
    expect(feedback).toHaveTextContent(/Reworded error message/)
    expect(feedback).toHaveAttribute('data-feedback-status', 'resolved')
  })

  it('exposes the Activity entry so the merged timeline dialog is reachable on an archived issue', async () => {
    mockUseIssue.mockReturnValue({
      data: archivedDoneIssue(),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const activity = await waitFor(() => screen.getByTestId('activity-entry'))
    expect(activity).toHaveAttribute('aria-label', 'Activity')
  })
})

describe('IssueDetailPage archived Done issue — no active-workflow controls', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
    mockUseWorkspaceStatus.mockReturnValue({ data: undefined, isLoading: false })
    mockUseWorkflowTimeline.mockReturnValue({ data: makeCompletedTimeline('wr_6a87cd36464a455a844cf9fad72f736e') })
  })

  afterEach(() => {
    cleanup()
  })

  it('does not render an active Running pill for an archived issue even when no agent signal', async () => {
    mockUseIssue.mockReturnValue({
      data: archivedDoneIssue(),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('issue-detail-header')).toBeTruthy())
    expect(screen.queryByTestId('running-pill')).toBeNull()
  })

  it('does not render the Start, Stop Workflow, Retry, Rerun, Resume, or Force Stop controls for an archived issue', async () => {
    mockUseIssue.mockReturnValue({
      data: archivedDoneIssue({
        recovery: {
          currentWorkItem: { type: 'task' as const, id: 't-1', title: 'historical' },
          latestAttemptState: 'completed',
          workflowSummaryState: 'completed',
          allowedActions: ['inspect'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('issue-detail-right-rail')).toBeTruthy())
    expect(screen.queryByTestId('start-button')).toBeNull()
    expect(screen.queryByTestId('mark-ready-button')).toBeNull()
    expect(screen.queryByTestId('start-readiness')).toBeNull()
    expect(screen.queryByText(/^Force Stop$/i)).toBeNull()
    expect(screen.queryByText(/^Stop Workflow$/i)).toBeNull()
    expect(screen.queryByText(/^Confirm Stop$/i)).toBeNull()
    expect(screen.queryByRole('button', { name: /^Retry$/ })).toBeNull()
    expect(screen.queryByRole('button', { name: /^Resume$/ })).toBeNull()
    expect(screen.queryByRole('button', { name: /^Rerun Stage$/ })).toBeNull()
    expect(screen.queryByRole('button', { name: /^Start$/ })).toBeNull()
  })

  it('shows an explanatory note in the Actions card instead of any controls for an archived issue', async () => {
    mockUseIssue.mockReturnValue({
      data: archivedDoneIssue(),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const note = await waitFor(() => screen.getByTestId('archived-actions-note'))
    expect(note.textContent ?? '').toMatch(/archived/i)
    expect(note.textContent ?? '').toMatch(/start, stop, retry, rerun, resume/i)
  })

  it('still allows the non-archived Done issue to render its Actions card empty of active-workflow controls too', async () => {
    mockUseIssue.mockReturnValue({
      data: activeDoneIssue(),
      isLoading: false,
      isError: false,
    })

    const { container } = renderPage()

    await waitFor(() => expect(screen.getByTestId('issue-detail-right-rail')).toBeTruthy())
    expect(screen.queryByTestId('archived-actions-note')).toBeNull()
    expect(container.querySelector('[data-testid="start-button"]')).toBeNull()
    expect(container.querySelector('[data-testid="mark-ready-button"]')).toBeNull()
  })
})

describe('IssueDetailPage archived Done issue — workflow status reflects Done state', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
    mockUseWorkspaceStatus.mockReturnValue({ data: undefined, isLoading: false })
  })

  afterEach(() => {
    cleanup()
  })

  it('renders the Done health pill for an archived Done issue and never the running one', async () => {
    mockUseIssue.mockReturnValue({
      data: archivedDoneIssue(),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const health = await waitFor(() => screen.getByTestId('health-pill'))
    expect(health.textContent ?? '').toMatch(/Done/i)
  })

  it('renders the runtime-decision surface with summary=done for an archived Done issue', async () => {
    mockUseIssue.mockReturnValue({
      data: archivedDoneIssue(),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    expect(surface.dataset.summary).toBe('done')
  })
})
