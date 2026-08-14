import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, render, screen, waitFor } from '@testing-library/react'
import { QueryClientProvider, useQuery } from '@tanstack/react-query'
import { ProjectProvider } from '../../../entities/project'
import { issueWorkflowTaskLogQueryOptions } from '../../../entities/issue'
import type { TaskLogPage } from '../../../entities/issue/model/task-log'
import {
  TaskLogPanel as DefaultTaskLogPanel,
  type TaskLogDataHook,
  type TaskLogPanelProps,
} from './TaskLogPanel'
import {
  flushAndGetLastConnection,
  fakeConnections,
  makeEnvelope,
  makeLine,
  makePage,
  mockConnectionBuilder,
  projects,
  recordedInvokes,
  renderWithTaskLogProviders,
  newQueryClient,
  type TaskLogTestState,
} from './_taskLogPanelTestUtils'
import { deferNextFakeConnectionStart } from '../../../../tests/support/signalr-fake'

const _taskLogPageRef: { current: TaskLogPage | undefined } = { current: undefined }
const queryClients = new Set<TaskLogTestState['queryClient']>()

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
    queryFn: async () => _taskLogPageRef.current ?? makePage([]),
  })

function TaskLogPanel(props: Omit<TaskLogPanelProps, 'taskLogHook'>) {
  return <DefaultTaskLogPanel {...props} taskLogHook={taskLogHook} />
}

function createTaskLogTestState(initialPage: TaskLogPage | undefined): TaskLogTestState {
  const queryClient = newQueryClient()
  queryClients.add(queryClient)
  _taskLogPageRef.current = initialPage
  return {
    queryClient,
    page: _taskLogPageRef,
    setPage(next) {
      _taskLogPageRef.current = next
    },
  }
}

describe('TaskLogPanel live append', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    fakeConnections.length = 0
    recordedInvokes.length = 0
    _taskLogPageRef.current = undefined
    mockConnectionBuilder()
  })

  afterEach(() => {
    cleanup()
    for (const queryClient of queryClients) queryClient.clear()
    queryClients.clear()
  })

  it('renders cached log lines in sequence', async () => {
    const testState = createTaskLogTestState(makePage([
      makeLine({ seq: 1, text: 'Cloning repo' }),
      makeLine({ seq: 2, text: 'CONFLICT' }),
    ]))

    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      testState,
    )

    expect(await screen.findByText('Cloning repo')).toBeInTheDocument()
    expect(await screen.findByText('CONFLICT')).toBeInTheDocument()
  })

  it('calls SubscribeTaskLogAsync when the task is running, the panel is rendered, and the connection is ready', async () => {
    const testState = createTaskLogTestState(makePage([]))
    deferNextFakeConnectionStart()

    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="running" />,
      testState,
    )

    const conn = await flushAndGetLastConnection()
    await conn.waitForInvoke('SubscribeTaskLogAsync')
    const subInvoke = recordedInvokes.find((inv) => inv.method === 'SubscribeTaskLogAsync')
    expect(subInvoke?.args).toEqual(['wr-1', 'build-task-1'])
  })

  it('keeps a blocked task log subscribed for a late authoritative result', async () => {
    const testState = createTaskLogTestState(makePage([]))
    deferNextFakeConnectionStart()

    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="blocked" />,
      testState,
    )

    const conn = await flushAndGetLastConnection()
    await conn.waitForInvoke('SubscribeTaskLogAsync')
    expect(recordedInvokes.some((inv) => inv.method === 'SubscribeTaskLogAsync')).toBe(true)
  })

  it('does not subscribe the task-log panel connection to domain or transcript event types', async () => {
    const testState = createTaskLogTestState(makePage([]))
    deferNextFakeConnectionStart()

    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="running" />,
      testState,
    )

    const conn = await flushAndGetLastConnection()
    await conn.waitForInvoke('SubscribeTaskLogAsync')
    expect(recordedInvokes.some((inv) => inv.method === 'SetSubscriptionsAsync')).toBe(false)
  })

  it('does not subscribe when the task is in a terminal state', async () => {
    const testState = createTaskLogTestState(makePage([]))
    deferNextFakeConnectionStart()

    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      testState,
    )

    await flushAndGetLastConnection()

    expect(recordedInvokes.some((inv) => inv.method === 'SubscribeTaskLogAsync')).toBe(false)
  })

  it('does not subscribe when workflowRunId is missing', async () => {
    const testState = createTaskLogTestState(makePage([]))
    deferNextFakeConnectionStart()

    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId={null} taskStatus="running" />,
      testState,
    )

    await flushAndGetLastConnection()

    expect(recordedInvokes.some((inv) => inv.method === 'SubscribeTaskLogAsync')).toBe(false)
  })

  it('live-appends incoming OnTaskLogDelta lines for this task during execution', async () => {
    const testState = createTaskLogTestState(makePage([
      makeLine({ seq: 1, timestamp: '2026-07-03T08:00:00.000Z', text: 'before' }),
    ]))
    deferNextFakeConnectionStart()

    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="running" />,
      testState,
    )

    await screen.findByText('before')

    const conn = await flushAndGetLastConnection()
    const handler = conn.handlers.get('OnTaskLogDelta')
    expect(handler).toBeDefined()

    await act(async () => {
      handler!(makeEnvelope([
        { seq: 2, text: 'incremental-1' },
        { seq: 3, text: 'incremental-2' },
      ]))
    })

    await waitFor(() => {
      expect(screen.getByText('incremental-1')).toBeInTheDocument()
    })
    expect(screen.getByText('incremental-2')).toBeInTheDocument()
    expect(screen.getByText('before')).toBeInTheDocument()
  })

  it('deduplicates incoming deltas by seq — already cached and out-of-order low seqs are dropped', async () => {
    const testState = createTaskLogTestState(makePage([
      makeLine({ seq: 5, text: 'cached-5' }),
      makeLine({ seq: 6, text: 'cached-6' }),
    ]))
    deferNextFakeConnectionStart()

    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="running" />,
      testState,
    )

    await screen.findByText('cached-5')

    const conn = await flushAndGetLastConnection()
    const handler = conn.handlers.get('OnTaskLogDelta')
    expect(handler).toBeDefined()

    await act(async () => {
      handler!(makeEnvelope([
        { seq: 6, text: 'should-be-dropped-already-cached' },
        { seq: 3, text: 'should-append-out-of-order' },
        { seq: 7, text: 'should-append' },
      ]))
    })

    await waitFor(() => {
      expect(screen.getByText('should-append')).toBeInTheDocument()
    })
    expect(screen.queryByText('should-be-dropped-already-cached')).not.toBeInTheDocument()
    expect(screen.getByText('should-append-out-of-order')).toBeInTheDocument()
  })

  it('ignores deltas from a different workflowRunId even when taskId matches', async () => {
    const testState = createTaskLogTestState(makePage([]))
    deferNextFakeConnectionStart()

    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-2" taskStatus="running" />,
      testState,
    )

    const conn = await flushAndGetLastConnection()
    const handler = conn.handlers.get('OnTaskLogDelta')
    expect(handler).toBeDefined()

    await act(async () => {
      handler!(makeEnvelope([{ seq: 1, text: 'wrong-run' }], { ownerId: 'wr-1' }))
    })

    expect(screen.queryByText('wrong-run')).not.toBeInTheDocument()
  })

  it('ignores deltas scoped to a different taskId', async () => {
    const testState = createTaskLogTestState(makePage([]))
    deferNextFakeConnectionStart()

    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="running" />,
      testState,
    )

    const conn = await flushAndGetLastConnection()
    const handler = conn.handlers.get('OnTaskLogDelta')
    expect(handler).toBeDefined()

    await act(async () => {
      handler!(makeEnvelope([{ seq: 1, text: 'for-other-task' }], { taskId: 'other-task' }))
    })

    expect(screen.queryByText('for-other-task')).not.toBeInTheDocument()
  })

  it('triggers queryClient.invalidateQueries for the task-log key when the task reaches a terminal state', async () => {
    const queryClient = newQueryClient()
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')
    deferNextFakeConnectionStart()

    const { rerender } = render(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
          <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="running" />
        </ProjectProvider>
      </QueryClientProvider>,
    )

    await flushAndGetLastConnection()

    invalidateSpy.mockClear()

    rerender(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
          <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />
        </ProjectProvider>
      </QueryClientProvider>,
    )

    await waitFor(() => {
      const calls = invalidateSpy.mock.calls
      const matchKey = calls.some(([arg]) => {
        const key = (arg as { queryKey?: unknown[] })?.queryKey
        return Array.isArray(key) && key.includes('workflow-task-log')
      })
      expect(matchKey).toBe(true)
    })
  })

  it('calls UnsubscribeTaskLogAsync when the panel unmounts while subscribed', async () => {
    const testState = createTaskLogTestState(makePage([]))
    deferNextFakeConnectionStart()

    const { unmount } = renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="running" />,
      testState,
    )

    const conn = await flushAndGetLastConnection()
    await conn.waitForInvoke('SubscribeTaskLogAsync')

    recordedInvokes.length = 0
    unmount()

    await conn.waitForInvoke('UnsubscribeTaskLogAsync')

    expect(recordedInvokes.some((inv) => inv.method === 'UnsubscribeTaskLogAsync')).toBe(true)
  })

  it('re-subscribes the active task-log scope after SignalR reconnect', async () => {
    const testState = createTaskLogTestState(makePage([]))
    deferNextFakeConnectionStart()

    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="running" />,
      testState,
    )

    const conn = await flushAndGetLastConnection()
    await conn.waitForInvoke('SubscribeTaskLogAsync')

    await act(async () => {
      conn.emit('reconnected')
    })

    await conn.waitForInvoke('SubscribeTaskLogAsync', 2)
  })

  it('does not subscribe for pending or missing task status', async () => {
    const pendingState = createTaskLogTestState(makePage([]))
    deferNextFakeConnectionStart()
    const { unmount } = renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="pending" />,
      pendingState,
    )

    await flushAndGetLastConnection()
    expect(recordedInvokes.some((inv) => inv.method === 'SubscribeTaskLogAsync')).toBe(false)

    unmount()
    fakeConnections.length = 0
    recordedInvokes.length = 0

    const missingState = createTaskLogTestState(makePage([]))
    mockConnectionBuilder()
    deferNextFakeConnectionStart()
    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" />,
      missingState,
    )

    await flushAndGetLastConnection()
    expect(recordedInvokes.some((inv) => inv.method === 'SubscribeTaskLogAsync')).toBe(false)
  })

  it('preserves the truncation indicator from cached data', async () => {
    const testState = createTaskLogTestState(makePage([
      makeLine({ seq: 4999, text: 'CONFLICT' }),
      makeLine({ seq: 5000, text: 'Patch failed' }),
    ], true))

    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      testState,
    )

    expect(await screen.findByTestId('task-log-truncation-indicator')).toBeInTheDocument()
  })

  it('preserves the empty-state message when no lines are cached', async () => {
    const testState = createTaskLogTestState(makePage([]))

    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      testState,
    )

    expect(await screen.findByTestId('task-log-empty')).toBeInTheDocument()
  })

  it('shows the full authoritative log when no live subscriber ran', async () => {
    const testState = createTaskLogTestState(makePage([
      makeLine({ seq: 1, timestamp: '2026-07-03T08:00:00.000Z', text: 'authoritative-line-1' }),
      makeLine({ seq: 2, timestamp: '2026-07-03T08:00:00.050Z', text: 'authoritative-line-2' }),
    ]))

    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      testState,
    )

    expect(await screen.findByText('authoritative-line-1')).toBeInTheDocument()
    expect(screen.getByText('authoritative-line-2')).toBeInTheDocument()

    expect(recordedInvokes.some((inv) => inv.method === 'SubscribeTaskLogAsync')).toBe(false)
  })
})
