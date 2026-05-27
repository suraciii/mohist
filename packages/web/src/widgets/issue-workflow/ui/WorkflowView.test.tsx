import { describe, expect, it, vi } from 'vitest'
import { screen } from '@testing-library/react'
import { render } from '../../../../tests/test-utils'
import { WorkflowView } from './WorkflowView'
import { IssueStage, IssueStatus, WorkflowStage, type Issue, type WorkflowTimeline } from '../../../entities/issue'
import { useWorkflowTimeline } from '../../../entities/issue'

vi.mock('../../../entities/issue', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../../entities/issue')>()),
  useWorkflowTimeline: vi.fn(),
}))

const mockedUseWorkflowTimeline = vi.mocked(useWorkflowTimeline)

function makeIssue(overrides: Partial<Issue> = {}): Issue {
  return {
    id: 'issue-1',
    number: 1,
    title: 'Implement workflow naming',
    body: '',
    stage: IssueStage.InProgress,
    workflowStage: WorkflowStage.Build,
    status: IssueStatus.Active,
    projectId: 'test-project',
    labels: [],
    createdAt: '2026-01-01T00:00:00.000Z',
    updatedAt: '2026-01-01T00:00:00.000Z',
    comments: [],
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

describe('WorkflowView', () => {
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

  it('does not request workflow timeline for backlog issues', () => {
    mockedUseWorkflowTimeline.mockReturnValue({ data: undefined } as ReturnType<typeof useWorkflowTimeline>)

    render(<WorkflowView issue={makeIssue({ stage: IssueStage.Backlog, workflowStage: null })} />)

    expect(mockedUseWorkflowTimeline).toHaveBeenCalledWith(1, false)
  })

  it('renders approval actions when an attention issue is awaiting approval', () => {
    mockedUseWorkflowTimeline.mockReturnValue(({
      data: {
        ...makeTimeline(),
        status: 'AwaitingApproval',
        currentStage: WorkflowStage.Plan,
        pendingWork: null,
        stages: [
          {
            stage: WorkflowStage.Plan,
            status: 'awaitingApproval',
            order: 1,
            startedAt: '2026-01-01T00:00:00.000Z',
            completedAt: null,
            durationMs: null,
            tasks: [],
            checks: [],
            approval: {
              status: 'awaiting',
              requestedAt: '2026-01-01T00:01:00.000Z',
              respondedAt: null,
            },
          },
        ],
        availableActions: [{ name: 'approve', label: 'Approve', target: null }],
      },
    } as unknown) as ReturnType<typeof useWorkflowTimeline>)

    render(<WorkflowView issue={makeIssue({
      workflowStage: WorkflowStage.Plan,
      status: 'attention' as IssueStatus,
      approvalState: {
        status: 'awaiting',
        stage: WorkflowStage.Plan,
        requestedAt: '2026-01-01T00:01:00.000Z',
      },
    })} />)

    expect(screen.getByText('Approval Required')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Approve & Continue' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Send back' })).toBeInTheDocument()
  })
})
