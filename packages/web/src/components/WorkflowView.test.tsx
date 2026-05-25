import { describe, expect, it, vi } from 'vitest'
import { screen } from '@testing-library/react'
import { render } from '../../tests/test-utils'
import { WorkflowView } from './WorkflowView'
import { IssueStatus, Stage, type Issue, type WorkflowTimeline } from '../lib/types'
import { useWorkflowTimeline } from '../hooks/useQueries'

vi.mock('../hooks/useQueries', () => ({
  useWorkflowTimeline: vi.fn(),
}))

const mockedUseWorkflowTimeline = vi.mocked(useWorkflowTimeline)

function makeIssue(overrides: Partial<Issue> = {}): Issue {
  return {
    id: 'issue-1',
    number: 1,
    title: 'Implement workflow naming',
    body: '',
    stage: Stage.Build,
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
    currentStage: Stage.Build,
    pendingWork: {
      workId: 'build-task-1',
      workType: 'task',
      stage: Stage.Build,
      title: 'Implement WorkflowView',
      uses: 'mohist/agent',
    },
    stages: [
      {
        stage: Stage.Build,
        status: 'running',
        order: 2,
        startedAt: '2026-01-01T00:00:00.000Z',
        completedAt: null,
        durationMs: null,
        tasks: [
          {
            id: 'build-task-1',
            title: 'Implement WorkflowView',
            uses: 'mohist/agent',
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
            name: 'health:typecheck',
            title: 'Typecheck',
            uses: 'mohist/health-gate',
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
    expect(screen.getByText('runtime:agent')).toBeInTheDocument()
    expect(screen.getByText('runtime:health-gate')).toBeInTheDocument()
    expect(screen.queryByText('No tasks yet')).not.toBeInTheDocument()
  })

  it('does not request workflow timeline for backlog issues', () => {
    mockedUseWorkflowTimeline.mockReturnValue({ data: undefined } as ReturnType<typeof useWorkflowTimeline>)

    render(<WorkflowView issue={makeIssue({ stage: Stage.Backlog })} />)

    expect(mockedUseWorkflowTimeline).toHaveBeenCalledWith(1, false)
  })
})
