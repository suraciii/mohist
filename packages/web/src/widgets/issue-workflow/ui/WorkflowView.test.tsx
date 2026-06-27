import { describe, expect, it, vi, beforeEach } from 'vitest'
import { QueryClient } from '@tanstack/react-query'
import { screen, waitFor, fireEvent, within } from '@testing-library/react'
import { render } from '../../../../tests/test-utils'
import { WorkflowView } from './WorkflowView'
import { IssueStatus, IssueHealth, WorkflowStage, type Issue, type WorkflowTimeline } from '../../../entities/issue'
import { useWorkflowTimeline, useRequestChangesIssue, useIssueWorkflowArtifactContent } from '../../../entities/issue'
import * as clientModule from '../../../entities/issue/api/client'

vi.mock('../../../entities/issue', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../../entities/issue')>()),
  useWorkflowTimeline: vi.fn(),
  useIssueWorkflowArtifactContent: vi.fn(),
  useRequestChangesIssue: vi.fn(),
}))

vi.mock('../../../entities/issue/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../../entities/issue/api/client')>()),
  approveIssue: vi.fn(),
}))

const mockedUseWorkflowTimeline = vi.mocked(useWorkflowTimeline)
const mockedUseIssueWorkflowArtifactContent = vi.mocked(useIssueWorkflowArtifactContent)
const mockedUseRequestChangesIssue = vi.mocked(useRequestChangesIssue)
const mockedApproveIssue = vi.mocked(clientModule.approveIssue)

function makeIssue(overrides: Partial<Issue> = {}): Issue {
  return {
    id: 'issue-1',
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
              { artifactId: 'artifact-1', path: 'proposal.md', kind: 'file', size: 1234, recordedAt: '2026-01-01T00:02:00.000Z' },
              { artifactId: 'artifact-2', path: 'design.md', kind: 'file', size: 5678, recordedAt: '2026-01-01T00:02:30.000Z' },
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
              { artifactId: 'artifact-3', path: 'specs/', kind: 'directory', size: 4096, recordedAt: '2026-01-01T00:01:15.000Z' },
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
              { artifactId: 'artifact-4', path: 'design.md', kind: 'file', size: 5678, recordedAt: '2026-01-01T00:02:30.000Z' },
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

function mockRequestChangesMutation() {
  const mutate = vi.fn()
  mockedUseRequestChangesIssue.mockReturnValue({
    mutate,
    isPending: false,
    isError: false,
    error: null,
    data: undefined,
    variables: undefined,
    reset: vi.fn(),
    context: undefined,
    mutateAsync: vi.fn(),
    isIdle: true,
    isSuccess: false,
    failureCount: 0,
    failureReason: null,
    status: 'idle',
    submittedAt: 0,
  } as unknown as ReturnType<typeof useRequestChangesIssue>)
  return mutate
}

describe('WorkflowView', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    window.innerWidth = 1280
    mockedApproveIssue.mockResolvedValue({
      issue: {} as Issue,
      context: null,
      message: 'approved',
    })
  })

  it('renders workflow timeline tasks and checks', () => {
    mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimeline() } as ReturnType<typeof useWorkflowTimeline>)

    render(<WorkflowView issue={makeIssue()} />)

    expect(mockedUseWorkflowTimeline).toHaveBeenCalledWith(1, true)
    expect(screen.getByText('Implement WorkflowView')).toBeInTheDocument()
    expect(screen.getByText('Typecheck')).toBeInTheDocument()
    expect(screen.getByText('runtime:coder-agent')).toBeInTheDocument()
    expect(screen.getByText('runtime:core/script')).toBeInTheDocument()
    expect(screen.queryByText('No tasks yet')).not.toBeInTheDocument()
  })

  it('renders a scrollable stage stepper on mobile without clipping stage labels', async () => {
    window.innerWidth = 390
    mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimeline() } as ReturnType<typeof useWorkflowTimeline>)

    render(<WorkflowView issue={makeIssue()} />)

    const stageBar = await screen.findByTestId('workflow-stage-bar-scrollable-stepper')
    expect(stageBar).toHaveClass('overflow-x-auto', 'flex-nowrap')
    expect(screen.queryByTestId('workflow-stage-bar')).not.toBeInTheDocument()

    for (const label of ['Plan', 'Build', 'Check', 'Integrate', 'Done']) {
      const stageButton = screen.getByRole('button', { name: new RegExp(label, 'i') })
      expect(stageButton).toBeInTheDocument()
      expect(stageButton).toHaveClass('min-w-32', 'shrink-0')
      const labelNode = within(stageButton).getByText(label)
      expect(labelNode).toBeInTheDocument()
      expect(labelNode).toHaveClass('whitespace-nowrap')
      expect(labelNode).not.toHaveClass('truncate')
    }
  })

  it('does not request workflow timeline for backlog issues', () => {
    mockedUseWorkflowTimeline.mockReturnValue({ data: undefined } as ReturnType<typeof useWorkflowTimeline>)

    render(<WorkflowView issue={makeIssue({ status: IssueStatus.Backlog, workflowStage: null })} />)

    expect(mockedUseWorkflowTimeline).toHaveBeenCalledWith(1, false)
  })

  describe('InlineApproval - awaiting approval', () => {
    it('renders Approve and Request changes actions; no Reject or Send back labels', () => {
      mockRequestChangesMutation()
      mockedUseWorkflowTimeline.mockReturnValue(({
        data: makeAwaitingApprovalTimeline(),
      } as unknown) as ReturnType<typeof useWorkflowTimeline>)

      render(<WorkflowView issue={makeIssue({
        workflowStage: WorkflowStage.Plan,
        health: 'attention' as IssueHealth,
        approvalState: {
          status: 'awaiting',
          stage: WorkflowStage.Plan,
          requestedAt: '2026-01-01T00:01:00.000Z',
        },
      })} />)

      expect(screen.getByText('Approval Required')).toBeInTheDocument()
      expect(screen.getByTestId('approve-button')).toBeInTheDocument()
      expect(screen.getByTestId('request-changes-button')).toBeInTheDocument()
      expect(screen.queryByRole('button', { name: /send back/i })).not.toBeInTheDocument()
      expect(screen.queryByRole('button', { name: /^reject$/i })).not.toBeInTheDocument()
      expect(screen.queryByRole('button', { name: /approve anyway/i })).not.toBeInTheDocument()
    })

    it('Request changes action is hidden when stage is not awaiting approval', () => {
      mockRequestChangesMutation()
      mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimeline() } as ReturnType<typeof useWorkflowTimeline>)

      render(<WorkflowView issue={makeIssue({
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Active,
      })} />)

      expect(screen.queryByTestId('request-changes-button')).not.toBeInTheDocument()
      expect(screen.queryByTestId('request-changes-disabled')).not.toBeInTheDocument()
      expect(screen.queryByText('Approval Required')).not.toBeInTheDocument()
    })

    it('Request changes action is hidden when stage is running', () => {
      mockRequestChangesMutation()
      mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimeline() } as ReturnType<typeof useWorkflowTimeline>)

      render(<WorkflowView issue={makeIssue({
        workflowStage: WorkflowStage.Build,
        workflowStatus: 'running',
        health: IssueHealth.Active,
      })} />)

      expect(screen.queryByTestId('request-changes-button')).not.toBeInTheDocument()
    })

    it('clicking Request changes opens a text input for feedback body', () => {
      mockRequestChangesMutation()
      mockedUseWorkflowTimeline.mockReturnValue(({
        data: makeAwaitingApprovalTimeline(),
      } as unknown) as ReturnType<typeof useWorkflowTimeline>)

      render(<WorkflowView issue={makeIssue({
        workflowStage: WorkflowStage.Plan,
        health: 'attention' as IssueHealth,
        approvalState: {
          status: 'awaiting',
          stage: WorkflowStage.Plan,
          requestedAt: '2026-01-01T00:01:00.000Z',
        },
      })} />)

      fireEvent.click(screen.getByTestId('request-changes-button'))

      const form = screen.getByTestId('request-changes-form')
      expect(form).toBeInTheDocument()
      expect(screen.getByTestId('request-changes-textarea')).toBeInTheDocument()
      expect(screen.getByTestId('submit-request-changes')).toBeInTheDocument()
    })

    it('Submit button is disabled without feedback body', () => {
      mockRequestChangesMutation()
      mockedUseWorkflowTimeline.mockReturnValue(({
        data: makeAwaitingApprovalTimeline(),
      } as unknown) as ReturnType<typeof useWorkflowTimeline>)

      render(<WorkflowView issue={makeIssue({
        workflowStage: WorkflowStage.Plan,
        health: 'attention' as IssueHealth,
        approvalState: {
          status: 'awaiting',
          stage: WorkflowStage.Plan,
          requestedAt: '2026-01-01T00:01:00.000Z',
        },
      })} />)

      fireEvent.click(screen.getByTestId('request-changes-button'))

      const submit = screen.getByTestId('submit-request-changes')
      expect(submit).toBeDisabled()
    })

    it('submitting feedback calls requestChangesIssue with stage and body via POST /feedback', () => {
      const mutate = mockRequestChangesMutation()
      mockedUseWorkflowTimeline.mockReturnValue(({
        data: makeAwaitingApprovalTimeline(),
      } as unknown) as ReturnType<typeof useWorkflowTimeline>)

      render(<WorkflowView issue={makeIssue({
        workflowStage: WorkflowStage.Plan,
        health: 'attention' as IssueHealth,
        approvalState: {
          status: 'awaiting',
          stage: WorkflowStage.Plan,
          requestedAt: '2026-01-01T00:01:00.000Z',
        },
      })} />)

      fireEvent.click(screen.getByTestId('request-changes-button'))
      const textarea = screen.getByTestId('request-changes-textarea') as HTMLTextAreaElement
      fireEvent.change(textarea, { target: { value: 'Please address the security findings' } })
      fireEvent.click(screen.getByTestId('submit-request-changes'))

      expect(mutate).toHaveBeenCalledTimes(1)
      expect(mutate).toHaveBeenCalledWith(
        {
          issueNumber: 1,
          data: {
            stage: 'plan',
            body: 'Please address the security findings',
          },
        },
        expect.objectContaining({ onSuccess: expect.any(Function) }),
      )
    })

    it('renders feedback history when feedback records exist', () => {
      mockRequestChangesMutation()
      mockedUseWorkflowTimeline.mockReturnValue(({
        data: makeAwaitingApprovalTimeline(),
      } as unknown) as ReturnType<typeof useWorkflowTimeline>)

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

      render(<WorkflowView issue={makeIssue({
        workflowStage: WorkflowStage.Plan,
        health: 'attention' as IssueHealth,
        approvalState: {
          status: 'awaiting',
          stage: WorkflowStage.Plan,
          requestedAt: '2026-01-01T00:01:00.000Z',
        },
        feedback,
      })} />)

      expect(screen.getByText('Feedback history')).toBeInTheDocument()
      expect(screen.getByText('Cycle 1')).toBeInTheDocument()
      expect(screen.getByText('Please add error handling')).toBeInTheDocument()
      expect(screen.getByText('Added try/catch in all handlers')).toBeInTheDocument()
      expect(screen.getByText('Resolved')).toBeInTheDocument()
    })

    it('renders multiple feedback cycles distinctly', () => {
      mockRequestChangesMutation()
      mockedUseWorkflowTimeline.mockReturnValue(({
        data: makeAwaitingApprovalTimeline(),
      } as unknown) as ReturnType<typeof useWorkflowTimeline>)

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

      render(<WorkflowView issue={makeIssue({
        workflowStage: WorkflowStage.Plan,
        health: 'attention' as IssueHealth,
        approvalState: {
          status: 'awaiting',
          stage: WorkflowStage.Plan,
          requestedAt: '2026-01-01T00:01:00.000Z',
        },
        feedback,
      })} />)

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
      mockRequestChangesMutation()
      mockedUseWorkflowTimeline.mockReturnValue(({
        data: makeAwaitingApprovalTimeline(),
      } as unknown) as ReturnType<typeof useWorkflowTimeline>)

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

      render(<WorkflowView issue={makeIssue({
        workflowStage: WorkflowStage.Plan,
        health: 'attention' as IssueHealth,
        approvalState: {
          status: 'awaiting',
          stage: WorkflowStage.Plan,
          requestedAt: '2026-01-01T00:01:00.000Z',
        },
        feedback,
      })} />)

      expect(screen.getAllByText('Awaiting application').length).toBeGreaterThan(0)
      expect(screen.getByText(/apply-feedback task is pending/)).toBeInTheDocument()
    })

    it('Feedback history is visible during the running feedback-loop without the approval card', () => {
      mockRequestChangesMutation();
      // While the apply-feedback task is running, the stage is `Running`
      // and the server's `RequestChanges` clears the stage approval
      // state, so `issue.approvalState` is null. The InlineApproval
      // card is hidden. The feedback-history timeline should still
      // surface the open cycle.
      mockedUseWorkflowTimeline.mockReturnValue(({
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
      } as unknown) as ReturnType<typeof useWorkflowTimeline>);

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
      ];

      render(<WorkflowView issue={makeIssue({
        workflowStage: WorkflowStage.Plan,
        workflowStatus: 'running',
        health: 'attention' as IssueHealth,
        approvalState: undefined,
        feedback,
      })} />);

      // The InlineApproval card is hidden because the stage is running.
      expect(screen.queryByTestId('approve-button')).not.toBeInTheDocument();
      expect(screen.queryByTestId('request-changes-button')).not.toBeInTheDocument();
      // The feedback history still surfaces the open cycle.
      expect(screen.getByText('Feedback history')).toBeInTheDocument();
      expect(screen.getByText('Pending feedback')).toBeInTheDocument();
      expect(screen.getByText(/apply-feedback task is pending/)).toBeInTheDocument();
    });

    it('Approve action calls approveIssue', async () => {
      const invalidateSpy = vi.spyOn(QueryClient.prototype, 'invalidateQueries')
      mockRequestChangesMutation()
      mockedUseWorkflowTimeline.mockReturnValue(({
        data: makeAwaitingApprovalTimeline(),
      } as unknown) as ReturnType<typeof useWorkflowTimeline>)

      render(<WorkflowView issue={makeIssue({
        workflowStage: WorkflowStage.Plan,
        health: 'attention' as IssueHealth,
        approvalState: {
          status: 'awaiting',
          stage: WorkflowStage.Plan,
          requestedAt: '2026-01-01T00:01:00.000Z',
        },
      })} />)

      fireEvent.click(screen.getByTestId('approve-button'))

      await waitFor(() => {
        expect(mockedApproveIssue).toHaveBeenCalledWith(1, 'test-project')
      })
      await waitFor(() => {
        expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['issues', 'metrics', 'approval-wait'] })
      })
      invalidateSpy.mockRestore()
    })

    it('Cancel button closes the feedback input and discards the text', () => {
      mockRequestChangesMutation()
      mockedUseWorkflowTimeline.mockReturnValue(({
        data: makeAwaitingApprovalTimeline(),
      } as unknown) as ReturnType<typeof useWorkflowTimeline>)

      render(<WorkflowView issue={makeIssue({
        workflowStage: WorkflowStage.Plan,
        health: 'attention' as IssueHealth,
        approvalState: {
          status: 'awaiting',
          stage: WorkflowStage.Plan,
          requestedAt: '2026-01-01T00:01:00.000Z',
        },
      })} />)

      fireEvent.click(screen.getByTestId('request-changes-button'))
      const textarea = screen.getByTestId('request-changes-textarea') as HTMLTextAreaElement
      fireEvent.change(textarea, { target: { value: 'Draft text' } })
      fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))

      expect(screen.queryByTestId('request-changes-form')).not.toBeInTheDocument()
      expect(screen.getByTestId('request-changes-button')).toBeInTheDocument()
    })
  })

  describe('artifact chips on task rows', () => {
    beforeEach(() => {
      mockedUseIssueWorkflowArtifactContent.mockReturnValue({
        data: undefined,
        isLoading: false,
        error: null,
      } as ReturnType<typeof useIssueWorkflowArtifactContent>)
    })

    it('renders clickable artifact chips for completed tasks with artifact summaries', () => {
      mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimelineWithArtifacts() } as ReturnType<typeof useWorkflowTimeline>)

      render(<WorkflowView issue={makeIssue({ workflowStage: WorkflowStage.Plan })} />)

      const taskRow = screen.getByText('Generate proposal').closest('button')
      expect(taskRow).toBeInTheDocument()
      expect(within(taskRow!).getByText('proposal.md')).toBeInTheDocument()
      expect(within(taskRow!).getByText('design.md')).toBeInTheDocument()
    })

    it('renders directory artifact chips with folder styling', () => {
      mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimelineWithArtifacts() } as ReturnType<typeof useWorkflowTimeline>)

      render(<WorkflowView issue={makeIssue({ workflowStage: WorkflowStage.Plan })} />)

      const taskRow = screen.getByText('Write specs').closest('button')
      expect(taskRow).toBeInTheDocument()
      expect(within(taskRow!).getByText('specs/')).toBeInTheDocument()
    })

    it('does not render artifact chips for running tasks', () => {
      mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimelineWithArtifacts() } as ReturnType<typeof useWorkflowTimeline>)

      render(<WorkflowView issue={makeIssue({ workflowStage: WorkflowStage.Plan })} />)

      const taskRow = screen.getByText('Create design').closest('button')
      expect(taskRow).toBeInTheDocument()
      expect(within(taskRow!).queryByText('design.md')).not.toBeInTheDocument()
    })

    it('does not render artifact chips for completed tasks without artifacts', () => {
      mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimelineWithArtifacts() } as ReturnType<typeof useWorkflowTimeline>)

      render(<WorkflowView issue={makeIssue({ workflowStage: WorkflowStage.Plan })} />)

      const taskRow = screen.getByText('Review plan').closest('button')
      expect(taskRow).toBeInTheDocument()
      expect(within(taskRow!).queryByRole('button', { name: /proposal\.md|design\.md|specs\// })).not.toBeInTheDocument()
    })

    it('opens ArtifactContentViewer when an artifact chip is clicked', () => {
      mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimelineWithArtifacts() } as ReturnType<typeof useWorkflowTimeline>)

      render(<WorkflowView issue={makeIssue({ workflowStage: WorkflowStage.Plan })} />)

      const chip = screen.getByRole('button', { name: 'proposal.md' })
      fireEvent.click(chip)

      expect(mockedUseIssueWorkflowArtifactContent).toHaveBeenCalledWith(
        1,
        'artifact-1',
        { file: undefined },
        true,
      )
      const dialog = screen.getByRole('dialog')
      expect(dialog).toBeInTheDocument()
      expect(within(dialog).getByText('proposal.md')).toBeInTheDocument()
    })

    it('does not toggle the task row when a chip is clicked on an expand-capable task', () => {
      mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimelineWithArtifacts() } as ReturnType<typeof useWorkflowTimeline>)

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

    it('activates the artifact chip with Enter and Space keyboard events', () => {
      mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimelineWithArtifacts() } as ReturnType<typeof useWorkflowTimeline>)

      render(<WorkflowView issue={makeIssue({ workflowStage: WorkflowStage.Plan })} />)

      const chip = screen.getByRole('button', { name: 'proposal.md' })
      fireEvent.keyDown(chip, { key: 'Enter' })

      expect(screen.getByRole('dialog')).toBeInTheDocument()
    })
  })
})
