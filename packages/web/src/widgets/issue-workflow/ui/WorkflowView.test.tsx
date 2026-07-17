import { describe, expect, it, vi, beforeEach } from 'vitest'
import { QueryClient, useMutation } from '@tanstack/react-query'
import { screen, waitFor, fireEvent, within } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import type { ComponentProps, ReactElement } from 'react'
import { createQueryClient, render as renderWithProviders } from '../../../../tests/test-utils'
import { useMswServer } from '../../../../tests/support/msw'
import { WorkflowView as DefaultWorkflowView } from './WorkflowView'
import type { ArtifactContentHook } from './ArtifactContentViewer'
import type { StepListDependencies } from './InlineApproval'
import { IssueStatus, IssueHealth, WorkflowStage, type ApprovalFeedback, type Issue, type WorkflowTimeline, type useWorkflowTimeline } from '../../../entities/issue'
import type { WorkflowArtifactContentResult } from '../../../entities/issue/api/client'
import { setScopedValue } from '../../../../tests/support/scoped-property'

let approveRequests: string[] = []
let feedbackRequests: Array<{ issueNumber: number; stage: string; body: string }> = []
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
  useMutation<ApprovalFeedback, Error, { issueNumber: number; data: { stage: string; body: string } }>({
    mutationFn: async ({ issueNumber, data }) => {
      feedbackRequests.push({
        issueNumber,
        stage: data.stage,
        body: data.body,
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

const workflowDependencies: StepListDependencies = {
  approveIssue: approveIssueFn,
  requestChangesHook,
  artifactContentHook,
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

function setWorkflowTimeline(value: { data: WorkflowTimeline | null | undefined }) {
  timelineData = value.data
}

function render(ui: ReactElement) {
  const queryClient = createQueryClient()
  const queryKey = ['issues', 1, 'test-project', 'workflow-timeline']
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

  it('renders a scrollable stage stepper on mobile without clipping stage labels', async () => {
    setScopedValue(window, 'innerWidth', 390)
    setWorkflowTimeline({ data: makeTimeline() } as ReturnType<typeof useWorkflowTimeline>)

    render(<WorkflowView issue={makeIssue()} />)

    const stageBar = await screen.findByTestId('workflow-stage-bar-scrollable-stepper')
    expect(stageBar).toHaveClass('overflow-x-auto', 'flex-nowrap')
    expect(screen.queryByTestId('workflow-stage-bar')).not.toBeInTheDocument()

    for (const label of ['Plan', 'Build', 'Check', 'Integrate']) {
      const stageButton = screen.getByRole('button', { name: new RegExp(label, 'i') })
      expect(stageButton).toBeInTheDocument()
      expect(stageButton).toHaveClass('min-w-32', 'shrink-0')
      const labelNode = within(stageButton).getByText(label)
      expect(labelNode).toBeInTheDocument()
      expect(labelNode).toHaveClass('whitespace-nowrap')
      expect(labelNode).not.toHaveClass('truncate')
    }

    expect(screen.queryByRole('button', { name: /^done$/i })).not.toBeInTheDocument()
  })

  it('does not request workflow timeline for backlog issues', () => {
    setWorkflowTimeline({ data: undefined } as ReturnType<typeof useWorkflowTimeline>)

    render(<WorkflowView issue={makeIssue({ status: IssueStatus.Backlog, workflowStage: null })} />)

    expect(timelineRequests).toEqual([])
  })

  describe('InlineApproval - awaiting approval', () => {
    it('does not render or initialize approval controls when mounted read-only', () => {
      setWorkflowTimeline(({
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
      })} readOnly />)

      expect(screen.queryByText('Approval Required')).not.toBeInTheDocument()
      expect(screen.queryByTestId('approve-button')).not.toBeInTheDocument()
      expect(screen.queryByTestId('request-changes-button')).not.toBeInTheDocument()
      expect(feedbackRequests).toEqual([])
    })

    it('renders Approve and Request changes actions; no Reject or Send back labels', () => {
      setWorkflowTimeline(({
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
      setWorkflowTimeline({ data: makeTimeline() } as ReturnType<typeof useWorkflowTimeline>)

      render(<WorkflowView issue={makeIssue({
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Active,
      })} />)

      expect(screen.queryByTestId('request-changes-button')).not.toBeInTheDocument()
      expect(screen.queryByTestId('request-changes-disabled')).not.toBeInTheDocument()
      expect(screen.queryByText('Approval Required')).not.toBeInTheDocument()
    })

    it('Request changes action is hidden when stage is running', () => {
      setWorkflowTimeline({ data: makeTimeline() } as ReturnType<typeof useWorkflowTimeline>)

      render(<WorkflowView issue={makeIssue({
        workflowStage: WorkflowStage.Build,
        workflowStatus: 'running',
        health: IssueHealth.Active,
      })} />)

      expect(screen.queryByTestId('request-changes-button')).not.toBeInTheDocument()
    })

    it('clicking Request changes opens a text input for feedback body', () => {
      setWorkflowTimeline(({
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
      setWorkflowTimeline(({
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

    it('submitting feedback sends the stage and body through the request-changes mutation', async () => {
      setWorkflowTimeline(({
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

      await waitFor(() => {
        expect(feedbackRequests).toEqual([{
          issueNumber: 1,
          stage: 'plan',
          body: 'Please address the security findings',
        }])
      })
    })

    it('renders feedback history when feedback records exist', () => {
      setWorkflowTimeline(({
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
      setWorkflowTimeline(({
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
      setWorkflowTimeline(({
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
      // While the apply-feedback task is running, the stage is `Running`
      // and the server's `RequestChanges` clears the stage approval
      // state, so `issue.approvalState` is null. The InlineApproval
      // card is hidden. The feedback-history timeline should still
      // surface the open cycle.
      setWorkflowTimeline(({
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
      setWorkflowTimeline(({
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
        expect(approveRequests).toEqual(['1'])
      })
      await waitFor(() => {
        expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['issues', 'metrics', 'approval-wait'] })
      })
      invalidateSpy.mockRestore()
    })

    it('Cancel button closes the feedback input and discards the text', () => {
      setWorkflowTimeline(({
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
    it('renders clickable artifact chips for completed tasks with artifact summaries', () => {
      setWorkflowTimeline({ data: makeTimelineWithArtifacts() } as ReturnType<typeof useWorkflowTimeline>)

      render(<WorkflowView issue={makeIssue({ workflowStage: WorkflowStage.Plan })} />)

      const taskRow = screen.getByText('Generate proposal').closest('button')
      expect(taskRow).toBeInTheDocument()
      expect(within(taskRow!).getByText('proposal.md')).toBeInTheDocument()
      expect(within(taskRow!).getByText('design.md')).toBeInTheDocument()
    })

    it('renders directory artifact chips with folder styling', () => {
      setWorkflowTimeline({ data: makeTimelineWithArtifacts() } as ReturnType<typeof useWorkflowTimeline>)

      render(<WorkflowView issue={makeIssue({ workflowStage: WorkflowStage.Plan })} />)

      const taskRow = screen.getByText('Write specs').closest('button')
      expect(taskRow).toBeInTheDocument()
      expect(within(taskRow!).getByText('specs/')).toBeInTheDocument()
    })

    it('does not render artifact chips for running tasks', () => {
      setWorkflowTimeline({ data: makeTimelineWithArtifacts() } as ReturnType<typeof useWorkflowTimeline>)

      render(<WorkflowView issue={makeIssue({ workflowStage: WorkflowStage.Plan })} />)

      const taskRow = screen.getByText('Create design').closest('button')
      expect(taskRow).toBeInTheDocument()
      expect(within(taskRow!).queryByText('design.md')).not.toBeInTheDocument()
    })

    it('does not render artifact chips for completed tasks without artifacts', () => {
      setWorkflowTimeline({ data: makeTimelineWithArtifacts() } as ReturnType<typeof useWorkflowTimeline>)

      render(<WorkflowView issue={makeIssue({ workflowStage: WorkflowStage.Plan })} />)

      const taskRow = screen.getByText('Review plan').closest('button')
      expect(taskRow).toBeInTheDocument()
      expect(within(taskRow!).queryByRole('button', { name: /proposal\.md|design\.md|specs\// })).not.toBeInTheDocument()
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

    it('activates the artifact chip with Enter and Space keyboard events', () => {
      setWorkflowTimeline({ data: makeTimelineWithArtifacts() } as ReturnType<typeof useWorkflowTimeline>)

      render(<WorkflowView issue={makeIssue({ workflowStage: WorkflowStage.Plan })} />)

      const chip = screen.getByRole('button', { name: 'proposal.md' })
      fireEvent.keyDown(chip, { key: 'Enter' })

      expect(screen.getByRole('dialog')).toBeInTheDocument()
    })
  })
})
