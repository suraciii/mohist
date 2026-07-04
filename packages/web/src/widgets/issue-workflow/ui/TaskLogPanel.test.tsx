// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { HubConnectionBuilder } from '@microsoft/signalr'
import { ProjectProvider } from '../../../entities/project'
import { getIssueWorkflowTaskLog } from '../../../entities/issue'
import { TaskLogPanel, mergeTaskLogDelta } from './TaskLogPanel'
import type { TaskLogLine, TaskLogPage } from '../../../entities/issue'
import type { TaskLogDeltaEnvelopeWire } from '../../../shared/api/events-hub'

vi.mock('@microsoft/signalr', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@microsoft/signalr')>()
  return {
    ...actual,
    HubConnectionBuilder: vi.fn(),
  }
})

vi.mock('../../../entities/issue/api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/issue/api/client')>()
  return {
    ...actual,
    getIssueWorkflowTaskLog: vi.fn(),
  }
})

const mockedGetIssueWorkflowTaskLog = vi.mocked(getIssueWorkflowTaskLog)

type Listener = (...args: unknown[]) => void

interface FakeConnection {
  state: number
  onreconnecting: (handler?: Listener) => Listener | null | void
  onreconnected: (handler?: Listener) => Listener | null | void
  onclose: (handler?: Listener) => Listener | null | void
  on: (event: string, handler: Listener) => void
  start: () => Promise<void>
  stop: () => Promise<void>
  invoke: (...args: unknown[]) => Promise<unknown>
  handlers: Map<string, Listener>
  invokes: Array<{ method: string; args: unknown[] }>
  reconnectHandler: Listener | null
}

const fakeConnections: FakeConnection[] = []
const recordedInvokes: Array<{ method: string; args: unknown[] }> = []

function makeFakeConnection(): FakeConnection {
  const handlers = new Map<string, Listener>()
  const invokes: Array<{ method: string; args: unknown[] }> = []
  const conn: FakeConnection = {
    state: 0,
    reconnectHandler: null,
    onreconnecting(handler) {
      if (handler === undefined) return undefined
    },
    onreconnected(handler) {
      conn.reconnectHandler = handler ?? null
      if (handler === undefined) return undefined
    },
    onclose(handler) {
      if (handler === undefined) return undefined
    },
    on: vi.fn((event: string, handler: Listener) => {
      handlers.set(event, handler)
    }),
    start: vi.fn(async () => {
      conn.state = 1
    }),
    stop: vi.fn(async () => {
      conn.state = 0
    }),
    invoke: vi.fn(async (...callArgs: unknown[]) => {
      const [method, ...args] = callArgs
      const m = String(method)
      invokes.push({ method: m, args })
      recordedInvokes.push({ method: m, args })
      return undefined
    }),
    handlers,
    invokes,
  }
  fakeConnections.push(conn)
  return conn
}

function mockConnectionBuilder() {
  function FakeBuilder(this: unknown) {
    return {
      withUrl: () => ({
        withAutomaticReconnect: () => ({
          configureLogging: () => ({
            build: () => makeFakeConnection(),
          }),
        }),
      }),
    }
  }
  vi.mocked(HubConnectionBuilder).mockImplementation(FakeBuilder as unknown as typeof HubConnectionBuilder)
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

function makeLine(overrides: Partial<TaskLogLine>): TaskLogLine {
  return {
    seq: 1,
    timestamp: '2026-07-03T08:00:00.000Z',
    source: 'action:rebase',
    text: 'default',
    ...overrides,
  }
}

function makePage(lines: TaskLogLine[], truncated = false): TaskLogPage {
  return { lines: lines.slice().sort((a, b) => a.seq - b.seq), nextCursor: null, truncated }
}

function makeEnvelope(entries: { seq: number; timestamp?: string; source?: string; text?: string }[], options: { ownerKind?: string; ownerId?: string; workId?: string; taskId?: string | null; truncated?: boolean } = {}): TaskLogDeltaEnvelopeWire {
  return {
    ownerKind: options.ownerKind ?? 'workflow',
    ownerId: options.ownerId ?? 'wr-1',
    workId: options.workId ?? 'work-1',
    taskId: options.taskId ?? 'build-task-1',
    entries: entries.map((e) => ({
      seq: e.seq,
      timestamp: e.timestamp ?? '2026-07-03T08:00:01.000Z',
      source: e.source ?? 'action:rebase',
      text: e.text ?? `line ${e.seq}`,
    })),
    truncated: options.truncated ?? false,
  }
}

interface TestHarness {
  queryClient: QueryClient
  page: { current: TaskLogPage | undefined }
  setPage: (next: TaskLogPage | undefined) => void
}

function buildHarness(initialPage: TaskLogPage | undefined): TestHarness {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const page = { current: initialPage }
  mockedGetIssueWorkflowTaskLog.mockImplementation(async () => {
    return page.current ?? { lines: [], nextCursor: null, truncated: false }
  })
  return {
    queryClient,
    page,
    setPage(next) {
      page.current = next
    },
  }
}

function renderWithHarness(ui: React.ReactNode, harness: TestHarness) {
  return render(
    <QueryClientProvider client={harness.queryClient}>
      <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
        {ui}
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

async function flushAndGetLastConnection(): Promise<FakeConnection> {
  await waitFor(() => {
    expect(fakeConnections.length).toBeGreaterThan(0)
  })
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 0))
  })
  return fakeConnections[fakeConnections.length - 1]
}

describe('TaskLogPanel — live append (Phase 2 T-004)', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    fakeConnections.length = 0
    recordedInvokes.length = 0
    mockConnectionBuilder()
  })

  afterEach(() => {
    cleanup()
  })

  it('renders Phase 1 line-by-line output (non-regression)', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, text: 'Cloning repo' }),
      makeLine({ seq: 2, text: 'CONFLICT' }),
    ]))

    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    await screen.findByTestId('task-log-panel')
    expect(screen.getByText('Cloning repo')).toBeInTheDocument()
    expect(screen.getByText('CONFLICT')).toBeInTheDocument()
  })

  it('calls SubscribeTaskLogAsync when the task is running, the panel is rendered, and the connection is ready', async () => {
    const harness = buildHarness(makePage([]))

    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="running" />,
      harness,
    )

    await flushAndGetLastConnection()

    await waitFor(() => {
      expect(recordedInvokes.some((inv) => inv.method === 'SubscribeTaskLogAsync')).toBe(true)
    })
    const subInvoke = recordedInvokes.find((inv) => inv.method === 'SubscribeTaskLogAsync')
    expect(subInvoke?.args).toEqual(['wr-1', 'build-task-1'])
  })

  it('does not subscribe when the task is in a terminal state', async () => {
    const harness = buildHarness(makePage([]))

    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    await flushAndGetLastConnection()

    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0))
    })

    expect(recordedInvokes.some((inv) => inv.method === 'SubscribeTaskLogAsync')).toBe(false)
  })

  it('does not subscribe when workflowRunId is missing', async () => {
    const harness = buildHarness(makePage([]))

    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId={null} taskStatus="running" />,
      harness,
    )

    await flushAndGetLastConnection()

    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0))
    })

    expect(recordedInvokes.some((inv) => inv.method === 'SubscribeTaskLogAsync')).toBe(false)
  })

  it('live-appends incoming OnTaskLogDelta lines for this task during execution', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, timestamp: '2026-07-03T08:00:00.000Z', text: 'before' }),
    ]))

    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="running" />,
      harness,
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
    const harness = buildHarness(makePage([
      makeLine({ seq: 5, text: 'cached-5' }),
      makeLine({ seq: 6, text: 'cached-6' }),
    ]))

    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="running" />,
      harness,
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
    const harness = buildHarness(makePage([]))

    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-2" taskStatus="running" />,
      harness,
    )

    const conn = await flushAndGetLastConnection()
    const handler = conn.handlers.get('OnTaskLogDelta')
    expect(handler).toBeDefined()

    await act(async () => {
      handler!(makeEnvelope([{ seq: 1, text: 'wrong-run' }], { ownerId: 'wr-1' }))
    })

    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0))
    })

    expect(screen.queryByText('wrong-run')).not.toBeInTheDocument()
  })

  it('ignores deltas scoped to a different taskId', async () => {
    const harness = buildHarness(makePage([]))

    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="running" />,
      harness,
    )

    const conn = await flushAndGetLastConnection()
    const handler = conn.handlers.get('OnTaskLogDelta')
    expect(handler).toBeDefined()

    await act(async () => {
      handler!(makeEnvelope([{ seq: 1, text: 'for-other-task' }], { taskId: 'other-task' }))
    })

    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0))
    })

    expect(screen.queryByText('for-other-task')).not.toBeInTheDocument()
  })

  it('triggers queryClient.invalidateQueries for the task-log key when the task reaches a terminal state', async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')
    mockedGetIssueWorkflowTaskLog.mockResolvedValue({ lines: [], nextCursor: null, truncated: false })

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
    const harness = buildHarness(makePage([]))

    const { unmount } = renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="running" />,
      harness,
    )

    await flushAndGetLastConnection()
    await waitFor(() => {
      expect(recordedInvokes.some((inv) => inv.method === 'SubscribeTaskLogAsync')).toBe(true)
    })

    recordedInvokes.length = 0
    unmount()

    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0))
    })

    expect(recordedInvokes.some((inv) => inv.method === 'UnsubscribeTaskLogAsync')).toBe(true)
  })

  it('re-subscribes the active task-log scope after SignalR reconnect', async () => {
    const harness = buildHarness(makePage([]))

    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="running" />,
      harness,
    )

    const conn = await flushAndGetLastConnection()
    await waitFor(() => {
      expect(recordedInvokes.filter((inv) => inv.method === 'SubscribeTaskLogAsync')).toHaveLength(1)
    })

    await act(async () => {
      conn.reconnectHandler?.()
    })

    await waitFor(() => {
      expect(recordedInvokes.filter((inv) => inv.method === 'SubscribeTaskLogAsync')).toHaveLength(2)
    })
  })

  it('does not subscribe for pending or missing task status', async () => {
    const pendingHarness = buildHarness(makePage([]))
    const { unmount } = renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="pending" />,
      pendingHarness,
    )

    await flushAndGetLastConnection()
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0))
    })
    expect(recordedInvokes.some((inv) => inv.method === 'SubscribeTaskLogAsync')).toBe(false)

    unmount()
    fakeConnections.length = 0
    recordedInvokes.length = 0

    const missingHarness = buildHarness(makePage([]))
    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" />,
      missingHarness,
    )

    await flushAndGetLastConnection()
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0))
    })
    expect(recordedInvokes.some((inv) => inv.method === 'SubscribeTaskLogAsync')).toBe(false)
  })

  it('preserves the truncation indicator from cached data (Phase 1 rendering non-regression)', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 4999, text: 'CONFLICT' }),
      makeLine({ seq: 5000, text: 'Patch failed' }),
    ], true))

    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    expect(await screen.findByTestId('task-log-truncation-indicator')).toBeInTheDocument()
  })

  it('preserves the empty-state message when no lines are cached (Phase 1 rendering non-regression)', async () => {
    const harness = buildHarness(makePage([]))

    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    expect(await screen.findByTestId('task-log-empty')).toBeInTheDocument()
  })

  it('shows the full authoritative log when there were no live subscribers during execution (Phase 1 non-regression)', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, timestamp: '2026-07-03T08:00:00.000Z', text: 'authoritative-line-1' }),
      makeLine({ seq: 2, timestamp: '2026-07-03T08:00:00.050Z', text: 'authoritative-line-2' }),
    ]))

    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    expect(await screen.findByText('authoritative-line-1')).toBeInTheDocument()
    expect(screen.getByText('authoritative-line-2')).toBeInTheDocument()

    // No OnTaskLogDelta was emitted — the panel still renders the authoritative log from the issue-path query.
    expect(recordedInvokes.some((inv) => inv.method === 'SubscribeTaskLogAsync')).toBe(false)
  })
})

describe('mergeTaskLogDelta — pure merge', () => {
  it('appends unseen entries and sorts by seq while deduping existing seqs', () => {
    const page = makePage([
      makeLine({ seq: 5, text: 'a' }),
      makeLine({ seq: 6, text: 'b' }),
    ])
    const delta = makeEnvelope([
      { seq: 6, text: 'dup' },
      { seq: 3, text: 'out-of-order' },
      { seq: 7, text: 'c' },
    ])
    const merged = mergeTaskLogDelta(page, delta)
    expect(merged.lines.map((l) => l.seq)).toEqual([3, 5, 6, 7])
    expect(merged.lines.map((l) => l.text)).toEqual(['out-of-order', 'a', 'b', 'c'])
  })

  it('keeps the truncated flag if either side is truncated', () => {
    const page = makePage([], true)
    const delta = makeEnvelope([{ seq: 1, text: 'a' }], { truncated: true })
    const merged = mergeTaskLogDelta(page, delta)
    expect(merged.truncated).toBe(true)
  })

  it('keeps equivalent page contents if nothing changes (no incoming entries, no truncate change)', () => {
    const page = makePage([
      makeLine({ seq: 1, text: 'a' }),
    ])
    const delta = makeEnvelope([{ seq: 1, text: 'dup' }], { truncated: false })
    const merged = mergeTaskLogDelta(page, delta)
    expect(merged.lines).toEqual(page.lines)
    expect(merged.truncated).toBe(page.truncated)
  })
})
