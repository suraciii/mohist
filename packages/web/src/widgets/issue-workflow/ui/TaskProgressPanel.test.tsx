import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query'
import { recordedInvokes } from '../../../../tests/support/signalr-fake'
import { ProjectProvider } from '../../../entities/project'
import {
  TaskProgressPanel as DefaultTaskProgressPanel,
  type TaskProgressPanelProps,
  type TaskProgressTimelineHook,
} from './TaskProgressPanel'
import type { TaskLogDataHook } from './TaskLogPanel'
import { setScopedProperty } from '../../../../tests/support/scoped-property'
import {
  issueWorkflowTaskLogQueryOptions,
  WorkflowStage,
  type WorkflowTimeline,
  type TaskLogLine,
  type TaskLogPage,
} from '../../../entities/issue'

type LogResponse = TaskLogPage | 'loading' | 'error' | 'missing'

let timelineData: WorkflowTimeline = makeTimeline()
let logResponse: LogResponse = { lines: [], nextCursor: null, truncated: false }
let taskLogRequests: Array<{ issueNumber: string; taskId: string; limit: string | null }> = []
const queryClients = new Set<QueryClient>()

const timelineHook: TaskProgressTimelineHook = () => ({ data: timelineData })

const taskLogHook: TaskLogDataHook = ({ issueNumber, taskId, projectId, workflowRunId }) =>
  useQuery({
    ...issueWorkflowTaskLogQueryOptions(
      projectId,
      issueNumber,
      taskId,
      { limit: 5000 },
      true,
      workflowRunId,
    ),
    queryFn: async () => {
      taskLogRequests.push({
        issueNumber: String(issueNumber),
        taskId,
        limit: '5000',
      })
      if (logResponse === 'loading') return new Promise<never>(() => {})
      if (logResponse === 'error') throw new Error('log unavailable')
      if (logResponse === 'missing') return { lines: [], nextCursor: null, truncated: false }
      return logResponse
    },
  })

function TaskProgressPanel(
  props: Omit<TaskProgressPanelProps, 'timelineHook' | 'taskLogHook'>,
) {
  return (
    <DefaultTaskProgressPanel
      {...props}
      timelineHook={timelineHook}
      taskLogHook={taskLogHook}
    />
  )
}

const projects = [
  {
    id: 'proj-1',
    name: 'Project 1',
    path: '/tmp/p1',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    repositories: [],
  },
]

function makeTimeline(): WorkflowTimeline {
  return {
    workflowRunId: 'workflow-run-1',
    status: 'Running',
    currentStage: WorkflowStage.Build,
    pendingWork: null,
    stages: [
      {
        stage: WorkflowStage.Build,
        status: 'failed',
        order: 2,
        startedAt: '2026-01-01T00:00:00.000Z',
        completedAt: null,
        durationMs: null,
        tasks: [
          {
            id: 'build-task-1',
            title: 'Rebase onto master',
            uses: 'mohist/publish-via-pr',
            status: 'failed',
            startedAt: '2026-01-01T00:00:00.000Z',
            completedAt: '2026-01-01T00:01:00.000Z',
            durationMs: 60000,
            attempts: 1,
            message: 'Rebase failed: CONFLICT (content): Merge conflict in src/foo.ts',
            output: { rebaseConflict: { conflictingFile: 'src/foo.ts' } },
          },
          {
            id: 'build-task-2',
            title: 'Build artifacts',
            uses: 'mohist/coder-agent',
            status: 'completed',
            startedAt: '2026-01-01T00:01:00.000Z',
            completedAt: '2026-01-01T00:02:00.000Z',
            durationMs: 60000,
            attempts: 1,
            message: null,
            output: null,
          },
        ],
        checks: [],
        approval: null,
      },
    ],
    availableActions: [],
  }
}

function makeRunningTimeline(): WorkflowTimeline {
  const timeline = makeTimeline()
  return {
    ...timeline,
    status: 'Running',
    stages: [
      {
        ...timeline.stages[0],
        status: 'running',
        tasks: [
          {
            id: 'build-running-1',
            title: 'Generate OpenSpec',
            uses: 'mohist/openspec',
            status: 'running',
            startedAt: '2026-01-01T00:00:00.000Z',
            completedAt: null,
            durationMs: null,
            attempts: 1,
            message: null,
            output: null,
          },
        ],
        checks: [],
        approval: null,
      },
    ],
  }
}

function setLogPage(page: TaskLogPage | { isLoading: true } | { isError: true } | undefined) {
  if (page === undefined) {
    logResponse = 'missing'
    return
  }
  if ('isLoading' in page) {
    logResponse = 'loading'
    return
  }
  if ('isError' in page) {
    logResponse = 'error'
    return
  }
  logResponse = page
}

function setWorkflowTimeline(value: { data: WorkflowTimeline }) {
  timelineData = value.data
}

async function expandFailedTask(taskTitle: string) {
  const row = (await screen.findByText(taskTitle)).closest('[class*="rounded-md border"]') as HTMLElement | null
  expect(row).not.toBeNull()
  const expandButton = row!.querySelector('button') as HTMLButtonElement | null
  expect(expandButton).not.toBeNull()
  fireEvent.click(expandButton!)
  await act(async () => {
    await Promise.resolve()
  })
}

function makeLine(overrides: Partial<TaskLogLine>): TaskLogLine {
  return {
    seq: 1,
    timestamp: '2026-07-03T08:00:00.000Z',
    source: 'action:rebase',
    text: 'default',
    ...overrides,
  }
}

function renderWithQueryClient(ui: React.ReactNode) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  queryClients.add(queryClient)
  const rendered = render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
        {ui}
      </ProjectProvider>
    </QueryClientProvider>,
  )
  return { ...rendered, queryClient }
}

beforeEach(() => {
  timelineData = makeTimeline()
  logResponse = { lines: [], nextCursor: null, truncated: false }
  taskLogRequests = []
})

afterEach(() => {
  cleanup()
  for (const queryClient of queryClients) queryClient.clear()
  queryClients.clear()
})

describe('TaskProgressPanel — task execution log panel', () => {
  it('allows expanding a running task and subscribes its log panel for live updates', async () => {
    setWorkflowTimeline({ data: makeRunningTimeline() })
    setLogPage({ lines: [], nextCursor: null, truncated: false })

    renderWithQueryClient(<TaskProgressPanel issueNumber={161} currentStage={WorkflowStage.Build} isAgentRunning={true} />)

    await expandFailedTask('Generate OpenSpec')

    expect(await screen.findByTestId('task-log-panel')).toBeInTheDocument()
    await waitFor(() => {
      expect(recordedInvokes.some((inv) => inv.method === 'SubscribeTaskLogAsync')).toBe(true)
    })
  })

  it('renders the task log panel inside the expanded region of a failed task', async () => {
    setWorkflowTimeline({ data: makeTimeline() })
    setLogPage( { lines: [], nextCursor: null, truncated: false })

    renderWithQueryClient(<TaskProgressPanel issueNumber={161} currentStage={WorkflowStage.Build} isAgentRunning={false} />)

    await expandFailedTask('Rebase onto master')

    await waitFor(() => {
      expect(taskLogRequests.length).toBeGreaterThan(0)
    })
    await waitFor(() => {
      expect(screen.getByTestId('task-log-panel')).toBeInTheDocument()
    })
  })

  it('does NOT alter the existing failure-kind guidance or output rendering', async () => {
    setWorkflowTimeline({ data: makeTimeline() })
    setLogPage( { lines: [], nextCursor: null, truncated: false })

    renderWithQueryClient(<TaskProgressPanel issueNumber={161} currentStage={WorkflowStage.Build} isAgentRunning={false} />)

    await expandFailedTask('Rebase onto master')

    expect(screen.getByText('Rebase failed: CONFLICT (content): Merge conflict in src/foo.ts')).toBeInTheDocument()
    expect(screen.getByText(/rebaseConflict/)).toBeInTheDocument()

    expect(screen.getByTestId('task-log-panel')).toBeInTheDocument()
  })

  it('renders each line with source label, timestamp, and text', async () => {
    setWorkflowTimeline({ data: makeTimeline() })
    setLogPage( {
      lines: [
        makeLine({ seq: 1, timestamp: '2026-07-03T08:00:00.000Z', source: 'workspace-prep', text: 'Cloning repo' }),
        makeLine({ seq: 2, timestamp: '2026-07-03T08:00:00.050Z', source: 'action:rebase', text: 'CONFLICT (content): Merge conflict in src/foo.ts' }),
        makeLine({ seq: 3, timestamp: '2026-07-03T08:00:00.100Z', source: 'cleanup', text: 'Recovering stale index.lock' }),
      ],
      nextCursor: null,
      truncated: false,
    })

    renderWithQueryClient(<TaskProgressPanel issueNumber={161} currentStage={WorkflowStage.Build} isAgentRunning={false} />)

    await expandFailedTask('Rebase onto master')

    await waitFor(() => {
      expect(screen.getByText('Cloning repo')).toBeInTheDocument()
    })
    expect(screen.getByText('[workspace-prep]')).toBeInTheDocument()
    expect(screen.getByText('08:00:00.000')).toBeInTheDocument()
    expect(screen.getByText('[action:rebase]')).toBeInTheDocument()
    expect(screen.getByText('[cleanup]')).toBeInTheDocument()
    expect(screen.getByText('CONFLICT (content): Merge conflict in src/foo.ts')).toBeInTheDocument()
    expect(screen.getByText('Recovering stale index.lock')).toBeInTheDocument()
    expect(screen.getByTestId('task-log-scroll')).toBeInTheDocument()
  })

  it('exposes the failing command output by scrolling the panel', async () => {
    setWorkflowTimeline({ data: makeTimeline() })
    setLogPage( {
      lines: [
        makeLine({ seq: 1, source: 'action:rebase', text: 'Patch failed at 0001 feat: add foo' }),
        makeLine({ seq: 2, source: 'action:rebase', text: 'CONFLICT (content): Merge conflict in src/foo.ts' }),
      ],
      nextCursor: null,
      truncated: false,
    })

    renderWithQueryClient(<TaskProgressPanel issueNumber={161} currentStage={WorkflowStage.Build} isAgentRunning={false} />)

    await expandFailedTask('Rebase onto master')

    const scroll = await waitFor(() => screen.getByTestId('task-log-scroll') as HTMLElement)
    expect(scroll.className).toContain('overflow-y-auto')
    expect(scroll.className).toContain('max-h-64')
    expect(screen.getByText('Patch failed at 0001 feat: add foo')).toBeInTheDocument()
    expect(screen.getByText('CONFLICT (content): Merge conflict in src/foo.ts')).toBeInTheDocument()
  })

  it('scrolls to the retained tail when log lines render', async () => {
    setScopedProperty(HTMLElement.prototype, 'scrollHeight', {
      configurable: true,
      get(this: HTMLElement) {
        return this.getAttribute('data-testid') === 'task-log-scroll' ? 1234 : 0
      },
    })

    setWorkflowTimeline({ data: makeTimeline() })
    setLogPage({
      lines: [
        makeLine({ seq: 4999, source: 'action:rebase', text: 'CONFLICT (content): Merge conflict in src/foo.ts' }),
        makeLine({ seq: 5000, source: 'action:rebase', text: 'Patch failed at 0001 feat: add foo' }),
      ],
      nextCursor: null,
      truncated: true,
    })

    renderWithQueryClient(<TaskProgressPanel issueNumber={161} currentStage={WorkflowStage.Build} isAgentRunning={false} />)

    await expandFailedTask('Rebase onto master')

    const scroll = await waitFor(() => screen.getByTestId('task-log-scroll') as HTMLElement)
    await waitFor(() => expect(scroll.scrollTop).toBe(1234))
  })

  it('shows a truncation indicator when the response reports truncated: true', async () => {
    setWorkflowTimeline({ data: makeTimeline() })
    setLogPage( {
      lines: [
        makeLine({ seq: 4999, source: 'action:rebase', text: 'CONFLICT (content): Merge conflict in src/foo.ts' }),
        makeLine({ seq: 5000, source: 'action:rebase', text: 'Patch failed at 0001 feat: add foo' }),
      ],
      nextCursor: null,
      truncated: true,
    })

    renderWithQueryClient(<TaskProgressPanel issueNumber={161} currentStage={WorkflowStage.Build} isAgentRunning={false} />)

    await expandFailedTask('Rebase onto master')

    expect(await screen.findByTestId('task-log-truncation-indicator')).toBeInTheDocument()
    expect(screen.getByText('CONFLICT (content): Merge conflict in src/foo.ts')).toBeInTheDocument()
    expect(screen.getByText('Patch failed at 0001 feat: add foo')).toBeInTheDocument()
  })

  it('does NOT show the truncation indicator when truncated: false', async () => {
    setWorkflowTimeline({ data: makeTimeline() })
    setLogPage( {
      lines: [makeLine({ seq: 1, source: 'action:rebase', text: 'ok' })],
      nextCursor: null,
      truncated: false,
    })

    renderWithQueryClient(<TaskProgressPanel issueNumber={161} currentStage={WorkflowStage.Build} isAgentRunning={false} />)

    await expandFailedTask('Rebase onto master')

    await waitFor(() => {
      expect(screen.getByTestId('task-log-panel')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('task-log-truncation-indicator')).not.toBeInTheDocument()
  })

  it('renders a graceful empty state when the task has no captured log', async () => {
    setWorkflowTimeline({ data: makeTimeline() })
    setLogPage( { lines: [], nextCursor: null, truncated: false })

    renderWithQueryClient(<TaskProgressPanel issueNumber={161} currentStage={WorkflowStage.Build} isAgentRunning={false} />)

    await expandFailedTask('Rebase onto master')

    expect(await screen.findByTestId('task-log-empty')).toBeInTheDocument()
    expect(screen.getByText(/No execution log captured/)).toBeInTheDocument()
  })

  it('renders the log panel for a completed task when expanded', async () => {
    setWorkflowTimeline({ data: makeTimeline() })
    setLogPage( {
      lines: [makeLine({ seq: 1, source: 'action:script', text: 'completed task output' })],
      nextCursor: null,
      truncated: false,
    })

    renderWithQueryClient(<TaskProgressPanel issueNumber={161} currentStage={WorkflowStage.Build} isAgentRunning={false} />)

    await expandFailedTask('Build artifacts')

    expect(await screen.findByText('completed task output')).toBeInTheDocument()
  })

  it('passes the issue-path query inputs with retention limit and workflowRunId', async () => {
    setWorkflowTimeline({ data: makeTimeline() })
    setLogPage( { lines: [], nextCursor: null, truncated: false })

    const { queryClient } = renderWithQueryClient(<TaskProgressPanel issueNumber={161} currentStage={WorkflowStage.Build} isAgentRunning={false} />)

    await expandFailedTask('Rebase onto master')

    await waitFor(() => {
      expect(taskLogRequests.length).toBeGreaterThan(0)
      expect(taskLogRequests.every((request) =>
        request.issueNumber === '161' &&
        request.taskId === 'build-task-1' &&
        request.limit === '5000',
      )).toBe(true)
    })
    expect(queryClient.getQueryState([
      161,
      'build-task-1',
      'proj-1',
      'workflow-run-1',
      'workflow-task-log',
      { limit: 5000 },
    ])).toBeDefined()
  })

  it('keeps the task row title and status icon rendering unchanged when the panel is expanded', async () => {
    setWorkflowTimeline({ data: makeTimeline() })
    setLogPage( { lines: [], nextCursor: null, truncated: false })

    renderWithQueryClient(<TaskProgressPanel issueNumber={161} currentStage={WorkflowStage.Build} isAgentRunning={false} />)

    await expandFailedTask('Rebase onto master')

    expect(screen.getByText('Rebase onto master')).toBeInTheDocument()
    expect(screen.getByText(/1 failed/)).toBeInTheDocument()
  })
})

describe('TaskProgressPanel — log query degrades gracefully', () => {
  it('renders empty state when query returns undefined data (e.g. 404 handled in hook)', async () => {
    setWorkflowTimeline({ data: makeTimeline() })
    setLogPage( undefined)

    renderWithQueryClient(<TaskProgressPanel issueNumber={161} currentStage={WorkflowStage.Build} isAgentRunning={false} />)

    await expandFailedTask('Rebase onto master')

    expect(await screen.findByTestId('task-log-empty')).toBeInTheDocument()
  })

  it('renders an "unavailable" placeholder when the query errors with a non-404 status', async () => {
    setWorkflowTimeline({ data: makeTimeline() })
    setLogPage( { isError: true })

    renderWithQueryClient(<TaskProgressPanel issueNumber={161} currentStage={WorkflowStage.Build} isAgentRunning={false} />)

    await expandFailedTask('Rebase onto master')

    expect(await screen.findByText(/Execution log unavailable/)).toBeInTheDocument()
  })

  it('renders a loading state when the query is in flight', async () => {
    setWorkflowTimeline({ data: makeTimeline() })
    setLogPage( { isLoading: true })

    renderWithQueryClient(<TaskProgressPanel issueNumber={161} currentStage={WorkflowStage.Build} isAgentRunning={false} />)

    await expandFailedTask('Rebase onto master')

    expect(await screen.findByText(/Loading execution log/)).toBeInTheDocument()
  })
})
