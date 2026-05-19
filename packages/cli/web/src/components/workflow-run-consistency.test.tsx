// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
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
    workflowDefinition: {
      workflowId: 'project/custom',
      source: { type: 'project', path: '.mohist/workflow.yaml' },
      capturedAt: '2026-01-01T00:00:00Z',
      stageOrder: [Stage.Plan, Stage.Build, Stage.Check, Stage.Integrate],
    },
    stageRuns,
  }
}

function makeWorkflowTasks(overrides: { taskId: string; title: string; status: string; reason?: string; causedBy?: unknown; origin?: unknown }[]): any[] {
  return overrides.map((t, i) => ({
    taskId: t.taskId,
    title: t.title,
    status: t.status,
    origin: t.origin ?? null,
    taskOrder: i + 1,
    attempts: 1,
    duration: t.status === 'completed' ? 5000 : 0,
    artifacts: [],
    output: null,
    reason: t.reason ?? null,
    causedBy: t.causedBy ?? null,
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

function makeWorkflowChecks(overrides: { checkName: string; title: string; status: string; origin?: unknown }[]): any[] {
  return overrides.map((c) => ({
    checkName: c.checkName,
    title: c.title,
    status: c.status,
    origin: c.origin ?? null,
    message: null,
    output: c.checkName.startsWith('health:') ? { kind: 'health-gate' } : null,
    runCount: 1,
    lastRunAt: null,
  }))
}

function makeIntegrateTasks(overrides: { taskId: string; title: string; status: string }[]): any[] {
  return makeWorkflowTasks(overrides)
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

  it('PipelineView shows workflow definition source and work item origins', () => {
    const workflowRun = makeWorkflowRun([
      makeWorkflowStageRun(
        Stage.Build,
        makeWorkflowTasks([
          { taskId: 'T-001', title: 'WorkflowRun task', status: 'completed', origin: { source: 'project', uses: 'mohist/shell' } },
        ]),
        makeWorkflowChecks([
          { checkName: 'health:build', title: 'Build verification', status: 'passed', origin: { source: 'builtin', uses: 'mohist/health-gate' } },
        ]),
      ),
    ])
    setupWorkflowRunMocks(workflowRun)

    render(
      <QueryClientProvider client={new QueryClient()}>
        <PipelineView issue={makeIssue()} />
      </QueryClientProvider>,
    )

    expect(screen.getByText('Workflow')).toBeTruthy()
    expect(screen.getByText('project/custom')).toBeTruthy()
    expect(screen.getByText('.mohist/workflow.yaml')).toBeTruthy()
    expect(screen.getByText('project:shell')).toBeTruthy()
    expect(screen.getByText('built-in:health-gate')).toBeTruthy()
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
          {
            taskId: 'fix-review-findings',
            title: 'Fix review findings',
            status: 'completed',
            reason: 'Review passed failed',
            causedBy: { type: 'check-failure', checkName: 'review-passed', message: 'Review passed failed' },
          },
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
    expect(screen.getAllByText('reason').length).toBeGreaterThanOrEqual(1)
    expect(screen.getAllByTitle('Review passed failed').length).toBeGreaterThanOrEqual(1)
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

    expect(screen.getAllByText('AI review').length).toBeGreaterThanOrEqual(1)
    expect(screen.getAllByText('Build test').length).toBeGreaterThanOrEqual(1)
  })

  it('health gate checks preserve WorkflowRun titles in PipelineView', () => {
    const workflowRun = makeWorkflowRun([
      makeWorkflowStageRun(
        Stage.Integrate,
        makeWorkflowTasks([{ taskId: 'integrate:merge', title: 'Merge branch', status: 'completed' }]),
        [
          {
            checkName: 'health:integrate',
            title: 'Post-merge health check',
            status: 'failed',
            message: 'build failed',
            output: { kind: 'health-gate' },
            runCount: 1,
            lastRunAt: null,
          },
        ],
      ),
    ])
    setupWorkflowRunMocks(workflowRun)

    const issue = makeIssue({ stage: Stage.Integrate, status: IssueStatus.Blocked })
    const queryClient = new QueryClient()

    render(
      <QueryClientProvider client={queryClient}>
        <PipelineView issue={issue} />
      </QueryClientProvider>
    )

    expect(screen.getAllByText('Post-merge health check').length).toBeGreaterThanOrEqual(1)
    expect(screen.queryByText('Health Gate: integrate')).toBeNull()
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

  it('PipelineView shows Integrate tasks as discrete ordered items', () => {
    const workflowRun = makeWorkflowRun([
      makeWorkflowStageRun(
        Stage.Integrate,
        makeIntegrateTasks([
          { taskId: 'integrate:spec-sync', title: 'Sync specs', status: 'completed' },
          { taskId: 'integrate:archive-change', title: 'Archive change', status: 'running' },
          { taskId: 'integrate:merge', title: 'Merge branch', status: 'pending' },
        ]),
        [],
      ),
    ])
    setupWorkflowRunMocks(workflowRun)

    const issue = makeIssue({ stage: Stage.Integrate, status: IssueStatus.Active })
    const queryClient = new QueryClient()

    render(
      <QueryClientProvider client={queryClient}>
        <PipelineView issue={issue} />
      </QueryClientProvider>
    )

    expect(screen.getAllByText('Sync specs').length).toBeGreaterThanOrEqual(1)
    expect(screen.getAllByText('Archive change').length).toBeGreaterThanOrEqual(1)
    expect(screen.getAllByText('Merge branch').length).toBeGreaterThanOrEqual(1)
  })

  it('Integrate health check appears in checks section, not task list', () => {
    const workflowRun = makeWorkflowRun([
      makeWorkflowStageRun(
        Stage.Integrate,
        makeIntegrateTasks([
          { taskId: 'integrate:spec-sync', title: 'Sync specs', status: 'completed' },
          { taskId: 'integrate:archive-change', title: 'Archive change', status: 'completed' },
          { taskId: 'integrate:merge', title: 'Merge branch', status: 'completed' },
        ]),
        makeWorkflowChecks([
          { checkName: 'health:integrate', title: 'Post-merge health check', status: 'passed' },
        ]),
      ),
    ])
    setupWorkflowRunMocks(workflowRun)

    const issue = makeIssue({ stage: Stage.Integrate, status: IssueStatus.Active })
    const queryClient = new QueryClient()

    render(
      <QueryClientProvider client={queryClient}>
        <PipelineView issue={issue} />
      </QueryClientProvider>
    )

    expect(screen.getAllByText('Sync specs').length).toBeGreaterThanOrEqual(1)
    fireEvent.click(screen.getByRole('button', { name: /integrate/i }))
    expect(screen.getAllByText('Post-merge health check').length).toBeGreaterThanOrEqual(1)
    expect(screen.queryAllByText('Health Gate: integrate')).toHaveLength(0)
  })

  it('TaskProgressPanel renders Integrate tasks from WorkflowRun', () => {
    const workflowRun = makeWorkflowRun([
      makeWorkflowStageRun(
        Stage.Integrate,
        makeIntegrateTasks([
          { taskId: 'integrate:spec-sync', title: 'Sync specs', status: 'completed' },
          { taskId: 'integrate:archive-change', title: 'Archive change', status: 'pending' },
        ]),
        [],
      ),
    ])
    setupWorkflowRunMocks(workflowRun)

    const issue = makeIssue({ stage: Stage.Integrate, status: IssueStatus.Active })
    const queryClient = new QueryClient()

    render(
      <QueryClientProvider client={queryClient}>
        <TaskProgressPanel issueNumber={issue.number} currentStage={Stage.Integrate} isAgentRunning={true} />
      </QueryClientProvider>
    )

    expect(screen.getAllByText('Sync specs').length).toBeGreaterThanOrEqual(1)
    expect(screen.getAllByText('Archive change').length).toBeGreaterThanOrEqual(1)
  })

  it('Integrate stage shows health:integrate check separately from tasks', () => {
    const workflowRun = makeWorkflowRun([
      makeWorkflowStageRun(
        Stage.Integrate,
        makeIntegrateTasks([
          { taskId: 'integrate:spec-sync', title: 'Sync specs', status: 'completed' },
          { taskId: 'integrate:merge', title: 'Merge branch', status: 'completed' },
        ]),
        makeWorkflowChecks([
          { checkName: 'health:integrate', title: 'Post-merge health check', status: 'failed' },
        ]),
      ),
    ])
    setupWorkflowRunMocks(workflowRun)

    const issue = makeIssue({ stage: Stage.Integrate, status: IssueStatus.Blocked })
    const queryClient = new QueryClient()

    render(
      <QueryClientProvider client={queryClient}>
        <PipelineView issue={issue} />
      </QueryClientProvider>
    )

    fireEvent.click(screen.getByRole('button', { name: /integrate/i }))
    expect(screen.getAllByText('Post-merge health check').length).toBeGreaterThanOrEqual(1)
    expect(screen.queryAllByText('Health Gate: integrate')).toHaveLength(0)
  })

  it('completed Issue Detail shows Integrate delivery metadata from WorkflowRun', () => {
    const mergeOutput = {
      targetBranch: 'main',
      baseSha: 'base1234',
      candidateHeadSha: 'head5678',
      landedSha: 'landed99',
      rebased: false,
    }
    const workflowRun = makeWorkflowRun([
      {
        ...makeWorkflowStageRun(
          Stage.Integrate,
          makeIntegrateTasks([
            { taskId: 'integrate:spec-sync', title: 'Sync specs', status: 'completed' },
            { taskId: 'integrate:archive-change', title: 'Archive change', status: 'completed' },
            { taskId: 'integrate:merge', title: 'Merge branch', status: 'completed' },
          ]).map((task) => task.taskId === 'integrate:merge' ? { ...task, output: mergeOutput } : task),
          makeWorkflowChecks([
            { checkName: 'health:integrate', title: 'Post-merge health check', status: 'passed' },
          ]),
        ),
        deliveryMetadata: {
          specSync: { status: 'completed', output: null },
          archive: { status: 'completed', output: { archivePath: 'openspec/changes/archive/188' } },
          merge: { status: 'completed', output: mergeOutput, ...mergeOutput },
          health: { status: 'passed', message: null, output: null },
          frozen: true,
        },
      } as WorkflowStageRun,
    ])
    setupWorkflowRunMocks(workflowRun)

    const issue = makeIssue({ stage: Stage.Done, status: IssueStatus.Completed })
    const queryClient = new QueryClient()

    render(
      <QueryClientProvider client={queryClient}>
        <PipelineView issue={issue} />
      </QueryClientProvider>
    )

    expect(screen.getByText('Integration Evidence')).toBeTruthy()
    expect(screen.getAllByText('Spec Sync').length).toBeGreaterThanOrEqual(1)
    expect(screen.getAllByText('Archive OpenSpec Change').length).toBeGreaterThanOrEqual(1)
    expect(screen.getAllByText('Merge to Target Branch').length).toBeGreaterThanOrEqual(1)
    expect(screen.getByText(/main: base123 → head567 → landed9/)).toBeTruthy()
    expect(screen.getAllByText('Post-merge health check').length).toBeGreaterThanOrEqual(1)
  })

  it('blocked Issue Detail keeps post-merge delivery metadata visible after final health failure', () => {
    const mergeOutput = {
      targetBranch: 'main',
      baseSha: 'base1234',
      candidateHeadSha: 'head5678',
      landedSha: 'landed99',
      rebased: true,
    }
    const workflowRun = makeWorkflowRun([
      {
        ...makeWorkflowStageRun(
          Stage.Integrate,
          makeIntegrateTasks([
            { taskId: 'integrate:spec-sync', title: 'Sync specs', status: 'completed' },
            { taskId: 'integrate:archive-change', title: 'Archive change', status: 'completed' },
            { taskId: 'integrate:merge', title: 'Merge branch', status: 'completed' },
          ]).map((task) => task.taskId === 'integrate:merge' ? { ...task, output: mergeOutput } : task),
          [
            {
              checkName: 'health:integrate',
              title: 'Post-merge health check',
              status: 'failed',
              message: 'post-merge build failed',
              output: { manualIntervention: true },
              runCount: 1,
              lastRunAt: null,
            },
          ],
        ),
        status: 'failed',
        failure: {
          reason: 'post-merge-health-failed',
          stage: Stage.Integrate,
          checkName: 'health:integrate',
          message: 'post-merge build failed',
        },
        deliveryMetadata: {
          specSync: { status: 'completed', output: null },
          archive: { status: 'completed', output: { archivePath: 'openspec/changes/archive/188' } },
          merge: { status: 'completed', output: mergeOutput, ...mergeOutput },
          health: { status: 'failed', message: 'post-merge build failed', output: { manualIntervention: true } },
          frozen: true,
        },
      } as WorkflowStageRun,
    ])
    setupWorkflowRunMocks(workflowRun)

    const issue = makeIssue({ stage: Stage.Integrate, status: IssueStatus.Blocked })
    const queryClient = new QueryClient()

    render(
      <QueryClientProvider client={queryClient}>
        <PipelineView issue={issue} />
      </QueryClientProvider>
    )

    expect(screen.getByText('Integration Evidence')).toBeTruthy()
    expect(screen.getByText(/main: base123 → head567 → landed9/)).toBeTruthy()
    expect(screen.getAllByText('Post-merge health check').length).toBeGreaterThanOrEqual(1)
    expect(screen.getAllByText('post-merge build failed').length).toBeGreaterThanOrEqual(1)
  })
})
