// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { PipelineView } from './PipelineView'
import { TaskProgressPanel } from './TaskProgressPanel'
import { Stage, IssueStatus } from '../lib/types'
import type { Issue, IssueStageStateResponse, StageStateRead, StageTaskState } from '../lib/types'
import { useIssueStageState, useIssueExecutions } from '../hooks/useQueries'

vi.mock('../hooks/useQueries', () => ({
  useIssueStageState: vi.fn(),
  useIssueExecutions: vi.fn(),
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
})
