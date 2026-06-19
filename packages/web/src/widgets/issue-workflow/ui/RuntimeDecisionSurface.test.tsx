// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { BrowserRouter } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { RuntimeDecisionSurface } from './RuntimeDecisionSurface'
import {
  approveIssue,
  rejectIssue,
  retryIssue,
  resumeIssue,
  rerunIssue,
  startIssue,
  stopIssue,
  IssueHealth,
  IssueStatus,
  WorkflowStage,
  type Issue,
  type WorkflowTimeline,
} from '../../../entities/issue'

vi.mock('../../../entities/issue', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/issue')>()
  return {
    ...actual,
    approveIssue: vi.fn(),
    rejectIssue: vi.fn(),
    retryIssue: vi.fn(),
    resumeIssue: vi.fn(),
    rerunIssue: vi.fn(),
    startIssue: vi.fn(),
    stopIssue: vi.fn(),
  }
})

const mockedApprove = vi.mocked(approveIssue)
const mockedReject = vi.mocked(rejectIssue)
const mockedRetry = vi.mocked(retryIssue)
const mockedResume = vi.mocked(resumeIssue)
const mockedRerun = vi.mocked(rerunIssue)
const mockedStart = vi.mocked(startIssue)
const mockedStop = vi.mocked(stopIssue)

const projects = [
  {
    id: 'test-project',
    name: 'Test Project',
    createdAt: '2024-01-01T00:00:00.000Z',
    updatedAt: '2024-01-01T00:00:00.000Z',
    repositories: [{ name: 'main', gitUrl: 'https://example.com/test.git', baseBranch: 'main', isDefault: true }],
  },
]

function makeIssue(overrides: Partial<Issue> = {}): Issue {
  return {
    id: 'issue-1',
    number: 121,
    title: 'Test Issue',
    body: '',
    status: IssueStatus.InProgress,
    workflowStage: WorkflowStage.Build,
    workflowStatus: 'running',
    health: IssueHealth.Active,
    projectId: 'test-project',
    labels: {},
    createdAt: '2026-01-01T00:00:00.000Z',
    updatedAt: '2026-01-01T00:00:00.000Z',
    comments: [],
    isDraft: false,
    canStart: true,
    blocker: null,
    ...overrides,
  }
}

function renderSurface(props: React.ComponentProps<typeof RuntimeDecisionSurface>) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjects={projects} initialProjectId="test-project">
        <BrowserRouter>
          <RuntimeDecisionSurface {...props} />
        </BrowserRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('RuntimeDecisionSurface', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('renders a single running summary near the top with the current task/check named', () => {
    const issue = makeIssue({
      workflowStage: WorkflowStage.Build,
      health: IssueHealth.Active,
      recovery: {
        currentWorkItem: { type: 'task', id: 't1', title: 'Implement RuntimeDecisionSurface' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop', 'inspect'],
      },
    })
    const timeline: WorkflowTimeline = {
      workflowRunId: 'wr-1',
      status: 'Running',
      currentStage: WorkflowStage.Build,
      pendingWork: null,
      stages: [
        {
          stage: WorkflowStage.Build,
          status: 'running',
          order: 2,
          startedAt: null,
          completedAt: null,
          durationMs: null,
          tasks: [
            {
              id: 't1',
              title: 'Implement RuntimeDecisionSurface',
              uses: 'mohist/coder-agent',
              status: 'running',
              startedAt: null,
              completedAt: null,
              durationMs: null,
              attempts: 1,
              message: null,
            },
          ],
          checks: [],
          approval: null,
        },
      ],
      availableActions: [{ name: 'stop', label: 'Stop', target: null }],
    }

    renderSurface({
      issue,
      timeline,
      agentStatus: {
        running: true,
        issueId: 'issue-1',
        issueNumber: 121,
        activeAgents: [],
        capacity: { active: 0, max: 1 },
        runnerAvailable: true,
      },
      hasActiveAgent: true,
    })

    const surface = screen.getByTestId('runtime-decision-surface')
    expect(surface).toBeInTheDocument()
    expect(surface.dataset.summary).toBe('running')

    expect(within(surface).getByTestId('runtime-summary-label')).toHaveTextContent('Running')
    expect(within(surface).getByTestId('runtime-headline').textContent).toContain(
      'Implement RuntimeDecisionSurface',
    )
    expect(within(surface).getByTestId('runtime-current-task').textContent).toContain(
      'Implement RuntimeDecisionSurface',
    )
    expect(within(surface).getByTestId('runtime-next-action-body').textContent).toMatch(/no user action/i)

    const stopButton = within(surface).getByTestId('runtime-action-stop')
    expect(stopButton).toBeInTheDocument()
    expect(stopButton).not.toBeDisabled()

    expect(within(surface).queryByTestId('runtime-action-approve')).toBeNull()
    expect(within(surface).queryByTestId('runtime-action-send-back')).toBeNull()
  })

  it('renders a single approval-required summary with approve and send-back actions enabled when the projection allows them', () => {
    const issue = makeIssue({
      workflowStage: WorkflowStage.Check,
      health: IssueHealth.Paused,
      approvalState: {
        status: 'awaiting',
        stage: WorkflowStage.Check,
        requestedAt: '2026-01-01T00:00:00.000Z',
      },
      recovery: {
        currentWorkItem: { type: 'check', id: 'c1', title: 'Health check' },
        latestAttemptState: null,
        workflowSummaryState: 'awaiting-approval',
        allowedActions: ['approve', 'reject'],
      },
    })
    const timeline: WorkflowTimeline = {
      workflowRunId: 'wr-2',
      status: 'AwaitingApproval',
      currentStage: WorkflowStage.Check,
      pendingWork: null,
      stages: [
        {
          stage: WorkflowStage.Check,
          status: 'awaiting-approval',
          order: 3,
          startedAt: null,
          completedAt: null,
          durationMs: null,
          tasks: [],
          checks: [],
          approval: {
            status: 'awaiting',
            output: null,
            requestedAt: '2026-01-01T00:00:00.000Z',
            respondedAt: null,
          },
        },
      ],
      availableActions: [
        { name: 'approve', label: 'Approve', target: null },
        { name: 'reject', label: 'Send back', target: null },
      ],
    }

    renderSurface({
      issue,
      timeline,
      agentStatus: {
        running: false,
        issueId: null,
        issueNumber: null,
        activeAgents: [],
        capacity: { active: 0, max: 1 },
        runnerAvailable: true,
      },
    })

    const surface = screen.getByTestId('runtime-decision-surface')
    expect(surface.dataset.summary).toBe('approval-required')
    expect(within(surface).getByTestId('runtime-summary-label')).toHaveTextContent('Approval required')
    expect(within(surface).getByTestId('runtime-headline').textContent).toContain('Health check')

    const approve = within(surface).getByTestId('runtime-action-approve')
    const sendBack = within(surface).getByTestId('runtime-action-send-back')
    expect(approve).not.toBeDisabled()
    expect(sendBack).not.toBeDisabled()
  })

  it('renders a failed summary (not approval required) when a Check stage has a failed script/health verification', () => {
    const issue = makeIssue({
      workflowStage: WorkflowStage.Check,
      health: IssueHealth.Blocked,
      approvalState: {
        status: 'awaiting',
        stage: WorkflowStage.Check,
        requestedAt: '2026-01-01T00:00:00.000Z',
      },
      recovery: {
        currentWorkItem: null,
        latestAttemptState: 'failed',
        workflowSummaryState: 'waiting-for-recovery',
        allowedActions: ['retry', 'rerun'],
      },
    })
    const timeline: WorkflowTimeline = {
      workflowRunId: 'wr-3',
      status: 'Failed',
      currentStage: WorkflowStage.Check,
      pendingWork: null,
      stages: [
        {
          stage: WorkflowStage.Check,
          status: 'failed',
          order: 3,
          startedAt: null,
          completedAt: null,
          durationMs: null,
          tasks: [],
          checks: [
            {
              name: 'health',
              title: 'Typecheck',
              uses: null,
              status: 'failed',
              message: 'Typecheck failed',
              startedAt: null,
              completedAt: null,
              durationMs: null,
            },
          ],
          approval: null,
        },
      ],
      availableActions: [
        { name: 'retry', label: 'Retry', target: null },
        { name: 'rerun', label: 'Rerun', target: null },
      ],
    }

    renderSurface({ issue, timeline })

    const surface = screen.getByTestId('runtime-decision-surface')
    expect(surface.dataset.summary).toBe('failed')
    expect(within(surface).getByTestId('runtime-summary-label')).toHaveTextContent('Failed')
    expect(within(surface).getByTestId('runtime-current-task').textContent).toContain('Typecheck')
    expect(within(surface).getByTestId('runtime-action-retry')).not.toBeDisabled()
    expect(within(surface).queryByTestId('runtime-action-approve')).toBeNull()
  })

  it('disables approve and send-back when no projection allows them', () => {
    const issue = makeIssue({
      workflowStage: WorkflowStage.Plan,
      health: IssueHealth.Paused,
      approvalState: {
        status: 'awaiting',
        stage: WorkflowStage.Plan,
        requestedAt: '2026-01-01T00:00:00.000Z',
      },
      recovery: {
        currentWorkItem: null,
        latestAttemptState: null,
        workflowSummaryState: 'awaiting-approval',
        allowedActions: [],
      },
    })
    const timeline: WorkflowTimeline = {
      workflowRunId: 'wr-4',
      status: 'AwaitingApproval',
      currentStage: WorkflowStage.Plan,
      pendingWork: null,
      stages: [],
      availableActions: [],
    }

    renderSurface({ issue, timeline })

    const surface = screen.getByTestId('runtime-decision-surface')
    expect(surface.dataset.summary).toBe('approval-required')
    expect(within(surface).getByTestId('runtime-action-approve')).toBeDisabled()
    expect(within(surface).getByTestId('runtime-action-send-back')).toBeDisabled()
  })

  it('invokes approveIssue when the surface approve button is clicked', async () => {
    mockedApprove.mockResolvedValueOnce({ issue: makeIssue(), context: null, message: 'ok' })

    const issue = makeIssue({
      workflowStage: WorkflowStage.Check,
      health: IssueHealth.Paused,
      approvalState: {
        status: 'awaiting',
        stage: WorkflowStage.Check,
        requestedAt: '2026-01-01T00:00:00.000Z',
      },
      recovery: {
        currentWorkItem: null,
        latestAttemptState: null,
        workflowSummaryState: 'awaiting-approval',
        allowedActions: ['approve', 'reject'],
      },
    })
    const timeline: WorkflowTimeline = {
      workflowRunId: 'wr-5',
      status: 'AwaitingApproval',
      currentStage: WorkflowStage.Check,
      pendingWork: null,
      stages: [],
      availableActions: [{ name: 'approve', label: 'Approve', target: null }],
    }

    renderSurface({ issue, timeline })

    const approve = screen.getByTestId('runtime-action-approve')
    approve.click()

    await waitFor(() => expect(mockedApprove).toHaveBeenCalledTimes(1))
    expect(mockedApprove).toHaveBeenCalledWith(121, 'test-project')
    expect(mockedReject).not.toHaveBeenCalled()
    expect(mockedRetry).not.toHaveBeenCalled()
    expect(mockedResume).not.toHaveBeenCalled()
    expect(mockedRerun).not.toHaveBeenCalled()
    expect(mockedStart).not.toHaveBeenCalled()
    expect(mockedStop).not.toHaveBeenCalled()
  })
})
