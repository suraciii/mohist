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

const FORBIDDEN_FULL_SURFACE_BG = [
  'bg-blue-50',
  'bg-amber-50',
  'bg-red-50',
  'bg-orange-50',
  'bg-green-50',
  'bg-gray-50',
  'bg-violet-50',
]

function expectNeutralBackgroundWithLeftEdgeAccent(
  surface: HTMLElement,
  expectedTone: 'blue' | 'amber' | 'red' | 'orange' | 'green' | 'gray' | 'violet',
) {
  expect(surface.className).toContain('bg-white')
  expect(surface.className).toContain('border-l-4')
  expect(surface.className).toContain(`border-l-${expectedTone}-500`)
  expect(surface.dataset.tone).toBe(expectedTone)
  for (const bg of FORBIDDEN_FULL_SURFACE_BG) {
    expect(surface.className).not.toContain(bg)
  }
}

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
    expectNeutralBackgroundWithLeftEdgeAccent(surface, 'blue')

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
    expectNeutralBackgroundWithLeftEdgeAccent(surface, 'amber')
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
    expectNeutralBackgroundWithLeftEdgeAccent(surface, 'red')
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
    expectNeutralBackgroundWithLeftEdgeAccent(surface, 'amber')
    expect(within(surface).getByTestId('runtime-action-approve')).toBeDisabled()
    expect(within(surface).getByTestId('runtime-action-send-back')).toBeDisabled()
  })

  it('invokes approveIssue when the surface approve button is clicked', async () => {
    const invalidateSpy = vi.spyOn(QueryClient.prototype, 'invalidateQueries')
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
    await waitFor(() => {
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['issues', 'metrics', 'approval-wait'] })
    })
    invalidateSpy.mockRestore()
  })

  it('invalidates approval-wait metrics when the surface send-back button resolves', async () => {
    const invalidateSpy = vi.spyOn(QueryClient.prototype, 'invalidateQueries')
    mockedReject.mockResolvedValueOnce({ issue: makeIssue(), message: 'sent back' })

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
      workflowRunId: 'wr-6',
      status: 'AwaitingApproval',
      currentStage: WorkflowStage.Check,
      pendingWork: null,
      stages: [],
      availableActions: [{ name: 'reject', label: 'Send back', target: null }],
    }

    renderSurface({ issue, timeline })

    screen.getByTestId('runtime-action-send-back').click()

    await waitFor(() => expect(mockedReject).toHaveBeenCalledTimes(1))
    expect(mockedReject).toHaveBeenCalledWith(121, {}, 'test-project')
    await waitFor(() => {
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['issues', 'metrics', 'approval-wait'] })
    })
    invalidateSpy.mockRestore()
  })

  describe('restrained background with colored edge accent', () => {
    const emptyTimeline: WorkflowTimeline = {
      workflowRunId: '',
      status: '',
      currentStage: null,
      pendingWork: null,
      stages: [],
      availableActions: [],
    }

    type StateCase = {
      summary: 'running' | 'queued' | 'approval-required' | 'blocked' | 'failed' | 'done'
      tone: 'blue' | 'amber' | 'red' | 'orange' | 'green' | 'gray' | 'violet'
      label: string
      issue: Partial<Issue>
      timeline?: WorkflowTimeline
      agentStatus?: Parameters<typeof renderSurface>[0]['agentStatus']
      hasActiveAgent?: boolean
    }

    const cases: StateCase[] = [
      {
        summary: 'running',
        tone: 'blue',
        label: 'Running',
        issue: {
          workflowStage: WorkflowStage.Build,
          workflowStatus: 'running',
          health: IssueHealth.Active,
          recovery: {
            currentWorkItem: { type: 'task', id: 't1', title: 'Implement RuntimeDecisionSurface' },
            latestAttemptState: 'running',
            workflowSummaryState: 'running',
            allowedActions: ['stop'],
          },
        },
        timeline: {
          workflowRunId: 'wr-run',
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
        },
        agentStatus: {
          running: true,
          issueId: 'issue-1',
          issueNumber: 121,
          activeAgents: [],
          capacity: { active: 0, max: 1 },
          runnerAvailable: true,
        },
        hasActiveAgent: true,
      },
      {
        summary: 'queued',
        tone: 'violet',
        label: 'Queued',
        issue: {
          workflowStage: WorkflowStage.Build,
          workflowStatus: 'pending',
          health: IssueHealth.Active,
          blocker: { kind: 'waiting-for', issue: { number: 99, title: 'Blocker issue' } },
          recovery: {
            currentWorkItem: null,
            latestAttemptState: null,
            workflowSummaryState: null,
            allowedActions: [],
          },
        },
        timeline: emptyTimeline,
      },
      {
        summary: 'approval-required',
        tone: 'amber',
        label: 'Approval required',
        issue: {
          workflowStage: WorkflowStage.Check,
          workflowStatus: 'awaiting-approval',
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
        },
        timeline: {
          workflowRunId: 'wr-approval',
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
        },
      },
      {
        summary: 'blocked',
        tone: 'orange',
        label: 'Blocked',
        issue: {
          workflowStage: WorkflowStage.Build,
          workflowStatus: 'blocked',
          health: IssueHealth.Blocked,
          recovery: {
            currentWorkItem: null,
            latestAttemptState: 'interrupted',
            workflowSummaryState: null,
            allowedActions: ['resume'],
          },
        },
        timeline: emptyTimeline,
      },
      {
        summary: 'failed',
        tone: 'red',
        label: 'Failed',
        issue: {
          workflowStage: WorkflowStage.Check,
          workflowStatus: 'failed',
          health: IssueHealth.Blocked,
          recovery: {
            currentWorkItem: null,
            latestAttemptState: 'failed',
            workflowSummaryState: 'waiting-for-recovery',
            allowedActions: ['retry', 'rerun'],
          },
        },
        timeline: {
          workflowRunId: 'wr-failed',
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
        },
      },
      {
        summary: 'done',
        tone: 'green',
        label: 'Done',
        issue: {
          workflowStage: WorkflowStage.Done,
          workflowStatus: 'done',
          health: IssueHealth.Done,
          recovery: {
            currentWorkItem: null,
            latestAttemptState: null,
            workflowSummaryState: 'completed',
            allowedActions: [],
          },
        },
        timeline: {
          workflowRunId: 'wr-done',
          status: 'Done',
          currentStage: WorkflowStage.Done,
          pendingWork: null,
          stages: [],
          availableActions: [],
        },
      },
    ]

    for (const c of cases) {
      it(`renders ${c.summary} with a neutral background, a colored left edge accent, and the ${c.label} label`, () => {
        renderSurface({
          issue: makeIssue(c.issue),
          timeline: c.timeline,
          agentStatus: c.agentStatus,
          hasActiveAgent: c.hasActiveAgent,
        })

        const surface = screen.getByTestId('runtime-decision-surface')
        expect(surface.dataset.summary).toBe(c.summary)
        expectNeutralBackgroundWithLeftEdgeAccent(surface, c.tone)

        const label = within(surface).getByTestId('runtime-summary-label')
        expect(label).toHaveTextContent(c.label)
      })
    }

    it('never stacks a full-surface colored block across all states', () => {
      const edgeAccents = new Set<string>()

      for (const c of cases) {
        cleanup()
        renderSurface({
          issue: makeIssue(c.issue),
          timeline: c.timeline,
          agentStatus: c.agentStatus,
          hasActiveAgent: c.hasActiveAgent,
        })

        const surface = screen.getByTestId('runtime-decision-surface')
        for (const bg of FORBIDDEN_FULL_SURFACE_BG) {
          expect(surface.className, `state ${c.summary} leaked ${bg}`).not.toContain(bg)
        }
        expect(surface.className).toContain('bg-white')
        expect(surface.className).toContain('border-l-4')

        const accents = surface.className.match(/border-l-\S+/g) ?? []
        const coloredAccent = accents.find((cls) => /border-l-(blue|amber|red|orange|green|gray|violet)-500/.test(cls))
        expect(coloredAccent, `state ${c.summary} missing colored left edge accent`).toBeDefined()
        edgeAccents.add(coloredAccent!)
      }

      expect(edgeAccents.size).toBeGreaterThan(1)
    })
  })
})
