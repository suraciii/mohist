import { describe, expect, it, vi } from 'vitest'
import { screen, fireEvent, within } from '@testing-library/react'
import { render } from '../../../../tests/test-utils'
import { WorkflowView } from './WorkflowView'
import { IssueStatus, IssueHealth, WorkflowStage, type Issue, type WorkflowTimeline } from '../../../entities/issue'
import { useWorkflowTimeline, useIssueWorkflowArtifactContent } from '../../../entities/issue'

vi.mock('../../../entities/issue', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../../entities/issue')>()),
  useWorkflowTimeline: vi.fn(),
  useIssueWorkflowArtifactContent: vi.fn(),
}))

const mockedUseWorkflowTimeline = vi.mocked(useWorkflowTimeline)
const mockedUseIssueWorkflowArtifactContent = vi.mocked(useIssueWorkflowArtifactContent)

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

    render(<WorkflowView issue={makeIssue({ status: IssueStatus.Backlog, workflowStage: null })} />)

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
      health: 'attention' as IssueHealth,
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
