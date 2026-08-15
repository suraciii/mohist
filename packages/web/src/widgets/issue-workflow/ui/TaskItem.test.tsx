import { act, fireEvent, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { render } from '../../../../tests/test-utils'
import type { StageTaskState } from '../../../entities/issue'
import { TaskItem } from './TaskItem'
import type { TaskLogDataHook, WorkflowRunSessionsHook } from './TaskLogPanel'
import { flushAndGetLastConnection } from './_taskLogPanelTestUtils'
import { deferNextFakeConnectionStart, recordedInvokes } from '../../../../tests/support/signalr-fake'

const taskLogHook: TaskLogDataHook = ({ enabled }) => ({
  data:
    enabled === false
      ? undefined
      : {
          lines: [
            { seq: 1, timestamp: '2026-01-01T00:01:00.000Z', source: 'action:script', text: 'canonical log line' },
          ],
          nextCursor: null,
          truncated: false,
        },
  isLoading: false,
  isError: false,
})

const workflowSessionsHook: WorkflowRunSessionsHook = () => ({
  sessions: [{ id: 'build-session-id', sessionName: 'build-session' } as never],
  isLoading: false,
})

function makeTask(overrides: Partial<StageTaskState> = {}): StageTaskState {
  return {
    taskId: 'build-task-1',
    title: 'Canonical workflow task title',
    status: 'pending',
    order: 0,
    attempts: 1,
    duration: 0,
    artifacts: [],
    output: null,
    startedAt: null,
    completedAt: null,
    updatedAt: '',
    ...overrides,
  }
}

function renderTask(task: StageTaskState, logHook: TaskLogDataHook = taskLogHook) {
  return render(
    <TaskItem
      task={task}
      issueNumber={1}
      workflowRunId="workflow-run-1"
      taskLogHook={logHook}
      workflowSessionsHook={workflowSessionsHook}
      fileContentFn={async () => ({ base: '', head: '# required file' })}
    />,
  )
}

describe('TaskItem', () => {
  it('renders a task without revealable details as non-interactive content', () => {
    renderTask(makeTask())

    const row = screen.getByTestId('workflow-task-item')
    expect(screen.getByText('Canonical workflow task title')).toBeVisible()
    expect(row.querySelector('button')).toBeNull()
    expect(row).not.toHaveAttribute('aria-expanded')
  })

  it('renders a completed task with an empty captured log as non-interactive content', () => {
    const emptyLogHook: TaskLogDataHook = () => ({
      data: { lines: [], nextCursor: null, truncated: false },
      isLoading: false,
      isError: false,
    })
    renderTask(makeTask({ status: 'completed' }), emptyLogHook)

    const row = screen.getByTestId('workflow-task-item')
    expect(screen.getByText('Canonical workflow task title')).toBeVisible()
    expect(row.querySelector('button')).toBeNull()
  })

  it('keeps a running task inspectable and subscribes its canonical log panel', async () => {
    // The TaskLogPanel mounts on expand and opens a SignalR connection whose
    // `start()` resolves asynchronously; only after the connection resolves
    // does the subscription effect invoke SubscribeTaskLogAsync. Deferring the
    // connection start and flushing it inside act() (the same helper the
    // TaskLogPanel tests use) keeps this deterministic on slow CI runners
    // instead of polling a module global under a short waitFor timer.
    deferNextFakeConnectionStart()
    renderTask(makeTask({ status: 'running', startedAt: '2026-01-01T00:00:00.000Z' }))

    fireEvent.click(screen.getByRole('button', { name: 'Canonical workflow task title' }))

    const conn = await flushAndGetLastConnection()
    await conn.waitForInvoke('SubscribeTaskLogAsync')
    expect(recordedInvokes.some((invoke) => invoke.method === 'SubscribeTaskLogAsync')).toBe(true)
    expect(screen.getByTestId('task-log-panel')).toBeVisible()
  })

  it('renders recoverable interruption reason and deadline without failed treatment', async () => {
    renderTask(
      makeTask({
        status: 'recoverable-interrupted',
        interruption: {
          reasonCode: 'runner-lost',
          workId: 'build.1',
          ownerId: 'workflow-1',
          recordedAt: '2026-08-15T01:00:00Z',
          recoveryDeadlineAt: '2026-08-15T01:15:00Z',
        },
      }),
    )

    const disclosure = screen.getByRole('button', { name: /Canonical workflow task title/ })
    expect(disclosure).toHaveTextContent('recoverable-interrupted')
    fireEvent.click(disclosure)
    await act(async () => { await Promise.resolve() })

    const interruption = screen.getByTestId('workflow-task-recoverable-interruption')
    expect(interruption).toHaveTextContent('runner-lost')
    expect(interruption).toHaveTextContent('2026-08-15T01:15:00Z')
    expect(screen.queryByText('failed')).not.toBeInTheDocument()
  })

  it('renders blocked Agent settlement details without a failed task treatment', async () => {
    renderTask(
      makeTask({
        status: 'blocked',
        startedAt: '2026-08-14T10:56:58.000Z',
        agentResultSettlement: {
          state: 'blocked',
          reason: 'agent-result-unconfirmed',
          message: 'Runner disconnected before the Agent result was accepted.',
          firstUnknownAt: '2026-08-14T10:56:58.000Z',
          deadlineAt: '2026-08-14T11:01:58.000Z',
          taskRunId: 'build.1',
          workId: 'build.1',
          runnerId: 'runner-pluto',
          agentSessionId: 'session-1',
          agentTurnId: 'turn-1',
          nextAction: 'Restore the original Runner and allow the result to replay.',
          recoveryActions: ['stop'],
        },
      }),
    )

    const disclosure = screen.getByRole('button', { name: /Canonical workflow task title/ })
    expect(disclosure).toHaveTextContent('blocked')
    fireEvent.click(disclosure)
    await act(async () => {
      await Promise.resolve()
    })

    const attention = screen.getByTestId('workflow-task-blocked-attention')
    expect(attention).toHaveTextContent('Agent result unconfirmed')
    expect(attention).toHaveTextContent('session-1')
    expect(attention).toHaveTextContent('turn-1')
    expect(screen.queryByText('failed')).not.toBeInTheDocument()
  })

  it('keeps title primary, metadata actions separate, and expands all inspection details', async () => {
    const title = 'Complete the intentionally long workflow task title without allowing metadata to replace it'
    renderTask(
      makeTask({
        title,
        status: 'completed',
        attempts: 2,
        duration: 60000,
        startedAt: '2026-01-01T00:00:00.000Z',
        completedAt: '2026-01-01T00:01:00.000Z',
        origin: { source: 'runtime', uses: 'mohist/coder-agent' },
        sessionName: 'build-session',
        reason: 'Task produced review evidence',
        output: { result: 'complete' },
        requiredFiles: [{ path: 'openspec/changes/issue-454/design.md', source: 'task-expect', canFetchContent: true }],
        artifactSummaries: [
          {
            artifactId: 'artifact-1',
            path: 'openspec/changes/issue-454/proposal.md',
            kind: 'file',
            size: 42,
            recordedAt: '2026-01-01T00:01:00.000Z',
          },
        ],
      }),
    )

    const disclosure = screen.getByRole('button', { name: title })
    const artifact = screen.getByRole('button', { name: 'openspec/changes/issue-454/proposal.md' })
    const session = screen.getByRole('link', { name: /build-session/ })
    expect(disclosure).toHaveAttribute('aria-expanded', 'false')
    expect(screen.getByText(title)).toHaveClass('whitespace-normal', 'break-words')
    expect(disclosure.contains(artifact)).toBe(false)
    expect(disclosure.contains(session)).toBe(false)
    expect(screen.getByText('2 attempts')).toBeVisible()
    expect(screen.getByText('runtime:coder-agent')).toBeVisible()

    fireEvent.click(disclosure)
    await act(async () => {
      await Promise.resolve()
    })

    expect(disclosure).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByTestId('workflow-task-details')).toBeVisible()
    expect(screen.getByText('Task produced review evidence')).toBeVisible()
    expect(screen.getByText(/"result": "complete"/)).toBeVisible()
    expect(screen.getByText('openspec/changes/issue-454/design.md')).toBeVisible()
    expect(screen.getByText('canonical log line')).toBeVisible()

    fireEvent.click(disclosure)
    expect(screen.queryByTestId('workflow-task-details')).not.toBeInTheDocument()
  })

  it('shows mapped execution errors and delivery guidance', async () => {
    renderTask(
      makeTask({
        taskId: 'integrate:publish',
        title: 'Publish delivery',
        status: 'failed',
        origin: { source: 'runtime', uses: 'mohist/publish' },
        error: { code: 'workspace-setup', message: 'Workspace could not be prepared' },
      }),
    )

    fireEvent.click(screen.getByRole('button', { name: /Publish delivery/ }))
    await act(async () => {
      await Promise.resolve()
    })

    expect(screen.getByText('Workspace could not be prepared')).toBeVisible()
    expect(screen.getByText('Failure kind')).toBeVisible()
  })
})
