// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { PipelineView } from './PipelineView'
import { TaskProgressPanel } from './TaskProgressPanel'
import { Stage, IssueStatus } from '../lib/types'
import type { Issue, IssueStageStateResponse, WorkflowRun, WorkflowStageRun } from '../lib/types'
import { useIssueStageState, useIssueExecutions, useWorkflowRun } from '../hooks/useQueries'

vi.mock('../hooks/useQueries', () => ({
  useIssueStageState: vi.fn(),
  useIssueExecutions: vi.fn(),
  useWorkflowRun: vi.fn(),
  useWorktreeStatus: vi.fn(),
}))

vi.mock('../hooks/useSSE', () => ({
  useLiveTask: () => ({}),
}))

vi.mock('../hooks/useTaskProgress', () => ({
  useTaskProgress: () => ({}),
}))

vi.mock('../lib/agent-events', () => ({
  onAgentEvent: () => () => {},
}))

function makeWorkflowStageRun(stage: Stage, tasks: any[], checks: any[], approval: any = null): WorkflowStageRun {
  return {
    stage,
    status: 'running',
    tasks,
    checks,
    approvalStatus: approval?.status ?? null,
    approvalOutput: approval?.output ?? null,
    approvalRequestedAt: approval?.requestedAt ?? null,
    approvalRespondedAt: approval?.respondedAt ?? null,
    attempts: 0,
    startedAt: '2026-01-01T00:00:00Z',
    completedAt: null,
  }
}

function makeWorkflowRun(stageRuns: WorkflowStageRun[]): WorkflowRun {
  return {
    id: 'wr_1_1704067200000',
    issueId: 'issue-1',
    issueNumber: 1,
    status: 'running',
    currentStage: Stage.Build,
    stageRuns,
  }
}

function makeWorkflowTasks(overrides: { taskId: string; title: string; status: string }[]): any[] {
  return overrides.map((t, i) => ({
    taskId: t.taskId,
    title: t.title,
    status: t.status,
    taskOrder: i + 1,
    attempts: 1,
    duration: t.status === 'completed' ? 5000 : 0,
    artifacts: [],
    output: null,
    reason: null,
    causedBy: null,
    startedAt: null,
    completedAt: t.status === 'completed' ? '2026-01-01T00:00:00Z' : null,
  }))
}

function makeIssue(overrides?: Partial<Issue>): Issue {
  return {
    id: 'issue-1',
    number: 1,
    title: 'Test Issue',
    stage: Stage.Build,
    status: IssueStatus.Active,
    projectId: 'proj-1',
    labels: [],
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    ...overrides,
  }
}

function setupWorkflowRunMocks(workflowRunData: WorkflowRun) {
  vi.mocked(useWorkflowRun).mockReturnValue({
    data: workflowRunData,
    isLoading: false,
  } as unknown as ReturnType<typeof useWorkflowRun>)

  vi.mocked(useIssueStageState).mockReturnValue({
    data: undefined,
    isLoading: false,
  } as unknown as ReturnType<typeof useIssueStageState>)

  vi.mocked(useIssueExecutions).mockReturnValue({
    data: [],
    isLoading: false,
  } as unknown as ReturnType<typeof useIssueExecutions>)
}

describe('WorkflowRun-backed task and check data consistency', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('PipelineView and TaskProgressPanel render the same WorkflowRun-backed task list', () => {
    const workflowRun = makeWorkflowRun([
      makeWorkflowStageRun(
        Stage.Build,
        makeWorkflowTasks([
          { taskId: 'T-001', title: 'Add persistence', status: 'completed' },
          { taskId: 'T-002', title: 'Expose API', status: 'running' },
          { taskId: 'T-003', title: 'Add tests', status: 'pending' },
        ]),
        [],
      ),
    ])
    setupWorkflowRunMocks(workflowRun)

    const issue = makeIssue()
    const queryClient = new QueryClient()

    render(
      <QueryClientProvider client={queryClient}>
        <PipelineView issue={issue} />
        <TaskProgressPanel issueNumber={issue.number} currentStage={Stage.Build} isAgentRunning={true} />
      </QueryClientProvider>
    )

    expect(screen.getAllByText('Add persistence').length).toBeGreaterThanOrEqual(2)
    expect(screen.getAllByText('Expose API').length).toBeGreaterThanOrEqual(2)
    expect(screen.getAllByText('Add tests').length).toBeGreaterThanOrEqual(2)

    const progressPanel = screen.getByText('Task Progress').closest('.rounded-lg')!
    within(progressPanel as HTMLElement).getByText('Add persistence')
    within(progressPanel as HTMLElement).getByText('Expose API')
    within(progressPanel as HTMLElement).getByText('Add tests')
  })

  it('TaskProgressPanel prefers WorkflowRun data over stage-state fallback', () => {
    const workflowRun = makeWorkflowRun([
      makeWorkflowStageRun(
        Stage.Build,
        makeWorkflowTasks([
          { taskId: 'T-001', title: 'WorkflowRun task', status: 'completed' },
        ]),
        [],
      ),
    ])
    setupWorkflowRunMocks(workflowRun)

    const issue = makeIssue()
    const queryClient = new QueryClient()

    render(
      <QueryClientProvider client={queryClient}>
        <TaskProgressPanel issueNumber={issue.number} currentStage={Stage.Build} isAgentRunning={true} />
      </QueryClientProvider>
    )

    expect(screen.getAllByText('WorkflowRun task').length).toBeGreaterThanOrEqual(1)
  })

  it('PipelineView shows runtime-added repair task with reason from WorkflowRun', () => {
    const workflowRun = makeWorkflowRun([
      makeWorkflowStageRun(
        Stage.Check,
        makeWorkflowTasks([
          { taskId: 'fix-review-findings', title: 'Fix review findings', status: 'completed' },
        ]),
        [],
      ),
    ])
    setupWorkflowRunMocks(workflowRun)

    const issue = makeIssue({ stage: Stage.Check })
    const queryClient = new QueryClient()

    render(
      <QueryClientProvider client={queryClient}>
        <PipelineView issue={issue} />
      </QueryClientProvider>
    )

    expect(screen.getAllByText('Fix review findings').length).toBeGreaterThanOrEqual(1)
  })

  it('checks appear in a separate check list, not in the task list', () => {
    const workflowRun = makeWorkflowRun([
      makeWorkflowStageRun(
        Stage.Check,
        makeWorkflowTasks([{ taskId: 'fix-review-findings', title: 'Fix review findings', status: 'completed' }]),
        [
          { checkName: 'ai-review', title: 'AI review', status: 'passed', message: 'LGTM', output: null, runCount: 1, lastRunAt: null },
          { checkName: 'build-test', title: 'Build test', status: 'passed', message: 'All tests passed', output: null, runCount: 1, lastRunAt: null },
        ],
      ),
    ])
    setupWorkflowRunMocks(workflowRun)

    const issue = makeIssue({ stage: Stage.Check })
    const queryClient = new QueryClient()

    render(
      <QueryClientProvider client={queryClient}>
        <PipelineView issue={issue} />
      </QueryClientProvider>
    )

    expect(screen.getAllByText('Fix review findings').length).toBeGreaterThanOrEqual(1)

    const tasksHeading = screen.getByRole('heading', { name: /tasks/i })
    const checksHeading = screen.getByRole('heading', { name: /checks/i })

    const tasksDiv = tasksHeading.closest('div')
    const checksDiv = checksHeading.closest('div')

    const checkNameSpansInTasks = tasksDiv ? Array.from(tasksDiv.querySelectorAll('span')).filter(el => el.textContent === 'ai-review' || el.textContent === 'build-test') : []
    expect(checkNameSpansInTasks).toHaveLength(0)

    const checkNameSpansInChecks = checksDiv ? Array.from(checksDiv.querySelectorAll('span')).filter(el => el.textContent === 'ai-review' || el.textContent === 'build-test') : []
    expect(checkNameSpansInChecks.length).toBeGreaterThan(0)
  })

  it('approval is not rendered as a top-level task row', () => {
    const workflowRun = makeWorkflowRun([
      makeWorkflowStageRun(
        Stage.Plan,
        makeWorkflowTasks([{ taskId: 'proposal', title: 'Write proposal', status: 'completed' }]),
        [],
        { status: 'awaiting', output: null, requestedAt: '2026-01-01T00:00:00Z', respondedAt: null },
      ),
    ])
    setupWorkflowRunMocks(workflowRun)

    const issue = makeIssue({ stage: Stage.Plan })
    const queryClient = new QueryClient()

    render(
      <QueryClientProvider client={queryClient}>
        <PipelineView issue={issue} />
      </QueryClientProvider>
    )

    expect(screen.getAllByText('Write proposal').length).toBeGreaterThanOrEqual(1)
    expect(screen.queryByText('awaiting')).toBeNull()
  })

  it('Both PipelineView and TaskProgressPanel prefer WorkflowRun when both data sources are available', () => {
    const workflowRun = makeWorkflowRun([
      makeWorkflowStageRun(
        Stage.Build,
        makeWorkflowTasks([{ taskId: 'T-001', title: 'WorkflowRun task', status: 'completed' }]),
        [],
      ),
    ])
    const stageStateFallback: IssueStageStateResponse = {
      issueId: 'issue-1',
      issueNumber: 1,
      stages: [
        {
          stage: Stage.Build,
          status: 'running',
          tasks: [
            {
              taskId: 'T-001',
              title: 'StageState task',
              status: 'completed',
              source: 'dynamic',
              order: 1,
              attempts: 1,
              duration: 5000,
              artifacts: [],
              output: null,
              startedAt: null,
              completedAt: '2026-01-01T00:00:00Z',
              updatedAt: '2026-01-01T00:00:00Z',
            },
          ],
          checks: [],
          approval: null,
          attempts: 0,
          startedAt: '2026-01-01T00:00:00Z',
          completedAt: null,
          updatedAt: '2026-01-01T00:00:00Z',
        },
      ],
    }

    vi.mocked(useWorkflowRun).mockReturnValue({
      data: workflowRun,
      isLoading: false,
    } as unknown as ReturnType<typeof useWorkflowRun>)

    vi.mocked(useIssueStageState).mockReturnValue({
      data: stageStateFallback,
      isLoading: false,
    } as unknown as ReturnType<typeof useIssueStageState>)

    vi.mocked(useIssueExecutions).mockReturnValue({
      data: [],
      isLoading: false,
    } as unknown as ReturnType<typeof useIssueExecutions>)

    const issue = makeIssue()
    const queryClient = new QueryClient()

    render(
      <QueryClientProvider client={queryClient}>
        <PipelineView issue={issue} />
        <TaskProgressPanel issueNumber={issue.number} currentStage={Stage.Build} isAgentRunning={true} />
      </QueryClientProvider>
    )

    expect(screen.getAllByText('WorkflowRun task').length).toBeGreaterThanOrEqual(2)
    expect(screen.queryByText('StageState task')).toBeNull()
  })
})
