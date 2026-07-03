// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { TaskProgressPanel } from './TaskProgressPanel'
import {
  WorkflowStage,
  type WorkflowTimeline,
  type TaskLogLine,
  type TaskLogPage,
} from '../../../entities/issue'
import { useIssueWorkflowTaskLog, useWorkflowTimeline } from '../../../entities/issue'

vi.mock('../../../entities/issue', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../../entities/issue')>()),
  useWorkflowTimeline: vi.fn(),
  useIssueWorkflowTaskLog: vi.fn(),
}))

const mockedUseWorkflowTimeline = vi.mocked(useWorkflowTimeline)
const mockedUseIssueWorkflowTaskLog = vi.mocked(useIssueWorkflowTaskLog)

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
            output: JSON.stringify({ rebaseConflict: { conflictingFile: 'src/foo.ts' } }),
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

function setLogPage(page: TaskLogPage | { isLoading: true } | { isError: true } | undefined) {
  if (page === undefined) {
    mockedUseIssueWorkflowTaskLog.mockReturnValue({ data: undefined, isLoading: false, isError: false } as never)
    return
  }
  if ('isLoading' in page) {
    mockedUseIssueWorkflowTaskLog.mockReturnValue({ data: undefined, isLoading: true, isError: false } as never)
    return
  }
  if ('isError' in page) {
    mockedUseIssueWorkflowTaskLog.mockReturnValue({ data: undefined, isLoading: false, isError: true } as never)
    return
  }
  mockedUseIssueWorkflowTaskLog.mockReturnValue({ data: page, isLoading: false, isError: false } as never)
}

async function expandFailedTask(taskTitle: string) {
  const row = screen.getByText(taskTitle).closest('[class*="rounded-md border"]') as HTMLElement | null
  expect(row).not.toBeNull()
  const expandButton = row!.querySelector('button') as HTMLButtonElement | null
  expect(expandButton).not.toBeNull()
  fireEvent.click(expandButton!)
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

describe('TaskProgressPanel — task execution log panel', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('renders the task log panel inside the expanded region of a failed task', async () => {
    mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimeline() } as never)
    setLogPage( { lines: [], nextCursor: null, truncated: false })

    render(<TaskProgressPanel issueNumber={161} currentStage={WorkflowStage.Build} isAgentRunning={false} />)

    await expandFailedTask('Rebase onto master')

    expect(mockedUseIssueWorkflowTaskLog).toHaveBeenCalled()
    await waitFor(() => {
      expect(screen.getByTestId('task-log-panel')).toBeInTheDocument()
    })
  })

  it('does NOT alter the existing failure-kind guidance or output rendering', async () => {
    mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimeline() } as never)
    setLogPage( { lines: [], nextCursor: null, truncated: false })

    render(<TaskProgressPanel issueNumber={161} currentStage={WorkflowStage.Build} isAgentRunning={false} />)

    await expandFailedTask('Rebase onto master')

    expect(screen.getByText('Rebase failed: CONFLICT (content): Merge conflict in src/foo.ts')).toBeInTheDocument()
    expect(screen.getByText(/rebaseConflict/)).toBeInTheDocument()

    expect(screen.getByTestId('task-log-panel')).toBeInTheDocument()
  })

  it('renders each line with source label, timestamp, and text', async () => {
    mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimeline() } as never)
    setLogPage( {
      lines: [
        makeLine({ seq: 1, timestamp: '2026-07-03T08:00:00.000Z', source: 'workspace-prep', text: 'Cloning repo' }),
        makeLine({ seq: 2, timestamp: '2026-07-03T08:00:00.050Z', source: 'action:rebase', text: 'CONFLICT (content): Merge conflict in src/foo.ts' }),
        makeLine({ seq: 3, timestamp: '2026-07-03T08:00:00.100Z', source: 'cleanup', text: 'Recovering stale index.lock' }),
      ],
      nextCursor: null,
      truncated: false,
    })

    render(<TaskProgressPanel issueNumber={161} currentStage={WorkflowStage.Build} isAgentRunning={false} />)

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
    mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimeline() } as never)
    setLogPage( {
      lines: [
        makeLine({ seq: 1, source: 'action:rebase', text: 'Patch failed at 0001 feat: add foo' }),
        makeLine({ seq: 2, source: 'action:rebase', text: 'CONFLICT (content): Merge conflict in src/foo.ts' }),
      ],
      nextCursor: null,
      truncated: false,
    })

    render(<TaskProgressPanel issueNumber={161} currentStage={WorkflowStage.Build} isAgentRunning={false} />)

    await expandFailedTask('Rebase onto master')

    const scroll = await waitFor(() => screen.getByTestId('task-log-scroll') as HTMLElement)
    expect(scroll.className).toContain('overflow-y-auto')
    expect(scroll.className).toContain('max-h-64')
    expect(screen.getByText('Patch failed at 0001 feat: add foo')).toBeInTheDocument()
    expect(screen.getByText('CONFLICT (content): Merge conflict in src/foo.ts')).toBeInTheDocument()
  })

  it('scrolls to the retained tail when log lines render', async () => {
    const scrollHeightDescriptor = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'scrollHeight')
    Object.defineProperty(HTMLElement.prototype, 'scrollHeight', {
      configurable: true,
      get() {
        return this.getAttribute('data-testid') === 'task-log-scroll' ? 1234 : 0
      },
    })

    try {
      mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimeline() } as never)
      setLogPage({
        lines: [
          makeLine({ seq: 4999, source: 'action:rebase', text: 'CONFLICT (content): Merge conflict in src/foo.ts' }),
          makeLine({ seq: 5000, source: 'action:rebase', text: 'Patch failed at 0001 feat: add foo' }),
        ],
        nextCursor: null,
        truncated: true,
      })

      render(<TaskProgressPanel issueNumber={161} currentStage={WorkflowStage.Build} isAgentRunning={false} />)

      await expandFailedTask('Rebase onto master')

      const scroll = await waitFor(() => screen.getByTestId('task-log-scroll') as HTMLElement)
      await waitFor(() => expect(scroll.scrollTop).toBe(1234))
    } finally {
      if (scrollHeightDescriptor) {
        Object.defineProperty(HTMLElement.prototype, 'scrollHeight', scrollHeightDescriptor)
      } else {
        delete (HTMLElement.prototype as { scrollHeight?: number }).scrollHeight
      }
    }
  })

  it('shows a truncation indicator when the response reports truncated: true', async () => {
    mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimeline() } as never)
    setLogPage( {
      lines: [
        makeLine({ seq: 4999, source: 'action:rebase', text: 'CONFLICT (content): Merge conflict in src/foo.ts' }),
        makeLine({ seq: 5000, source: 'action:rebase', text: 'Patch failed at 0001 feat: add foo' }),
      ],
      nextCursor: null,
      truncated: true,
    })

    render(<TaskProgressPanel issueNumber={161} currentStage={WorkflowStage.Build} isAgentRunning={false} />)

    await expandFailedTask('Rebase onto master')

    expect(await screen.findByTestId('task-log-truncation-indicator')).toBeInTheDocument()
    expect(screen.getByText('CONFLICT (content): Merge conflict in src/foo.ts')).toBeInTheDocument()
    expect(screen.getByText('Patch failed at 0001 feat: add foo')).toBeInTheDocument()
  })

  it('does NOT show the truncation indicator when truncated: false', async () => {
    mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimeline() } as never)
    setLogPage( {
      lines: [makeLine({ seq: 1, source: 'action:rebase', text: 'ok' })],
      nextCursor: null,
      truncated: false,
    })

    render(<TaskProgressPanel issueNumber={161} currentStage={WorkflowStage.Build} isAgentRunning={false} />)

    await expandFailedTask('Rebase onto master')

    await waitFor(() => {
      expect(screen.getByTestId('task-log-panel')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('task-log-truncation-indicator')).not.toBeInTheDocument()
  })

  it('renders a graceful empty state when the task has no captured log', async () => {
    mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimeline() } as never)
    setLogPage( { lines: [], nextCursor: null, truncated: false })

    render(<TaskProgressPanel issueNumber={161} currentStage={WorkflowStage.Build} isAgentRunning={false} />)

    await expandFailedTask('Rebase onto master')

    expect(await screen.findByTestId('task-log-empty')).toBeInTheDocument()
    expect(screen.getByText(/No execution log captured/)).toBeInTheDocument()
  })

  it('renders the log panel for a completed task when expanded', async () => {
    mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimeline() } as never)
    setLogPage( {
      lines: [makeLine({ seq: 1, source: 'action:script', text: 'completed task output' })],
      nextCursor: null,
      truncated: false,
    })

    render(<TaskProgressPanel issueNumber={161} currentStage={WorkflowStage.Build} isAgentRunning={false} />)

    await expandFailedTask('Build artifacts')

    expect(await screen.findByText('completed task output')).toBeInTheDocument()
  })

  it('passes the issue-path query inputs with retention limit and workflowRunId', async () => {
    mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimeline() } as never)
    setLogPage( { lines: [], nextCursor: null, truncated: false })

    render(<TaskProgressPanel issueNumber={161} currentStage={WorkflowStage.Build} isAgentRunning={false} />)

    await expandFailedTask('Rebase onto master')

    await waitFor(() => {
      expect(mockedUseIssueWorkflowTaskLog).toHaveBeenCalledWith(161, 'build-task-1', { limit: 5000 }, true, 'workflow-run-1')
    })
  })

  it('keeps the task row title and status icon rendering unchanged when the panel is expanded', async () => {
    mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimeline() } as never)
    setLogPage( { lines: [], nextCursor: null, truncated: false })

    render(<TaskProgressPanel issueNumber={161} currentStage={WorkflowStage.Build} isAgentRunning={false} />)

    await expandFailedTask('Rebase onto master')

    expect(screen.getByText('Rebase onto master')).toBeInTheDocument()
    expect(screen.getByText(/1 failed/)).toBeInTheDocument()
  })
})

describe('TaskProgressPanel — log query degrades gracefully', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('renders empty state when query returns undefined data (e.g. 404 handled in hook)', async () => {
    mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimeline() } as never)
    setLogPage( undefined)

    render(<TaskProgressPanel issueNumber={161} currentStage={WorkflowStage.Build} isAgentRunning={false} />)

    await expandFailedTask('Rebase onto master')

    expect(await screen.findByTestId('task-log-empty')).toBeInTheDocument()
  })

  it('renders an "unavailable" placeholder when the query errors with a non-404 status', async () => {
    mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimeline() } as never)
    setLogPage( { isError: true })

    render(<TaskProgressPanel issueNumber={161} currentStage={WorkflowStage.Build} isAgentRunning={false} />)

    await expandFailedTask('Rebase onto master')

    expect(await screen.findByText(/Execution log unavailable/)).toBeInTheDocument()
  })

  it('renders a loading state when the query is in flight', async () => {
    mockedUseWorkflowTimeline.mockReturnValue({ data: makeTimeline() } as never)
    setLogPage( { isLoading: true })

    render(<TaskProgressPanel issueNumber={161} currentStage={WorkflowStage.Build} isAgentRunning={false} />)

    await expandFailedTask('Rebase onto master')

    expect(await screen.findByText(/Loading execution log/)).toBeInTheDocument()
  })
})
