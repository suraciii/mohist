// @vitest-environment jsdom
import { describe, expect, it, vi, beforeEach } from 'vitest'
import { screen, waitFor, fireEvent } from '@testing-library/react'
import { render } from '../../../../tests/test-utils'
import { LatestArtifactsPanel } from './LatestArtifactsPanel'
import { WorkflowView } from './WorkflowView'
import { IssueStatus, IssueHealth, WorkflowStage, type Issue, type WorkflowTimeline, type WorkflowArtifact } from '../../../entities/issue'
import { useIssueWorkflowArtifacts, useIssueWorkflowArtifactContent, useWorkflowTimeline } from '../../../entities/issue'

vi.mock('../../../entities/issue', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../../entities/issue')>()),
  useIssueWorkflowArtifacts: vi.fn(),
  useIssueWorkflowArtifactContent: vi.fn(),
  useWorkflowTimeline: vi.fn(),
}))

const mockedUseIssueWorkflowArtifacts = vi.mocked(useIssueWorkflowArtifacts)
const mockedUseIssueWorkflowArtifactContent = vi.mocked(useIssueWorkflowArtifactContent)
const mockedUseWorkflowTimeline = vi.mocked(useWorkflowTimeline)

function makeIssue(overrides: Partial<Issue> = {}): Issue {
  return {
    id: 'issue-1',
    number: 1,
    title: 'Implement workflow artifacts',
    body: '',
    status: IssueStatus.InProgress,
    workflowStage: WorkflowStage.Check,
    workflowRunId: 'workflow-run-1',
    health: IssueHealth.Active,
    projectId: 'test-project',
    labels: [],
    createdAt: '2026-01-01T00:00:00.000Z',
    updatedAt: '2026-01-01T00:00:00.000Z',
    comments: [],
    ...overrides,
  }
}

function makeFileArtifact(overrides: Partial<WorkflowArtifact> = {}): WorkflowArtifact {
  const path = overrides.path ?? 'review.md'
  return {
    artifactId: 'art-1',
    workflowRunId: 'workflow-run-1',
    taskRunId: 'ai-review.1',
    path,
    kind: 'file',
    contentType: 'text/markdown',
    size: 123,
    recordedAt: '2026-01-01T00:00:00.000Z',
    displayName: path,
    ...overrides,
  }
}

function makeDirectoryArtifact(overrides: Partial<WorkflowArtifact> = {}): WorkflowArtifact {
  const path = overrides.path ?? 'specs/'
  return {
    artifactId: 'art-dir-1',
    workflowRunId: 'workflow-run-1',
    taskRunId: 'plan.1',
    path,
    kind: 'directory',
    size: 456,
    recordedAt: '2026-01-01T00:00:00.000Z',
    displayName: path,
    ...overrides,
  }
}

function makeTimelineWithTaskArtifacts(): WorkflowTimeline {
  return {
    workflowRunId: 'workflow-run-1',
    status: 'Running',
    currentStage: WorkflowStage.Check,
    pendingWork: null,
    stages: [
      {
        stage: WorkflowStage.Check,
        status: 'running',
        order: 3,
        startedAt: '2026-01-01T00:00:00.000Z',
        completedAt: null,
        durationMs: null,
        tasks: [
          {
            id: 'ai-review.1',
            title: 'AI review',
            uses: 'mohist/acp-agent',
            status: 'completed',
            startedAt: '2026-01-01T00:00:00.000Z',
            completedAt: '2026-01-01T00:01:00.000Z',
            durationMs: 60000,
            attempts: 1,
            message: null,
            classification: 'UserFacing',
            artifactSummaries: [makeFileArtifact({ artifactId: 'art-review-1', taskRunId: 'ai-review.1' })],
          },
          {
            id: 'fix-review-findings.1',
            title: 'Fix review findings',
            uses: 'mohist/coder-agent',
            status: 'completed',
            startedAt: '2026-01-01T00:01:00.000Z',
            completedAt: '2026-01-01T00:02:00.000Z',
            durationMs: 60000,
            attempts: 1,
            message: null,
            classification: 'UserFacing',
            artifactSummaries: [],
          },
          {
            id: 'ai-review.2',
            title: 'AI review',
            uses: 'mohist/acp-agent',
            status: 'completed',
            startedAt: '2026-01-01T00:02:00.000Z',
            completedAt: '2026-01-01T00:03:00.000Z',
            durationMs: 60000,
            attempts: 1,
            message: null,
            classification: 'UserFacing',
            artifactSummaries: [
              makeFileArtifact({
                artifactId: 'art-review-2',
                taskRunId: 'ai-review.2',
                path: 'review.md',
                displayName: 'review.md',
              }),
            ],
          },
        ],
        checks: [],
        approval: null,
      },
    ],
    availableActions: [],
  }
}

async function expandTaskByTitle(taskTitle: string) {
  const titles = screen.getAllByText(taskTitle)
  for (const titleEl of titles) {
    const taskRow = titleEl.closest('[class*="rounded-md"]')
    const expandButton = taskRow?.querySelector('button')
    if (expandButton) {
      fireEvent.click(expandButton)
    }
  }
  await waitFor(() => {
    const mutedPanels = document.querySelectorAll('[class*="bg-muted"]')
    expect(mutedPanels.length).toBeGreaterThanOrEqual(titles.length)
  })
}

describe('LatestArtifactsPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockedUseIssueWorkflowArtifactContent.mockReturnValue({ data: undefined, isLoading: false, error: null } as ReturnType<typeof useIssueWorkflowArtifactContent>)
  })

  it('renders latest artifacts grouped by path', async () => {
    mockedUseIssueWorkflowArtifacts.mockReturnValue({
      data: [
        makeFileArtifact({ artifactId: 'art-proposal', path: 'proposal.md', taskRunId: 'plan.1' }),
        makeFileArtifact({ artifactId: 'art-review-2', path: 'review.md', taskRunId: 'ai-review.2' }),
      ],
      isLoading: false,
      error: null,
    } as ReturnType<typeof useIssueWorkflowArtifacts>)

    render(<LatestArtifactsPanel issueNumber={1} workflowRunId="workflow-run-1" />)

    await waitFor(() => {
      expect(screen.getByText('proposal.md')).toBeInTheDocument()
      expect(screen.getByText('review.md')).toBeInTheDocument()
    })
  })

  it('renders directory artifact as one collection', async () => {
    mockedUseIssueWorkflowArtifacts.mockReturnValue({
      data: [makeDirectoryArtifact({ artifactId: 'art-specs', path: 'specs/' })],
      isLoading: false,
      error: null,
    } as ReturnType<typeof useIssueWorkflowArtifacts>)

    render(<LatestArtifactsPanel issueNumber={1} workflowRunId="workflow-run-1" />)

    await waitFor(() => {
      expect(screen.getByText('specs/')).toBeInTheDocument()
      expect(screen.getByText('0 files')).toBeInTheDocument()
    })
  })

  it('opens recorded artifact content when latest artifact is clicked', async () => {
    mockedUseIssueWorkflowArtifacts.mockReturnValue({
      data: [makeFileArtifact({ artifactId: 'art-review-2', path: 'review.md', taskRunId: 'ai-review.2' })],
      isLoading: false,
      error: null,
    } as ReturnType<typeof useIssueWorkflowArtifacts>)

    mockedUseIssueWorkflowArtifactContent.mockReturnValue({
      data: { kind: 'text', content: 'PASS', contentType: 'text/markdown' },
      isLoading: false,
      error: null,
    } as ReturnType<typeof useIssueWorkflowArtifactContent>)

    render(<LatestArtifactsPanel issueNumber={1} workflowRunId="workflow-run-1" />)

    await waitFor(() => {
      expect(screen.getByText('review.md')).toBeInTheDocument()
    })

    fireEvent.click(screen.getByText('review.md'))

    await waitFor(() => {
      expect(screen.getByText('PASS')).toBeInTheDocument()
      expect(screen.getByText('Recorded artifact content')).toBeInTheDocument()
    })
  })

  it('renders directory entries and opens contained file content', async () => {
    mockedUseIssueWorkflowArtifacts.mockReturnValue({
      data: [makeDirectoryArtifact({ artifactId: 'art-specs', path: 'specs/' })],
      isLoading: false,
      error: null,
    } as ReturnType<typeof useIssueWorkflowArtifacts>)

    mockedUseIssueWorkflowArtifactContent.mockReturnValue({
      data: {
        kind: 'directory',
        entries: [{ relativePath: 'workflow.md', size: 100, contentType: 'text/markdown' }],
        totalSize: 100,
      },
      isLoading: false,
      error: null,
    } as ReturnType<typeof useIssueWorkflowArtifactContent>)

    render(<LatestArtifactsPanel issueNumber={1} workflowRunId="workflow-run-1" />)

    await waitFor(() => {
      expect(screen.getByText('specs/')).toBeInTheDocument()
    })

    fireEvent.click(screen.getByText('specs/'))

    await waitFor(() => {
      expect(screen.getByText('workflow.md')).toBeInTheDocument()
    })

    mockedUseIssueWorkflowArtifactContent.mockReturnValue({
      data: { kind: 'text', content: '# Spec', contentType: 'text/markdown' },
      isLoading: false,
      error: null,
    } as ReturnType<typeof useIssueWorkflowArtifactContent>)

    fireEvent.click(screen.getByText('workflow.md'))

    await waitFor(() => {
      expect(screen.getByText('# Spec')).toBeInTheDocument()
    })
  })
})

describe('Task artifact history rendering', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockedUseIssueWorkflowArtifactContent.mockReturnValue({ data: undefined, isLoading: false, error: null } as ReturnType<typeof useIssueWorkflowArtifactContent>)
  })

  it('renders artifacts on each task run row', async () => {
    mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimelineWithTaskArtifacts() } as ReturnType<typeof useWorkflowTimeline>)

    render(<WorkflowView issue={makeIssue()} />)

    await waitFor(() => {
      expect(screen.getAllByText('AI review').length).toBeGreaterThanOrEqual(2)
    })

    await expandTaskByTitle('AI review')

    await waitFor(() => {
      expect(screen.getAllByText('review.md').length).toBeGreaterThanOrEqual(1)
    })
  })

  it('preserves historical review artifact on ai-review.1 row after ai-review.2 runs', async () => {
    mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimelineWithTaskArtifacts() } as ReturnType<typeof useWorkflowTimeline>)

    render(<WorkflowView issue={makeIssue()} />)

    await waitFor(() => {
      expect(screen.getAllByText('AI review').length).toBeGreaterThanOrEqual(2)
    })

    await expandTaskByTitle('AI review')

    await waitFor(() => {
      const chips = screen.getAllByText('review.md')
      expect(chips.length).toBe(2)
    })
  })
})
