// @vitest-environment jsdom
import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest'
import { screen, waitFor, fireEvent } from '@testing-library/react'
import { render } from '../../../../tests/test-utils'
import { WorkflowView } from './WorkflowView'
import { IssueStatus, IssueHealth, WorkflowStage, type Issue, type WorkflowTimeline } from '../../../entities/issue'
import { useWorkflowTimeline, getFileContent } from '../../../entities/issue'
import { IssueCard } from '../../kanban-board/ui/IssueCard'
import type { AgentStatus } from '../../../entities/agent'

vi.mock('../../../entities/issue', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../../entities/issue')>()),
  useWorkflowTimeline: vi.fn(),
  getFileContent: vi.fn(),
}))

const mockedUseWorkflowTimeline = vi.mocked(useWorkflowTimeline)
const mockedGetFileContent = vi.mocked(getFileContent)

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
    isDraft: false,
    canStart: true,
    blocker: null,
    ...overrides,
  }
}

function makeTimelineWithRequiredFiles(): WorkflowTimeline {
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
            requiredFiles: [
              { path: 'proposal.md', source: 'task-expect', canFetchContent: true },
              { path: 'design.md', source: 'task-expect', canFetchContent: true },
            ],
            classification: 'UserFacing',
          },
        ],
        checks: [],
        approval: null,
      },
    ],
    availableActions: [],
  }
}

function makeTimelineWithMarkerRequiredFiles(): WorkflowTimeline {
  return {
    workflowRunId: 'workflow-run-1',
    status: 'Running',
    currentStage: WorkflowStage.Build,
    pendingWork: null,
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
            id: 'check-task-1',
            title: 'Run health checks',
            uses: 'core/script',
            status: 'completed',
            startedAt: '2026-01-01T00:00:00.000Z',
            completedAt: '2026-01-01T00:01:00.000Z',
            durationMs: 60000,
            attempts: 1,
            message: 'all checks passed',
            requiredFiles: [
              { path: 'review.md', source: 'task-expect', canFetchContent: true, markers: ['PASS', 'FAIL'] },
              { path: 'self-review.md', source: 'task-expect', canFetchContent: false },
            ],
            classification: 'UserFacing',
          },
        ],
        checks: [],
        approval: null,
      },
    ],
    availableActions: [],
  }
}

function makeTimelineNoRequiredFiles(): WorkflowTimeline {
  return {
    workflowRunId: 'workflow-run-1',
    status: 'Running',
    currentStage: WorkflowStage.Build,
    pendingWork: {
      workId: 'build-task-1',
      workType: 'task',
      stage: WorkflowStage.Build,
      title: 'Build project',
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
            title: 'Build project',
            uses: 'mohist/coder-agent',
            status: 'completed',
            startedAt: '2026-01-01T00:00:00.000Z',
            completedAt: '2026-01-01T00:05:00.000Z',
            durationMs: 300000,
            attempts: 1,
            message: null,
            requiredFiles: undefined,
            classification: 'UserFacing',
          },
        ],
        checks: [],
        approval: null,
      },
    ],
    availableActions: [],
  }
}

const mockAgentStatus: AgentStatus = {
  running: false,
  issueId: null,
  issueNumber: null,
  activeAgents: [],
  capacity: { active: 0, max: 2 },
}

async function expandTask(taskTitle: string) {
  const taskRow = screen.getByText(taskTitle).closest('[class*="rounded-md"]')
  const expandButton = taskRow?.querySelector('button')
  if (expandButton) {
    fireEvent.click(expandButton)
    await waitFor(() => {
      expect(taskRow?.querySelector('[class*="bg-muted"]')).toBeInTheDocument()
    })
  }
}

describe('Task artifact rendering', () => {
  describe('required file entries appear on task rows', () => {
    it('renders required file paths for task with required files', async () => {
      mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimelineWithRequiredFiles() } as ReturnType<typeof useWorkflowTimeline>)

      render(<WorkflowView issue={makeIssue()} />)

      await waitFor(() => {
        expect(screen.getByText('Implement WorkflowView')).toBeInTheDocument()
      })

      await expandTask('Implement WorkflowView')

      await waitFor(() => {
        expect(screen.getByText('proposal.md')).toBeInTheDocument()
        expect(screen.getByText('design.md')).toBeInTheDocument()
      }, { timeout: 2000 })
    })

    it('marks required file entries with task-expect source indicator', async () => {
      mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimelineWithRequiredFiles() } as ReturnType<typeof useWorkflowTimeline>)

      render(<WorkflowView issue={makeIssue()} />)

      await waitFor(() => {
        expect(screen.getByText('Implement WorkflowView')).toBeInTheDocument()
      })

      await expandTask('Implement WorkflowView')

      await waitFor(() => {
        const proposalEntry = screen.getByText('proposal.md').closest('[class*="text-xs"]')
        expect(proposalEntry?.textContent).toContain('expect')
      }, { timeout: 2000 })
    })

    it('renders review.md required file entry when markers are declared', async () => {
      mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimelineWithMarkerRequiredFiles() } as ReturnType<typeof useWorkflowTimeline>)

      render(<WorkflowView issue={makeIssue()} />)

      await waitFor(() => {
        expect(screen.getByText('Run health checks')).toBeInTheDocument()
      })

      await expandTask('Run health checks')

      await waitFor(() => {
        expect(screen.getByText('review.md')).toBeInTheDocument()
      }, { timeout: 2000 })
    })

  it('shows self-review.md entry with disabled button indicator when canFetchContent is false', async () => {
    mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimelineWithMarkerRequiredFiles() } as ReturnType<typeof useWorkflowTimeline>)

    render(<WorkflowView issue={makeIssue()} />)

    await waitFor(() => {
      expect(screen.getByText('Run health checks')).toBeInTheDocument()
    })

    await expandTask('Run health checks')

    await waitFor(() => {
      expect(screen.getByText('self-review.md')).toBeInTheDocument()
    }, { timeout: 2000 })
  })
  })

  describe('tasks without required files remain compact', () => {
    it('does not render empty artifact chrome for task without required files', async () => {
      mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimelineNoRequiredFiles() } as ReturnType<typeof useWorkflowTimeline>)

      render(<WorkflowView issue={makeIssue()} />)

      await waitFor(() => {
        expect(screen.getByText('Build project')).toBeInTheDocument()
      })
    })

    it('task row shows no artifact list when requiredFiles is absent', async () => {
      mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimelineNoRequiredFiles() } as ReturnType<typeof useWorkflowTimeline>)

      render(<WorkflowView issue={makeIssue()} />)

      await waitFor(() => {
        expect(screen.getByText('Build project')).toBeInTheDocument()
      })

      const taskRows = document.querySelectorAll('[class*="rounded-md border"]')
      let foundArtifactList = false
      for (const row of taskRows) {
        const artifactContent = row.querySelector('[class*="bg-muted"]')
        if (artifactContent && artifactContent.textContent?.includes('proposal.md')) {
          foundArtifactList = true
        }
      }
      expect(foundArtifactList).toBe(false)
    })
  })
})

describe('On-demand required file viewer', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('calls file-content API and renders viewer panel when required file is selected', async () => {
    mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimelineWithRequiredFiles() } as ReturnType<typeof useWorkflowTimeline>)
    mockedGetFileContent.mockResolvedValue({ base: '# Design Proposal\n\nContent here', head: '' })

    render(<WorkflowView issue={makeIssue()} />)

    await waitFor(() => {
      expect(screen.getByText('Implement WorkflowView')).toBeInTheDocument()
    })

    await expandTask('Implement WorkflowView')

    fireEvent.click(screen.getByText('proposal.md'))

    expect(mockedGetFileContent).toHaveBeenCalled()
    expect(mockedGetFileContent).toHaveBeenCalledWith(1, 'proposal.md', 'test-project')
  })

  it('shows unavailable state when file-content API fails', async () => {
    mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimelineWithRequiredFiles() } as ReturnType<typeof useWorkflowTimeline>)
    mockedGetFileContent.mockRejectedValue(new Error('file not found'))

    render(<WorkflowView issue={makeIssue()} />)

    await waitFor(() => {
      expect(screen.getByText('Implement WorkflowView')).toBeInTheDocument()
    })

    await expandTask('Implement WorkflowView')

    fireEvent.click(screen.getByText('proposal.md'))

    await waitFor(() => {
      expect(screen.getByText('File content unavailable')).toBeInTheDocument()
    }, { timeout: 2000 })
  })

  it('does not call file-content API when canFetchContent is false', async () => {
    mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimelineWithMarkerRequiredFiles() } as ReturnType<typeof useWorkflowTimeline>)

    render(<WorkflowView issue={makeIssue()} />)

    await waitFor(() => {
      expect(screen.getByText('Run health checks')).toBeInTheDocument()
    })

    await expandTask('Run health checks')

    fireEvent.click(screen.getByText('self-review.md'))

    expect(mockedGetFileContent).not.toHaveBeenCalled()
  })

  it('closes and reopens viewer without refetching when content is already loaded', async () => {
    mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimelineWithRequiredFiles() } as ReturnType<typeof useWorkflowTimeline>)
    mockedGetFileContent.mockResolvedValue({ base: '# Design', head: '' })

    render(<WorkflowView issue={makeIssue()} />)

    await waitFor(() => {
      expect(screen.getByText('Implement WorkflowView')).toBeInTheDocument()
    })

    await expandTask('Implement WorkflowView')

    fireEvent.click(screen.getByText('proposal.md'))

    await waitFor(() => {
      expect(screen.getByText('# Design')).toBeInTheDocument()
    }, { timeout: 2000 })

    fireEvent.click(screen.getByText('proposal.md'))

    await waitFor(() => {
      expect(screen.queryByText('# Design')).not.toBeInTheDocument()
    })

    fireEvent.click(screen.getByText('proposal.md'))

    await waitFor(() => {
      expect(screen.getByText('# Design')).toBeInTheDocument()
      expect(mockedGetFileContent).toHaveBeenCalledTimes(1)
    }, { timeout: 2000 })
  })
})

describe('Board card stage progress', () => {
  it('displays compact progress fraction for active stage with non-zero total', () => {
    const issue = makeIssue({
      workflowStage: WorkflowStage.Build,
      status: IssueStatus.InProgress,
      health: IssueHealth.Active,
      workflowStageProgress: {
        stage: 'Build',
        total: 7,
        completed: 3,
        running: 1,
        failed: 0,
        currentTaskTitle: 'Implement feature X',
      },
    })

    render(<IssueCard issue={issue} agentStatus={mockAgentStatus} />)

    expect(screen.getByText('3/7')).toBeInTheDocument()
  })

  it('omits progress indicator when total is zero', () => {
    const issue = makeIssue({
      workflowStage: WorkflowStage.Build,
      status: IssueStatus.InProgress,
      health: IssueHealth.Active,
      workflowStageProgress: { stage: 'Build', total: 0, completed: 0, running: 0, failed: 0 },
    })

    render(<IssueCard issue={issue} agentStatus={mockAgentStatus} />)

    expect(screen.queryByText('/')).not.toBeInTheDocument()
  })

  it('omits progress indicator for backlog issues', () => {
    const issue = makeIssue({
      status: IssueStatus.Backlog,
      health: IssueHealth.Active,
      workflowStage: null,
      workflowStageProgress: { stage: 'Build', total: 7, completed: 3, running: 1, failed: 0 },
    })

    render(<IssueCard issue={issue} agentStatus={mockAgentStatus} />)

    expect(screen.queryByText('/')).not.toBeInTheDocument()
  })

  it('omits progress indicator for done issues', () => {
    const issue = makeIssue({
      status: IssueStatus.Done,
      health: IssueHealth.Done,
      workflowStage: WorkflowStage.Done,
      workflowStageProgress: { stage: 'Done', total: 0, completed: 0, running: 0, failed: 0 },
    })

    render(<IssueCard issue={issue} agentStatus={mockAgentStatus} />)

    expect(screen.queryByText('/')).not.toBeInTheDocument()
  })

  it('omits progress indicator for cancelled issues', () => {
    const issue = makeIssue({
      status: IssueStatus.Cancelled,
      health: IssueHealth.Cancelled,
      workflowStageProgress: undefined,
    })

    render(<IssueCard issue={issue} agentStatus={mockAgentStatus} />)

    expect(screen.queryByText('/')).not.toBeInTheDocument()
  })

  it('progress tooltip includes current task title when available', () => {
    const issue = makeIssue({
      workflowStage: WorkflowStage.Build,
      status: IssueStatus.InProgress,
      health: IssueHealth.Active,
      workflowStageProgress: {
        stage: 'Build',
        total: 7,
        completed: 3,
        running: 1,
        failed: 0,
        currentTaskTitle: 'Implement feature X',
      },
    })

    render(<IssueCard issue={issue} agentStatus={mockAgentStatus} />)

    const progressEl = screen.getByText('3/7')
    expect(progressEl.getAttribute('title')).toContain('Implement feature X')
  })

  it('hides progress when workflowStageProgress is null', () => {
    const issue = makeIssue({
      workflowStage: WorkflowStage.Build,
      status: IssueStatus.InProgress,
      health: IssueHealth.Active,
      workflowStageProgress: null,
    })

    render(<IssueCard issue={issue} agentStatus={mockAgentStatus} />)

    expect(screen.queryByText('/')).not.toBeInTheDocument()
  })
})
