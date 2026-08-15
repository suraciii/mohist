import { describe, expect, it, vi, beforeEach } from 'vitest'
import { useMutation } from '@tanstack/react-query'
import { screen, waitFor, fireEvent, within } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import type { ComponentProps, ReactElement } from 'react'
import { createQueryClient, render as renderWithProviders } from '../../../../tests/test-utils'
import { useMswServer } from '../../../../tests/support/msw'
import { WorkflowView as DefaultWorkflowView, type WorkflowTimelineHook } from './WorkflowView'
import type { ArtifactContentHook } from './ArtifactContentViewer'
import type { StepListDependencies } from './InlineApproval'
import {
  IssueStatus,
  IssueHealth,
  WorkflowStage,
  type ApprovalFeedback,
  type Issue,
  type WorkflowTimeline,
  type useWorkflowTimeline,
} from '../../../entities/issue'
import type { WorkflowArtifactContentResult } from '../../../entities/issue/api/client'
import { setScopedValue } from '../../../../tests/support/scoped-property'
import type { TaskLogDataHook, WorkflowRunSessionsHook } from './TaskLogPanel'
import { issueWorkflowKeys } from '../../../entities/issue/api/query-keys'

let approveRequests: string[] = []
let feedbackRequests: Array<{ issueNumber: number; stage: string; body: string; author?: string | null }> = []
let artifactRequests: string[] = []
let timelineRequests: string[] = []
let timelineData: WorkflowTimeline | null | undefined

useMswServer(
  http.get('*/api/projects/:projectId/issues/:issueNumber/workflow/status', ({ params }) => {
    timelineRequests.push(String(params.issueNumber))
    return HttpResponse.json({ success: true, data: { workflow: timelineData ?? null } })
  }),
)

function makeIssue(overrides: Partial<Issue> = {}): Issue {
  return {
    number: 1,
    title: 'Implement workflow naming',
    body: '',
    status: IssueStatus.InProgress,
    workflowStage: WorkflowStage.Build,
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

const approveIssueFn: StepListDependencies['approveIssue'] = async (issueNumber) => {
  approveRequests.push(String(issueNumber))
  return { issue: makeIssue({ number: issueNumber }), context: null, message: 'approved' }
}

const requestChangesHook: StepListDependencies['requestChangesHook'] = () =>
  useMutation<
    ApprovalFeedback,
    Error,
    { issueNumber: number; data: { stage: string; body: string; author?: string | null } }
  >({
    mutationFn: async ({ issueNumber, data }) => {
      feedbackRequests.push({
        issueNumber,
        stage: data.stage,
        body: data.body,
        author: data.author,
      })
      return {
        id: 'feedback-1',
        issueNumber,
        workflowRunId: 'workflow-run-1',
        stage: data.stage,
        status: 'open',
        body: data.body,
        createdAt: '2026-01-01T00:00:00.000Z',
        resolution: null,
      }
    },
  })

const artifactContentHook: ArtifactContentHook = (_issueNumber, artifactId, _options, enabled = true) => {
  if (enabled && artifactId && !artifactRequests.includes(artifactId)) {
    artifactRequests.push(artifactId)
  }
  const data: WorkflowArtifactContentResult | undefined = enabled
    ? { kind: 'text', content: '# artifact', contentType: 'text/plain' }
    : undefined
  return { data, isLoading: false, error: null }
}

const taskLogHook: TaskLogDataHook = () => ({
  data: { lines: [], nextCursor: null, truncated: false },
  isLoading: false,
  isError: false,
})

const workflowSessionsHook: WorkflowRunSessionsHook = () => ({ sessions: [], isLoading: false })

const workflowDependencies: StepListDependencies = {
  approveIssue: approveIssueFn,
  requestChangesHook,
  artifactContentHook,
  taskLogHook,
  workflowSessionsHook,
}

function WorkflowView(props: Omit<ComponentProps<typeof DefaultWorkflowView>, 'dependencies'>) {
  return <DefaultWorkflowView {...props} dependencies={workflowDependencies} />
}

function makeTimeline(): WorkflowTimeline {
  return {
    workflowRunId: 'workflow-run-1',
    status: 'Running',
    currentStage: WorkflowStage.Build,
    pendingWork: {
      workId: 'build-task-1',
      workType: 'task',
      stage: WorkflowStage.Build,
      title: 'Implement WorkflowView',
      uses: 'mohist/coder-agent',
    },
    stages: [
      {
        stage: WorkflowStage.Build,
        status: 'running',
        order: 2,
        startedAt: '2026-01-01T00:00:00.000Z',
        completedAt: null,
        durationMs: null,
        tasks: [
          {
            id: 'build-task-1',
            title: 'Implement WorkflowView',
            uses: 'mohist/coder-agent',
            status: 'running',
            startedAt: '2026-01-01T00:00:00.000Z',
            completedAt: null,
            durationMs: null,
            attempts: 1,
            message: null,
          },
        ],
        checks: [
          {
            name: 'health',
            title: 'Typecheck',
            uses: 'core/script',
            status: 'completed',
            message: 'ok',
            startedAt: '2026-01-01T00:01:00.000Z',
            completedAt: '2026-01-01T00:01:02.000Z',
            durationMs: 2000,
          },
        ],
        approval: null,
      },
    ],
    availableActions: [],
  }
}

function makeFourStageTimeline(): WorkflowTimeline {
  const stages = [WorkflowStage.Plan, WorkflowStage.Build, WorkflowStage.Check, WorkflowStage.Integrate]
  return {
    workflowRunId: 'workflow-run-1',
    status: 'Running',
    currentStage: WorkflowStage.Build,
    pendingWork: null,
    stages: stages.map((stage, index) => ({
      stage,
      status:
        stage === WorkflowStage.Build
          ? ('running' as const)
          : index < 1
            ? ('completed' as const)
            : ('pending' as const),
      order: index,
      startedAt: index < 2 ? '2026-01-01T00:00:00.000Z' : null,
      completedAt: index < 1 ? '2026-01-01T00:01:00.000Z' : null,
      durationMs: index < 1 ? 60000 : null,
      tasks: [
        {
          id: `${stage}-task`,
          title: `${stage} inspection task`,
          uses: 'core/script',
          status:
            stage === WorkflowStage.Build
              ? ('running' as const)
              : index < 1
                ? ('completed' as const)
                : ('pending' as const),
          startedAt: index < 2 ? '2026-01-01T00:00:00.000Z' : null,
          completedAt: index < 1 ? '2026-01-01T00:01:00.000Z' : null,
          durationMs: index < 1 ? 60000 : null,
          attempts: 1,
          message: null,
        },
      ],
      checks: [],
      approval: null,
    })),
    availableActions: [],
  }
}

function makeTimelineWithArtifacts(): WorkflowTimeline {
  return {
    workflowRunId: 'workflow-run-1',
    status: 'Running',
    currentStage: WorkflowStage.Plan,
    pendingWork: null,
    stages: [
      {
        stage: WorkflowStage.Plan,
        status: 'completed',
        order: 1,
        startedAt: '2026-01-01T00:00:00.000Z',
        completedAt: '2026-01-01T00:02:30.000Z',
        durationMs: 150000,
        tasks: [
          {
            id: 'plan-task-1',
            title: 'Generate proposal',
            uses: 'mohist/coder-agent',
            status: 'completed',
            startedAt: '2026-01-01T00:00:00.000Z',
            completedAt: '2026-01-01T00:02:30.000Z',
            durationMs: 150000,
            attempts: 1,
            message: 'proposal generated',
            artifactSummaries: [
              {
                artifactId: 'artifact-1',
                path: 'proposal.md',
                kind: 'file',
                size: 1234,
                recordedAt: '2026-01-01T00:02:00.000Z',
              },
              {
                artifactId: 'artifact-2',
                path: 'design.md',
                kind: 'file',
                size: 5678,
                recordedAt: '2026-01-01T00:02:30.000Z',
              },
            ],
          },
          {
            id: 'plan-task-2',
            title: 'Write specs',
            uses: 'mohist/coder-agent',
            status: 'completed',
            startedAt: '2026-01-01T00:00:00.000Z',
            completedAt: '2026-01-01T00:01:15.000Z',
            durationMs: 75000,
            attempts: 1,
            message: null,
            artifactSummaries: [
              {
                artifactId: 'artifact-3',
                path: 'specs/',
                kind: 'directory',
                size: 4096,
                recordedAt: '2026-01-01T00:01:15.000Z',
              },
            ],
          },
          {
            id: 'plan-task-3',
            title: 'Create design',
            uses: 'mohist/coder-agent',
            status: 'running',
            startedAt: '2026-01-01T00:02:00.000Z',
            completedAt: null,
            durationMs: null,
            attempts: 1,
            message: null,
            artifactSummaries: [
              {
                artifactId: 'artifact-4',
                path: 'design.md',
                kind: 'file',
                size: 5678,
                recordedAt: '2026-01-01T00:02:30.000Z',
              },
            ],
          },
          {
            id: 'plan-task-4',
            title: 'Review plan',
            uses: 'mohist/reviewer-agent',
            status: 'completed',
            startedAt: '2026-01-01T00:01:00.000Z',
            completedAt: '2026-01-01T00:01:30.000Z',
            durationMs: 30000,
            attempts: 1,
            message: null,
          },
        ],
        checks: [],
        approval: null,
      },
    ],
    availableActions: [],
  }
}

function makeAwaitingApprovalTimeline(): WorkflowTimeline {
  return {
    workflowRunId: 'workflow-run-1',
    status: 'AwaitingApproval',
    currentStage: WorkflowStage.Plan,
    pendingWork: null,
    stages: [
      {
        stage: WorkflowStage.Plan,
        status: 'awaiting-approval',
        order: 1,
        startedAt: '2026-01-01T00:00:00.000Z',
        completedAt: null,
        durationMs: null,
        tasks: [],
        checks: [],
        approval: {
          status: 'awaiting',
          output: null,
          requestedAt: '2026-01-01T00:01:00.000Z',
          respondedAt: null,
        },
      },
    ],
    availableActions: [{ name: 'approve', label: 'Approve', target: null }],
  }
}

function setWorkflowTimeline(value: { data: WorkflowTimeline | null | undefined }) {
  timelineData = value.data
}

function render(ui: ReactElement) {
  const queryClient = createQueryClient()
  const queryKey = issueWorkflowKeys.timeline('test-project', 1)
  queryClient.setQueryDefaults(queryKey, { staleTime: Number.POSITIVE_INFINITY })
  if (timelineData !== undefined) {
    queryClient.setQueryData(queryKey, timelineData)
  }
  return renderWithProviders(ui, { queryClient })
}

describe('WorkflowView', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    approveRequests = []
    feedbackRequests = []
    artifactRequests = []
    timelineRequests = []
    timelineData = makeTimeline()
    setScopedValue(window, 'innerWidth', 1280)
  })

  it('renders workflow timeline tasks and checks', () => {
    setWorkflowTimeline({ data: makeTimeline() } as ReturnType<typeof useWorkflowTimeline>)

    render(<WorkflowView issue={makeIssue()} />)

    expect(screen.getByText('Implement WorkflowView')).toBeInTheDocument()
    expect(screen.getByText('Typecheck')).toBeInTheDocument()
    expect(screen.getByText('runtime:coder-agent')).toBeInTheDocument()
    expect(screen.getByText('runtime:core/script')).toBeInTheDocument()
    expect(screen.queryByText('No tasks yet')).not.toBeInTheDocument()
  })

  it('renders the update interruption lifecycle with operation and replacement context', () => {
    const timeline = makeTimeline()
    timeline.interruptionAttention = {
      state: 'recovering',
      updateOperationId: 'update-567',
      workId: 'work-old.recovery.1',
      taskRunId: 'build-task-1.recovery',
      recoveryGeneration: 1,
      originalTurnId: 'turn-old',
      replacementTurnId: 'turn-recovery',
      expectedRecoveryPath: 'The replacement dispatch will resume this work.',
      stopFailure: null,
      recordedAt: '2026-08-15T00:02:00.000Z',
    }
    timeline.stages[0].tasks[0].agentInterruption = timeline.interruptionAttention

    renderWithProviders(<WorkflowView issue={makeIssue()} timelineHook={() => ({ data: timeline })} />)

    const panel = screen.getByTestId('workflow-agent-interruption-attention')
    expect(panel).toHaveTextContent('recovering')
    expect(panel).toHaveTextContent('update-567')
    expect(panel).toHaveTextContent('turn-recovery')
    expect(panel).not.toHaveTextContent('session.abort fetch failed')
  })

  it('renders blocked Agent-result attention without a failure presentation', () => {
    const timeline = makeTimeline()
    timeline.status = 'blocked'
    timeline.stages[0].status = 'blocked'
    timeline.stages[0].tasks[0] = {
      ...timeline.stages[0].tasks[0],
      status: 'blocked',
      message: 'Runner disconnected before the Agent result was accepted.',
      agentResultSettlement: {
        state: 'blocked',
        reason: 'agent-result-unconfirmed',
        message: 'Runner disconnected before the Agent result was accepted.',
        firstUnknownAt: '2026-08-14T10:56:58Z',
        deadlineAt: '2026-08-14T11:01:58Z',
        taskRunId: 'build.1',
        workId: 'build.1',
        runnerId: 'runner-pluto',
        agentSessionId: 'session-1',
        agentTurnId: 'turn-1',
        nextAction: 'Restore the original Runner and allow the result to replay.',
        recoveryActions: ['stop'],
      },
    }
    timeline.agentResultAttention = {
      state: 'blocked',
      reason: 'agent-result-unconfirmed',
      message: 'Runner disconnected before the Agent result was accepted.',
      firstUnknownAt: '2026-08-14T10:56:58Z',
      deadlineAt: '2026-08-14T11:01:58Z',
      taskRunId: 'build.1',
      workId: 'build.1',
      runnerId: 'runner-pluto',
      agentSessionId: 'session-1',
      agentTurnId: 'turn-1',
      nextAction: 'Restore the original Runner and allow the result to replay.',
      recoveryActions: ['stop'],
    }
    setWorkflowTimeline({ data: timeline } as ReturnType<typeof useWorkflowTimeline>)

    render(
      <WorkflowView
        issue={makeIssue({
          health: IssueHealth.Blocked,
          workflowStatus: 'blocked',
          blockedReason: 'Agent result unconfirmed',
        })}
      />,
    )

    const attention = screen.getByTestId('workflow-agent-result-attention')
    expect(attention).toHaveTextContent('Agent result unconfirmed')
    expect(attention).toHaveTextContent('session-1')
    expect(attention).toHaveTextContent('turn-1')
    expect(screen.getByText('blocked')).toBeInTheDocument()
    expect(screen.queryByText(/Workflow failed/i)).not.toBeInTheDocument()
  })

  it('renders every stage in a fixed two-column mobile grid', async () => {
    setScopedValue(window, 'innerWidth', 390)
    setWorkflowTimeline({ data: makeTimeline() } as ReturnType<typeof useWorkflowTimeline>)

    render(<WorkflowView issue={makeIssue()} />)

    const stageBar = await screen.findByTestId('workflow-stage-bar')
    expect(stageBar).toHaveClass('grid', 'grid-cols-2', 'sm:grid-cols-4')
    expect(stageBar).not.toHaveClass('overflow-x-auto', 'flex-nowrap')

    for (const label of ['Plan', 'Build', 'Check', 'Integrate']) {
      const stageButton = screen.getByRole('button', { name: new RegExp(label, 'i') })
      expect(stageButton).toBeInTheDocument()
      expect(stageButton).not.toBeDisabled()
      const labelNode = within(stageButton).getByText(label)
      expect(labelNode).toBeInTheDocument()
      expect(labelNode).not.toHaveClass('truncate')
    }

    expect(screen.queryByRole('button', { name: /^done$/i })).not.toBeInTheDocument()
  })

  it('does not request workflow timeline for backlog issues', () => {
    setWorkflowTimeline({ data: undefined } as ReturnType<typeof useWorkflowTimeline>)

    render(<WorkflowView issue={makeIssue({ status: IssueStatus.Backlog, workflowStage: null })} />)

    expect(timelineRequests).toEqual([])
  })

  it('keeps every stage inspectable in read-only presentations', () => {
    setWorkflowTimeline({ data: makeFourStageTimeline() } as ReturnType<typeof useWorkflowTimeline>)

    render(<WorkflowView issue={makeIssue()} readOnly />)

    expect(screen.getByText('build inspection task')).toBeInTheDocument()
    const planButton = screen.getByRole('button', { name: /Plan/i })
    expect(planButton).not.toBeDisabled()
    fireEvent.click(planButton)

    expect(planButton).toHaveAttribute('aria-current', 'step')
    expect(screen.getByText('plan inspection task')).toBeInTheDocument()
    expect(screen.queryByText('build inspection task')).not.toBeInTheDocument()
    expect(screen.queryByTestId('approve-button')).not.toBeInTheDocument()
  })

  it('preserves user selection across timeline polling and resets for issue identity or default-stage changes', () => {
    let polledTimeline = makeFourStageTimeline()
    const pollingHook: WorkflowTimelineHook = () => ({ data: polledTimeline })
    const issue = makeIssue()
    const rendered = renderWithProviders(
      <DefaultWorkflowView issue={issue} timelineHook={pollingHook} dependencies={workflowDependencies} />,
    )
    const stageBar = screen.getByTestId('workflow-stage-bar')

    fireEvent.click(within(stageBar).getByRole('button', { name: /Plan/i }))
    expect(screen.getByText('plan inspection task')).toBeInTheDocument()

    polledTimeline = { ...makeFourStageTimeline(), status: 'AwaitingApproval' }
    rendered.rerender(
      <DefaultWorkflowView issue={issue} timelineHook={pollingHook} dependencies={workflowDependencies} />,
    )
    expect(within(stageBar).getByRole('button', { name: /Plan/i })).toHaveAttribute('aria-current', 'step')

    rendered.rerender(
      <DefaultWorkflowView
        issue={makeIssue({ number: 2 })}
        timelineHook={pollingHook}
        dependencies={workflowDependencies}
      />,
    )
    expect(within(stageBar).getByRole('button', { name: /Build/i })).toHaveAttribute('aria-current', 'step')

    fireEvent.click(within(stageBar).getByRole('button', { name: /Plan/i }))
    rendered.rerender(
      <DefaultWorkflowView
        issue={makeIssue({ number: 2, projectId: 'other-project' })}
        timelineHook={pollingHook}
        dependencies={workflowDependencies}
      />,
    )
    expect(within(stageBar).getByRole('button', { name: /Build/i })).toHaveAttribute('aria-current', 'step')

    rendered.rerender(
      <DefaultWorkflowView
        issue={makeIssue({ number: 2, projectId: 'other-project', workflowStage: WorkflowStage.Check })}
        timelineHook={pollingHook}
        dependencies={workflowDependencies}
      />,
    )
    expect(within(stageBar).getByRole('button', { name: /Check/i })).toHaveAttribute('aria-current', 'step')
  })

  it.each([IssueStatus.Done, IssueStatus.Cancelled])('allows stage inspection for %s issues', (status) => {
    setWorkflowTimeline({ data: makeFourStageTimeline() } as ReturnType<typeof useWorkflowTimeline>)
    const workflowStage = status === IssueStatus.Done ? WorkflowStage.Integrate : WorkflowStage.Build

    render(<WorkflowView issue={makeIssue({ status, workflowStage })} readOnly />)

    fireEvent.click(screen.getByRole('button', { name: /Plan/i }))
    expect(screen.getByText('plan inspection task')).toBeInTheDocument()
  })

  describe('InlineApproval - evidence rendering without mutation controls', () => {
    it('does not render the InlineApprovalControls or initialize any approval mutation when mounted read-only', () => {
      setWorkflowTimeline({
        data: makeAwaitingApprovalTimeline(),
      } as unknown as ReturnType<typeof useWorkflowTimeline>)

      render(
        <WorkflowView
          issue={makeIssue({
            workflowStage: WorkflowStage.Plan,
            health: 'attention' as IssueHealth,
            approvalState: {
              status: 'awaiting',
              stage: WorkflowStage.Plan,
              requestedAt: '2026-01-01T00:01:00.000Z',
            },
          })}
          readOnly
        />,
      )

      expect(screen.queryByTestId('approve-button')).not.toBeInTheDocument()
      expect(screen.queryByTestId('request-changes-button')).not.toBeInTheDocument()
      expect(screen.queryByTestId('request-changes-form')).not.toBeInTheDocument()
      expect(approveRequests).toEqual([])
      expect(feedbackRequests).toEqual([])
    })

    it('does not render approve/request-changes mutation controls even when not read-only', () => {
      setWorkflowTimeline({
        data: makeAwaitingApprovalTimeline(),
      } as unknown as ReturnType<typeof useWorkflowTimeline>)

      render(
        <WorkflowView
          issue={makeIssue({
            workflowStage: WorkflowStage.Plan,
            health: 'attention' as IssueHealth,
            approvalState: {
              status: 'awaiting',
              stage: WorkflowStage.Plan,
              requestedAt: '2026-01-01T00:01:00.000Z',
            },
          })}
        />,
      )

      expect(screen.queryByTestId('approve-button')).not.toBeInTheDocument()
      expect(screen.queryByTestId('request-changes-button')).not.toBeInTheDocument()
      expect(screen.queryByRole('button', { name: /send back/i })).not.toBeInTheDocument()
      expect(screen.queryByRole('button', { name: /^reject$/i })).not.toBeInTheDocument()
      expect(screen.queryByRole('button', { name: /approve anyway/i })).not.toBeInTheDocument()
      expect(approveRequests).toEqual([])
      expect(feedbackRequests).toEqual([])
    })

    it('renders the read-only approval evidence panel when an approval output is awaiting', () => {
      setWorkflowTimeline({
        data: makeAwaitingApprovalTimeline(),
      } as unknown as ReturnType<typeof useWorkflowTimeline>)

      render(
        <WorkflowView
          issue={makeIssue({
            workflowStage: WorkflowStage.Plan,
            health: 'attention' as IssueHealth,
            approvalState: {
              status: 'awaiting',
              stage: WorkflowStage.Plan,
              requestedAt: '2026-01-01T00:01:00.000Z',
              output: { result: 'PASS', checks: [] },
            },
          })}
          readOnly
        />,
      )

      expect(screen.getByTestId('step-list-approval-evidence')).toBeInTheDocument()
    })

    it('hides approval evidence when stage is not awaiting approval', () => {
      setWorkflowTimeline({ data: makeTimeline() } as ReturnType<typeof useWorkflowTimeline>)

      render(
        <WorkflowView
          issue={makeIssue({
            workflowStage: WorkflowStage.Build,
            health: IssueHealth.Active,
          })}
          readOnly
        />,
      )

      expect(screen.queryByTestId('step-list-approval-evidence')).not.toBeInTheDocument()
    })

    it('hides approval evidence when stage is running', () => {
      setWorkflowTimeline({ data: makeTimeline() } as ReturnType<typeof useWorkflowTimeline>)

      render(
        <WorkflowView
          issue={makeIssue({
            workflowStage: WorkflowStage.Build,
            workflowStatus: 'running',
            health: IssueHealth.Active,
          })}
          readOnly
        />,
      )

      expect(screen.queryByTestId('step-list-approval-evidence')).not.toBeInTheDocument()
    })

    it('renders feedback history when feedback records exist', () => {
      setWorkflowTimeline({
        data: makeAwaitingApprovalTimeline(),
      } as unknown as ReturnType<typeof useWorkflowTimeline>)

      const feedback = [
        {
          id: 'fb-1',
          issueNumber: 1,
          workflowRunId: 'wr-1',
          stage: 'plan',
          status: 'resolved' as const,
          body: 'Please add error handling',
          createdAt: '2026-01-01T00:00:00.000Z',
          resolution: {
            resolvedAt: '2026-01-01T00:10:00.000Z',
            resolutionTaskId: 'task-1',
            resolutionSummary: 'Added try/catch in all handlers',
          },
        },
      ]

      render(
        <WorkflowView
          issue={makeIssue({
            workflowStage: WorkflowStage.Plan,
            health: 'attention' as IssueHealth,
            approvalState: {
              status: 'awaiting',
              stage: WorkflowStage.Plan,
              requestedAt: '2026-01-01T00:01:00.000Z',
            },
            feedback,
          })}
        />,
      )

      expect(screen.getByText('Feedback history')).toBeInTheDocument()
      expect(screen.getByText('Cycle 1')).toBeInTheDocument()
      expect(screen.getByText('Please add error handling')).toBeInTheDocument()
      expect(screen.getByText('Added try/catch in all handlers')).toBeInTheDocument()
      expect(screen.getByText('Resolved')).toBeInTheDocument()
    })

    it('renders multiple feedback cycles distinctly', () => {
      setWorkflowTimeline({
        data: makeAwaitingApprovalTimeline(),
      } as unknown as ReturnType<typeof useWorkflowTimeline>)

      const feedback = [
        {
          id: 'fb-1',
          issueNumber: 1,
          workflowRunId: 'wr-1',
          stage: 'plan',
          status: 'resolved' as const,
          body: 'First feedback',
          createdAt: '2026-01-01T00:00:00.000Z',
          resolution: {
            resolvedAt: '2026-01-01T00:10:00.000Z',
            resolutionTaskId: 'task-1',
            resolutionSummary: 'First resolution',
          },
        },
        {
          id: 'fb-2',
          issueNumber: 1,
          workflowRunId: 'wr-1',
          stage: 'plan',
          status: 'open' as const,
          body: 'Second feedback',
          createdAt: '2026-01-02T00:00:00.000Z',
          resolution: null,
        },
      ]

      render(
        <WorkflowView
          issue={makeIssue({
            workflowStage: WorkflowStage.Plan,
            health: 'attention' as IssueHealth,
            approvalState: {
              status: 'awaiting',
              stage: WorkflowStage.Plan,
              requestedAt: '2026-01-01T00:01:00.000Z',
            },
            feedback,
          })}
        />,
      )

      expect(screen.getByText('Cycle 1')).toBeInTheDocument()
      expect(screen.getByText('Cycle 2')).toBeInTheDocument()
      expect(screen.getByText('First feedback')).toBeInTheDocument()
      expect(screen.getByText('Second feedback')).toBeInTheDocument()
      expect(screen.getByText('Resolved')).toBeInTheDocument()
      expect(screen.getAllByText('Awaiting application').length).toBeGreaterThan(0)

      const fb1 = screen.getByTestId('feedback-fb-1') as HTMLElement
      const fb2 = screen.getByTestId('feedback-fb-2') as HTMLElement
      expect(fb1).toBeInTheDocument()
      expect(fb2).toBeInTheDocument()
      expect(fb1.dataset.feedbackStatus).toBe('resolved')
      expect(fb2.dataset.feedbackStatus).toBe('open')
    })

    it('shows open feedback awaiting-application state', () => {
      setWorkflowTimeline({
        data: makeAwaitingApprovalTimeline(),
      } as unknown as ReturnType<typeof useWorkflowTimeline>)

      const feedback = [
        {
          id: 'fb-open',
          issueNumber: 1,
          workflowRunId: 'wr-1',
          stage: 'plan',
          status: 'open' as const,
          body: 'Needs a better approach',
          createdAt: '2026-01-01T00:00:00.000Z',
          resolution: null,
        },
      ]

      render(
        <WorkflowView
          issue={makeIssue({
            workflowStage: WorkflowStage.Plan,
            health: 'attention' as IssueHealth,
            approvalState: {
              status: 'awaiting',
              stage: WorkflowStage.Plan,
              requestedAt: '2026-01-01T00:01:00.000Z',
            },
            feedback,
          })}
        />,
      )

      expect(screen.getAllByText('Awaiting application').length).toBeGreaterThan(0)
      expect(screen.getByText(/apply-feedback task is pending/)).toBeInTheDocument()
    })

    it('Feedback history is visible during the running feedback-loop without the approval evidence', () => {
      // While the apply-feedback task is running, the stage is `Running`
      // and the server's `RequestChanges` clears the stage approval
      // state, so `issue.approvalState` is null. The feedback-history timeline should still
      // surface the open cycle.
      setWorkflowTimeline({
        data: {
          ...makeAwaitingApprovalTimeline(),
          status: 'Running',
          stages: [
            {
              ...makeAwaitingApprovalTimeline().stages[0],
              status: 'running',
              approval: null,
            },
          ],
        },
      } as unknown as ReturnType<typeof useWorkflowTimeline>)

      const feedback = [
        {
          id: 'fb-open',
          issueNumber: 1,
          workflowRunId: 'wr-1',
          stage: 'plan',
          status: 'open' as const,
          body: 'Pending feedback',
          createdAt: '2026-01-01T00:00:00.000Z',
          resolution: null,
        },
      ]

      render(
        <WorkflowView
          issue={makeIssue({
            workflowStage: WorkflowStage.Plan,
            workflowStatus: 'running',
            health: 'attention' as IssueHealth,
            approvalState: undefined,
            feedback,
          })}
        />,
      )

      expect(screen.queryByTestId('approve-button')).not.toBeInTheDocument()
      expect(screen.queryByTestId('request-changes-button')).not.toBeInTheDocument()
      expect(screen.getByText('Feedback history')).toBeInTheDocument()
      expect(screen.getByText('Pending feedback')).toBeInTheDocument()
      expect(screen.getByText(/apply-feedback task is pending/)).toBeInTheDocument()
    })
  })

  describe('artifact chips on task rows', () => {
    it('renders clickable artifact chips for completed tasks with artifact summaries', () => {
      setWorkflowTimeline({ data: makeTimelineWithArtifacts() } as ReturnType<typeof useWorkflowTimeline>)

      render(<WorkflowView issue={makeIssue({ workflowStage: WorkflowStage.Plan })} />)

      const taskRow = screen
        .getByText('Generate proposal')
        .closest('[data-testid="workflow-task-item"]') as HTMLElement | null
      expect(taskRow).toBeInTheDocument()
      expect(within(taskRow!).getByText('proposal.md')).toBeInTheDocument()
      expect(within(taskRow!).getByText('design.md')).toBeInTheDocument()
    })

    it('renders directory artifact chips with folder styling', () => {
      setWorkflowTimeline({ data: makeTimelineWithArtifacts() } as ReturnType<typeof useWorkflowTimeline>)

      render(<WorkflowView issue={makeIssue({ workflowStage: WorkflowStage.Plan })} />)

      const taskRow = screen
        .getByText('Write specs')
        .closest('[data-testid="workflow-task-item"]') as HTMLElement | null
      expect(taskRow).toBeInTheDocument()
      expect(within(taskRow!).getByText('specs/')).toBeInTheDocument()
    })

    it('does not render artifact chips for running tasks', () => {
      setWorkflowTimeline({ data: makeTimelineWithArtifacts() } as ReturnType<typeof useWorkflowTimeline>)

      render(<WorkflowView issue={makeIssue({ workflowStage: WorkflowStage.Plan })} />)

      const taskRow = screen
        .getByText('Create design')
        .closest('[data-testid="workflow-task-item"]') as HTMLElement | null
      expect(taskRow).toBeInTheDocument()
      expect(within(taskRow!).queryByText('design.md')).not.toBeInTheDocument()
    })

    it('does not render artifact chips for completed tasks without artifacts', () => {
      setWorkflowTimeline({ data: makeTimelineWithArtifacts() } as ReturnType<typeof useWorkflowTimeline>)

      render(<WorkflowView issue={makeIssue({ workflowStage: WorkflowStage.Plan })} />)

      const taskRow = screen
        .getByText('Review plan')
        .closest('[data-testid="workflow-task-item"]') as HTMLElement | null
      expect(taskRow).toBeInTheDocument()
      expect(
        within(taskRow!).queryByRole('button', { name: /proposal\.md|design\.md|specs\// }),
      ).not.toBeInTheDocument()
    })

    it('opens ArtifactContentViewer when an artifact chip is clicked', async () => {
      setWorkflowTimeline({ data: makeTimelineWithArtifacts() } as ReturnType<typeof useWorkflowTimeline>)

      render(<WorkflowView issue={makeIssue({ workflowStage: WorkflowStage.Plan })} />)

      const chip = screen.getByRole('button', { name: 'proposal.md' })
      fireEvent.click(chip)

      await waitFor(() => {
        expect(artifactRequests).toEqual(['artifact-1'])
      })
      const dialog = screen.getByRole('dialog')
      expect(dialog).toBeInTheDocument()
      expect(within(dialog).getByText('proposal.md')).toBeInTheDocument()
    })

    it('does not toggle the task row when a chip is clicked on an expand-capable task', () => {
      setWorkflowTimeline({ data: makeTimelineWithArtifacts() } as ReturnType<typeof useWorkflowTimeline>)

      render(<WorkflowView issue={makeIssue({ workflowStage: WorkflowStage.Plan })} />)

      // "Generate proposal" has both artifactSummaries and a message output, so canExpand is true
      // (via hasOutput). Clicking the chip should open the viewer without expanding the row.
      const chip = screen.getByRole('button', { name: 'proposal.md' })
      fireEvent.click(chip)

      // The expanded panel renders an "Artifacts" header (uppercase tracking-wide label) inside
      // a bg-muted block. The chip click should NOT trigger expansion, so the panel for the
      // "Generate proposal" task body should not be visible.
      expect(screen.queryByText('proposal generated')).not.toBeInTheDocument()

      // The dialog, however, must be open.
      expect(screen.getByRole('dialog')).toBeInTheDocument()
    })

    it('renders artifact actions as buttons outside the task disclosure', () => {
      setWorkflowTimeline({ data: makeTimelineWithArtifacts() } as ReturnType<typeof useWorkflowTimeline>)

      render(<WorkflowView issue={makeIssue({ workflowStage: WorkflowStage.Plan })} />)

      const chip = screen.getByRole('button', { name: 'proposal.md' })
      const disclosure = screen.getByRole('button', { name: 'Generate proposal' })

      expect(disclosure.contains(chip)).toBe(false)
      fireEvent.click(chip)
      expect(screen.getByRole('dialog')).toBeInTheDocument()
    })
  })
})
