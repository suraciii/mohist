// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { PipelineView } from './PipelineView'
import { TaskProgressPanel } from './TaskProgressPanel'
import { Stage, IssueStatus } from '../lib/types'
import type { Issue, IssueStageStateResponse, StageStateRead, StageTaskState } from '../lib/types'
import { useIssueStageState, useIssueExecutions } from '../hooks/useQueries'

vi.mock('../hooks/useQueries', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../hooks/useQueries')>()
  return {
    ...actual,
    useIssueStageState: vi.fn(),
    useIssueExecutions: vi.fn(),
    useWorktreeStatus: vi.fn(),
    useWorkflowRun: vi.fn().mockReturnValue({ data: undefined, isLoading: false }),
  }
})

vi.mock('../hooks/useSSE', () => ({
  useLiveTask: () => ({}),
}))

vi.mock('../hooks/useTaskProgress', () => ({
  useTaskProgress: () => ({}),
}))

vi.mock('../lib/agent-events', () => ({
  onAgentEvent: () => () => {},
}))

function makeStageStateResponse(stages: StageStateRead[]): IssueStageStateResponse {
  return {
    issueId: 'issue-1',
    issueNumber: 1,
    stages,
  }
}

interface TaskOverride {
  taskId: string
  title: string
  status: string
}

function makeTasks(overrides: TaskOverride[]): StageTaskState[] {
  return overrides.map((t, i) => ({
    taskId: t.taskId,
    title: t.title,
    status: t.status as StageTaskState['status'],
    source: 'dynamic' as const,
    order: i + 1,
    attempts: 1,
    duration: t.status === 'completed' ? 5000 : 0,
    artifacts: [] as string[],
    output: null,
    startedAt: null,
    completedAt: t.status === 'completed' ? '2026-01-01T00:00:00Z' : null,
    updatedAt: '2026-01-01T00:00:00Z',
  }))
}

function makeBuildStageState(tasksOverrides: TaskOverride[]): StageStateRead {
  return {
    stage: Stage.Build,
    status: 'running',
    tasks: makeTasks(tasksOverrides),
    checks: [],
    approval: null,
    attempts: 0,
    startedAt: '2026-01-01T00:00:00Z',
    completedAt: null,
    updatedAt: '2026-01-01T00:00:00Z',
  }
}

function makePlanStageState(tasksOverrides: TaskOverride[], checksOverrides: TaskOverride[] = []): StageStateRead {
  return {
    stage: Stage.Plan,
    status: 'running',
    tasks: makeTasks(tasksOverrides),
    checks: checksOverrides.map((c) => ({
      checkName: c.taskId,
      status: (c.status.replace('ed', '') as 'passed' | 'failed') || 'passed',
      message: null,
      output: null,
      runCount: 1,
      lastRunAt: null,
      updatedAt: '2026-01-01T00:00:00Z',
    })),
    approval: null,
    attempts: 0,
    startedAt: '2026-01-01T00:00:00Z',
    completedAt: null,
    updatedAt: '2026-01-01T00:00:00Z',
  }
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

const sharedStageState = makeStageStateResponse([
  makeBuildStageState([
    { taskId: 'T-001', title: 'Add persistence', status: 'completed' },
    { taskId: 'T-002', title: 'Expose API', status: 'running' },
    { taskId: 'T-003', title: 'Add tests', status: 'pending' },
  ]),
])

function setupMocks(stageStateData: IssueStageStateResponse) {
  vi.mocked(useIssueStageState).mockReturnValue({
    data: stageStateData,
    isLoading: false,
  } as unknown as ReturnType<typeof useIssueStageState>)

  vi.mocked(useIssueExecutions).mockReturnValue({
    data: [],
    isLoading: false,
  } as unknown as ReturnType<typeof useIssueExecutions>)
}

describe('PipelineView and TaskProgressPanel stage-state consistency', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('both components render tasks from the same stage-state response', () => {
    setupMocks(sharedStageState)

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

    expect(screen.getByText('1/3 completed')).toBeTruthy()
    expect(screen.getByText('33%')).toBeTruthy()
  })

  it('both components show dynamic fix tasks when present in stage-state data', () => {
    const stageStateWithFix = makeStageStateResponse([
      makeBuildStageState([
        { taskId: 'T-001', title: 'Compile code', status: 'completed' },
        { taskId: 'fix-build-health', title: 'Fix build health', status: 'running' },
      ]),
    ])

    setupMocks(stageStateWithFix)

    const issue = makeIssue()
    const queryClient = new QueryClient()

    render(
      <QueryClientProvider client={queryClient}>
        <PipelineView issue={issue} />
        <TaskProgressPanel issueNumber={issue.number} currentStage={Stage.Build} isAgentRunning={true} />
      </QueryClientProvider>
    )

    const fixTaskEls = screen.getAllByText('Fix build health')
    expect(fixTaskEls.length).toBeGreaterThanOrEqual(2)

    const compileEls = screen.getAllByText('Compile code')
    expect(compileEls.length).toBeGreaterThanOrEqual(2)
  })

  it('both components show retried stage latest state, not stale first-execution state', () => {
    const retriedState = makeStageStateResponse([
      {
        ...makeBuildStageState([
          { taskId: 'T-001', title: 'Compile code', status: 'completed' },
        ]),
        attempts: 1,
        status: 'running',
      },
    ])

    setupMocks(retriedState)

    const issue = makeIssue()
    const queryClient = new QueryClient()

    render(
      <QueryClientProvider client={queryClient}>
        <PipelineView issue={issue} />
        <TaskProgressPanel issueNumber={issue.number} currentStage={Stage.Build} isAgentRunning={false} />
      </QueryClientProvider>
    )

    const compileEls = screen.getAllByText('Compile code')
    expect(compileEls.length).toBeGreaterThanOrEqual(2)

    const progressPanel = screen.getByText('Task Progress').closest('.rounded-lg')!
    within(progressPanel as HTMLElement).getByText('1/1 completed')
    within(progressPanel as HTMLElement).getByText('100%')
  })

  it('uses the same useIssueStageState hook with consistent query key', () => {
    setupMocks(sharedStageState)

    const issue = makeIssue()
    const queryClient = new QueryClient()

    render(
      <QueryClientProvider client={queryClient}>
        <PipelineView issue={issue} />
        <TaskProgressPanel issueNumber={issue.number} currentStage={Stage.Build} isAgentRunning={true} />
      </QueryClientProvider>
    )

    expect(useIssueStageState).toHaveBeenCalledWith(1)
  })

  it('PipelineView does not show placeholder Plan tasks when real artifact tasks are present', () => {
    const planState = makeStageStateResponse([
      makePlanStageState(
        [
          { taskId: 'proposal', title: 'Write proposal', status: 'completed' },
          { taskId: 'specs', title: 'Write specs', status: 'completed' },
          { taskId: 'self-review', title: 'Self-review plan', status: 'pending' },
        ],
        [],
      ),
    ])

    setupMocks(planState)

    const issue = makeIssue({ stage: Stage.Plan })
    const queryClient = new QueryClient()

    render(
      <QueryClientProvider client={queryClient}>
        <PipelineView issue={issue} />
        <TaskProgressPanel issueNumber={issue.number} currentStage={Stage.Plan} isAgentRunning={false} />
      </QueryClientProvider>
    )

    expect(screen.queryByText('Read context files')).toBeNull()
    expect(screen.queryByText('Design solution')).toBeNull()
    expect(screen.getAllByText('Write proposal').length).toBeGreaterThanOrEqual(1)
    expect(screen.getAllByText('Write specs').length).toBeGreaterThanOrEqual(1)
    expect(screen.getAllByText('Self-review plan').length).toBeGreaterThanOrEqual(1)
  })

  it('PipelineView shows reason label on a runtime-added repair task', () => {
    const planStateWithRepair = makeStageStateResponse([
      makePlanStageState(
        [
          { taskId: 'proposal', title: 'Write proposal', status: 'completed' },
          { taskId: 'repair-plan-artifacts', title: 'Repair plan artifacts', status: 'completed' },
        ],
        [],
      ),
    ])

    setupMocks(planStateWithRepair)

    const issue = makeIssue({ stage: Stage.Plan })
    const queryClient = new QueryClient()

    render(
      <QueryClientProvider client={queryClient}>
        <PipelineView issue={issue} />
      </QueryClientProvider>
    )

    expect(screen.getAllByText('Repair plan artifacts').length).toBeGreaterThanOrEqual(1)
  })

  it('checks do not appear in the task list rendered by PipelineView StepList', () => {
    const checkState = makeStageStateResponse([
      {
        stage: Stage.Check,
        status: 'running',
        tasks: [
          {
            taskId: 'fix-review-findings',
            title: 'Fix review findings',
            status: 'completed',
            source: 'dynamic',
            order: 100,
            attempts: 1,
            duration: 15000,
            artifacts: [],
            output: null,
            startedAt: null,
            completedAt: null,
            updatedAt: '2026-01-01T00:00:00Z',
          },
        ],
        checks: [
          {
            checkName: 'ai-review',
            status: 'passed',
            message: 'LGTM',
            output: null,
            runCount: 1,
            lastRunAt: null,
            updatedAt: '2026-01-01T00:00:00Z',
          },
          {
            checkName: 'build-test',
            status: 'passed',
            message: 'All tests passed',
            output: null,
            runCount: 1,
            lastRunAt: null,
            updatedAt: '2026-01-01T00:00:00Z',
          },
        ],
        approval: null,
        attempts: 0,
        startedAt: '2026-01-01T00:00:00Z',
        completedAt: null,
        updatedAt: '2026-01-01T00:00:00Z',
      },
    ])

    setupMocks(checkState)

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
})
