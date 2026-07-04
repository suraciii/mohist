// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
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

  it('does not subscribe the task-log panel connection to domain or transcript event types', async () => {
    const harness = buildHarness(makePage([]))

    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="running" />,
      harness,
    )

    await flushAndGetLastConnection()

    await waitFor(() => {
      expect(recordedInvokes.some((inv) => inv.method === 'SubscribeTaskLogAsync')).toBe(true)
    })
    expect(recordedInvokes.some((inv) => inv.method === 'SetSubscriptionsAsync')).toBe(false)
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

  it('keeps only the retained tail when live deltas grow beyond the panel limit', () => {
    const page = makePage(
      Array.from({ length: 5000 }, (_, index) => makeLine({ seq: index + 1, text: `cached-${index + 1}` })),
    )
    const delta = makeEnvelope(
      Array.from({ length: 5 }, (_, index) => ({ seq: 5001 + index, text: `live-${5001 + index}` })),
    )

    const merged = mergeTaskLogDelta(page, delta)

    expect(merged.lines).toHaveLength(5000)
    expect(merged.lines[0].seq).toBe(6)
    expect(merged.lines[merged.lines.length - 1].seq).toBe(5005)
    expect(merged.truncated).toBe(true)
    expect(merged.nextCursor).toBeNull()
  })

  it('drops late low-seq deltas once the cache already contains a retained tail', () => {
    const page = makePage(
      Array.from({ length: 5000 }, (_, index) => makeLine({ seq: 1001 + index, text: `tail-${1001 + index}` })),
      true,
    )
    const delta = makeEnvelope([{ seq: 999, text: 'old-head' }])

    const merged = mergeTaskLogDelta(page, delta)

    expect(merged.lines).toHaveLength(5000)
    expect(merged.lines[0].seq).toBe(1001)
    expect(merged.lines[merged.lines.length - 1].seq).toBe(6000)
    expect(merged.lines.some((line) => line.seq === 999)).toBe(false)
    expect(merged.truncated).toBe(true)
  })
})

interface DownloadCapture {
  blob: Blob | null
  filename: string | null
  clicks: Array<{ download: string; href: string }>
}

function installDownloadSpy(): DownloadCapture {
  const capture: DownloadCapture = { blob: null, filename: null, clicks: [] }
  vi.spyOn(URL, 'createObjectURL').mockImplementation((obj: Blob | MediaSource) => {
    capture.blob = obj as Blob
    return 'blob:mock-task-log-url'
  })
  vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined)

  const realCreate = document.createElement.bind(document)
  vi.spyOn(document, 'createElement').mockImplementation(((tag: string, options?: ElementCreationOptions) => {
    const element = realCreate(tag, options)
    if (tag === 'a') {
      Object.defineProperty(element, 'click', {
        value: () => {
          const anchor = element as HTMLAnchorElement
          capture.clicks.push({ download: anchor.download, href: anchor.href })
        },
        configurable: true,
      })
    }
    return element
  }) as typeof document.createElement)

  return capture
}

async function readBlobText(blob: Blob | null): Promise<string> {
  if (!blob) return ''
  return await blob.text()
}

describe('TaskLogPanel — viewing enhancement (Phase 3a T-001)', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    fakeConnections.length = 0
    recordedInvokes.length = 0
    mockConnectionBuilder()
  })

  afterEach(() => {
    cleanup()
    vi.restoreAllMocks()
  })

  it('renders one chip per distinct source in lexicographic order with no absent-source chips', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'cleanup', text: 'rm -rf tmp' }),
      makeLine({ seq: 2, source: 'workspace-prep', text: 'cloning' }),
      makeLine({ seq: 3, source: 'action:rebase', text: 'rebasing' }),
      makeLine({ seq: 4, source: 'branch-check', text: 'on master' }),
    ]))

    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    await screen.findByTestId('task-log-panel')
    const chipsContainer = await screen.findByTestId('task-log-source-chips')
    const chipLabels = Array.from(chipsContainer.querySelectorAll('button')).map((b) => b.textContent?.trim())
    expect(chipLabels).toEqual([
      'action:rebase',
      'branch-check',
      'cleanup',
      'workspace-prep',
    ])
  })

  it('narrows visible lines in real time as the user types a keyword (case-insensitive)', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'Cloning repo' }),
      makeLine({ seq: 2, source: 'action:rebase', text: 'CONFLICT (content)' }),
      makeLine({ seq: 3, source: 'action:rebase', text: 'Patch failed' }),
    ]))

    const user = userEvent.setup()
    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    await screen.findByTestId('task-log-panel')
    await screen.findByText('Cloning repo')

    const fetchSpy = vi.spyOn(harness.queryClient, 'fetchQuery')
    const input = await screen.findByTestId('task-log-search-input')
    await user.type(input, 'CONFLICT')

    await waitFor(() => {
      expect(screen.queryByText('Cloning repo')).not.toBeInTheDocument()
    })
    expect(screen.getByText('CONFLICT (content)')).toBeInTheDocument()
    expect(screen.queryByText('Patch failed')).not.toBeInTheDocument()
    expect(fetchSpy).not.toHaveBeenCalled()
  })

  it('searches source as well as text (case-insensitive)', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'action:rebase', text: 'starting' }),
      makeLine({ seq: 2, source: 'branch-check', text: 'on master' }),
    ]))

    const user = userEvent.setup()
    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    await screen.findByTestId('task-log-panel')
    await screen.findByText('starting')

    await user.type(await screen.findByTestId('task-log-search-input'), 'REBASE')

    await waitFor(() => {
      expect(screen.queryByText('on master')).not.toBeInTheDocument()
    })
    expect(screen.getByText('starting')).toBeInTheDocument()
  })

  it('toggling a source chip hides only its lines while keeping other sources visible (opt-out semantics)', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'action:rebase', text: 'rebasing' }),
      makeLine({ seq: 2, source: 'branch-check', text: 'on master' }),
      makeLine({ seq: 3, source: 'cleanup', text: 'rm tmp' }),
    ]))

    const user = userEvent.setup()
    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    await screen.findByTestId('task-log-panel')

    const chip = await screen.findByTestId('task-log-source-chip-action:rebase')
    await user.click(chip)

    await waitFor(() => {
      expect(screen.queryByText('rebasing')).not.toBeInTheDocument()
    })
    expect(screen.getByText('on master')).toBeInTheDocument()
    expect(screen.getByText('rm tmp')).toBeInTheDocument()
  })

  it('newly-arrived source from a live delta remains visible by default (opt-out set)', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'cloning' }),
    ]))

    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="running" />,
      harness,
    )

    await screen.findByTestId('task-log-panel')
    const chipBar = await screen.findByTestId('task-log-source-chips')
    expect(chipBar.querySelectorAll('button')).toHaveLength(1)

    const conn = await flushAndGetLastConnection()
    const handler = conn.handlers.get('OnTaskLogDelta')
    expect(handler).toBeDefined()

    await act(async () => {
      handler!(makeEnvelope([
        { seq: 2, source: 'cleanup', text: 'rm tmp' },
        { seq: 3, source: 'branch-check', text: 'on master' },
      ]))
    })

    await waitFor(() => {
      expect(screen.getByText('rm tmp')).toBeInTheDocument()
    })
    expect(screen.getByText('on master')).toBeInTheDocument()
    expect(chipBar.querySelectorAll('button')).toHaveLength(3)
  })

  it('composes search AND source filter so a line must pass both', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'action:rebase', text: 'CONFLICT (content)' }),
      makeLine({ seq: 2, source: 'action:rebase', text: 'rebasing' }),
      makeLine({ seq: 3, source: 'cleanup', text: 'CONFLICT during cleanup' }),
    ]))

    const user = userEvent.setup()
    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    await screen.findByTestId('task-log-panel')

    await user.click(await screen.findByTestId('task-log-source-chip-cleanup'))
    await user.type(await screen.findByTestId('task-log-search-input'), 'CONFLICT')

    await waitFor(() => {
      expect(screen.queryByText('CONFLICT during cleanup')).not.toBeInTheDocument()
    })
    expect(screen.queryByText('rebasing')).not.toBeInTheDocument()
    expect(screen.getByText('CONFLICT (content)')).toBeInTheDocument()
  })

  it('exports the currently filtered view as a .txt Blob with the convention filename (filter applied)', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'cloning' }),
      makeLine({ seq: 2, source: 'action:rebase', text: 'rebasing-1' }),
      makeLine({ seq: 3, source: 'action:rebase', text: 'rebasing-2' }),
    ]))

    const user = userEvent.setup()
    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    await screen.findByTestId('task-log-panel')

    await user.click(await screen.findByTestId('task-log-source-chip-workspace-prep'))

    await waitFor(() => {
      expect(screen.queryByText('cloning')).not.toBeInTheDocument()
    })

    const capture = installDownloadSpy()
    const fetchSpy = vi.spyOn(window, 'fetch')

    const download = await screen.findByTestId('task-log-download-button')
    await user.click(download)

    await waitFor(() => {
      expect(capture.clicks.length).toBeGreaterThan(0)
    })

    expect(capture.clicks[0].download).toMatch(/^task-logs-build-task-1-\d{4}-\d{2}-\d{2}\.txt$/)
    expect(fetchSpy).not.toHaveBeenCalled()

    const blobText = await readBlobText(capture.blob)
    const lines = blobText.split('\n')
    expect(lines).toEqual(['rebasing-1', 'rebasing-2'])
  })

  it('preserves colon-containing task ids in the download filename', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'line-1' }),
    ]))

    const user = userEvent.setup()
    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="integrate:prepare" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    await screen.findByTestId('task-log-panel')

    const capture = installDownloadSpy()
    await user.click(await screen.findByTestId('task-log-download-button'))

    await waitFor(() => {
      expect(capture.clicks.length).toBeGreaterThan(0)
    })

    expect(capture.clicks[0].download).toMatch(/^task-logs-integrate:prepare-\d{4}-\d{2}-\d{2}\.txt$/)
  })

  it('exports the full loaded log when no filter is active', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'line-1' }),
      makeLine({ seq: 2, source: 'action:rebase', text: 'line-2' }),
      makeLine({ seq: 3, source: 'cleanup', text: 'line-3' }),
    ]))

    const user = userEvent.setup()
    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    await screen.findByTestId('task-log-panel')

    const capture = installDownloadSpy()
    const download = await screen.findByTestId('task-log-download-button')
    await user.click(download)

    await waitFor(() => {
      expect(capture.clicks.length).toBeGreaterThan(0)
    })

    expect(capture.clicks[0].download).toMatch(/^task-logs-build-task-1-\d{4}-\d{2}-\d{2}\.txt$/)
    const blobText = await readBlobText(capture.blob)
    expect(blobText.split('\n')).toEqual(['line-1', 'line-2', 'line-3'])
  })

  it('disables the download button when the filtered set is empty', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'cloning' }),
    ]))

    const user = userEvent.setup()
    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    await screen.findByTestId('task-log-panel')

    await user.click(await screen.findByTestId('task-log-source-chip-workspace-prep'))

    const download = await screen.findByTestId('task-log-download-button')
    expect(download).toBeDisabled()
  })

  it('opens with the default state: empty search input, every source chip enabled, every loaded line visible', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'line-1' }),
      makeLine({ seq: 2, source: 'action:rebase', text: 'line-2' }),
    ]))

    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    await screen.findByTestId('task-log-panel')

    const input = (await screen.findByTestId('task-log-search-input')) as HTMLInputElement
    expect(input.value).toBe('')

    const chipBar = await screen.findByTestId('task-log-source-chips')
    const chips = Array.from(chipBar.querySelectorAll('button'))
    expect(chips).toHaveLength(2)
    for (const chip of chips) {
      expect(chip.getAttribute('aria-pressed')).toBe('true')
    }

    expect(screen.getByText('line-1')).toBeInTheDocument()
    expect(screen.getByText('line-2')).toBeInTheDocument()
  })

  it('renders the no-search-match boundary when search yields zero results', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'cloning' }),
    ]))

    const user = userEvent.setup()
    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    await screen.findByTestId('task-log-panel')

    await user.type(await screen.findByTestId('task-log-search-input'), 'zzz-no-match')

    expect(await screen.findByTestId('task-log-no-search-match')).toBeInTheDocument()
    expect(screen.queryByText('cloning')).not.toBeInTheDocument()
  })

  it('renders the no-source-filter boundary when all sources are disabled and search is empty', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'cloning' }),
      makeLine({ seq: 2, source: 'cleanup', text: 'rm tmp' }),
    ]))

    const user = userEvent.setup()
    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    await screen.findByTestId('task-log-panel')

    await user.click(await screen.findByTestId('task-log-source-chip-workspace-prep'))
    await user.click(await screen.findByTestId('task-log-source-chip-cleanup'))

    expect(await screen.findByTestId('task-log-no-source-match')).toBeInTheDocument()
    expect(screen.queryByTestId('task-log-no-search-match')).not.toBeInTheDocument()
  })

  it('prefers the no-search-match boundary when search and source filters both yield zero rows', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'cloning' }),
    ]))

    const user = userEvent.setup()
    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    await screen.findByTestId('task-log-panel')

    await user.click(await screen.findByTestId('task-log-source-chip-workspace-prep'))
    await user.type(await screen.findByTestId('task-log-search-input'), 'never-matches')

    expect(await screen.findByTestId('task-log-no-search-match')).toBeInTheDocument()
    expect(screen.queryByTestId('task-log-no-source-match')).not.toBeInTheDocument()
  })

  it('preserves the loading and error boundary messages', async () => {
    mockedGetIssueWorkflowTaskLog.mockReturnValueOnce(new Promise(() => {}))
    const queryClientLoading = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const { unmount } = render(
      <QueryClientProvider client={queryClientLoading}>
        <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
          <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />
        </ProjectProvider>
      </QueryClientProvider>,
    )
    await screen.findByTestId('task-log-panel')
    expect(screen.getByText('Loading execution log…')).toBeInTheDocument()
    unmount()

    mockedGetIssueWorkflowTaskLog.mockRejectedValue(new Error('boom'))
    const queryClientError = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(
      <QueryClientProvider client={queryClientError}>
        <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
          <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />
        </ProjectProvider>
      </QueryClientProvider>,
    )
    await screen.findByTestId('task-log-panel')
    expect(await screen.findByText('Execution log unavailable')).toBeInTheDocument()
  })

  it('does not force-scroll on a new line while the user is paused away from the bottom', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'line-1' }),
    ]))

    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="running" />,
      harness,
    )

    await screen.findByTestId('task-log-panel')

    const scrollNode = (await screen.findByTestId('task-log-scroll')) as HTMLDivElement
    Object.defineProperty(scrollNode, 'scrollHeight', { configurable: true, value: 2000 })
    Object.defineProperty(scrollNode, 'clientHeight', { configurable: true, value: 200 })
    scrollNode.scrollTop = 0

    await act(async () => {
      fireEvent.scroll(scrollNode)
    })

    const conn = await flushAndGetLastConnection()
    const handler = conn.handlers.get('OnTaskLogDelta')
    expect(handler).toBeDefined()

    await act(async () => {
      handler!(makeEnvelope([{ seq: 2, source: 'workspace-prep', text: 'line-2' }]))
    })

    await waitFor(() => {
      expect(screen.getByText('line-2')).toBeInTheDocument()
    })

    expect(scrollNode.scrollTop).toBe(0)
  })

  it('does not force-scroll when a filter change hides lines while the user is paused away from the bottom', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'needle line' }),
      makeLine({ seq: 2, source: 'cleanup', text: 'other line' }),
    ]))

    const user = userEvent.setup()
    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    await screen.findByTestId('task-log-panel')

    const scrollNode = (await screen.findByTestId('task-log-scroll')) as HTMLDivElement
    Object.defineProperty(scrollNode, 'scrollHeight', { configurable: true, value: 2000 })
    Object.defineProperty(scrollNode, 'clientHeight', { configurable: true, value: 200 })
    scrollNode.scrollTop = 0

    await act(async () => {
      fireEvent.scroll(scrollNode)
    })

    await user.type(await screen.findByTestId('task-log-search-input'), 'needle')

    await waitFor(() => {
      expect(screen.queryByText('other line')).not.toBeInTheDocument()
    })
    expect(screen.getByText('needle line')).toBeInTheDocument()
    expect(scrollNode.scrollTop).toBe(0)
  })

  it('resumes auto-follow near the bottom and follows the next visible-line change', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'alpha line' }),
      makeLine({ seq: 2, source: 'cleanup', text: 'beta line' }),
    ]))

    const user = userEvent.setup()
    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    await screen.findByTestId('task-log-panel')

    const scrollNode = (await screen.findByTestId('task-log-scroll')) as HTMLDivElement
    Object.defineProperty(scrollNode, 'scrollHeight', { configurable: true, value: 2000 })
    Object.defineProperty(scrollNode, 'clientHeight', { configurable: true, value: 200 })
    scrollNode.scrollTop = 0

    await act(async () => {
      fireEvent.scroll(scrollNode)
    })

    scrollNode.scrollTop = 1795
    await act(async () => {
      fireEvent.scroll(scrollNode)
    })

    Object.defineProperty(scrollNode, 'scrollHeight', { configurable: true, value: 2200 })
    await user.type(await screen.findByTestId('task-log-search-input'), 'beta')

    await waitFor(() => {
      expect(screen.queryByText('alpha line')).not.toBeInTheDocument()
    })
    expect(screen.getByText('beta line')).toBeInTheDocument()
    await waitFor(() => {
      expect(scrollNode.scrollTop).toBe(2200)
    })
  })

  it('live-append during a running task still appends in seq order when no filter is active (non-regression)', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'line-1' }),
    ]))

    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="running" />,
      harness,
    )

    await screen.findByTestId('task-log-panel')
    await screen.findByText('line-1')

    const conn = await flushAndGetLastConnection()
    const handler = conn.handlers.get('OnTaskLogDelta')
    expect(handler).toBeDefined()

    await act(async () => {
      handler!(makeEnvelope([
        { seq: 2, source: 'workspace-prep', text: 'line-2' },
        { seq: 3, source: 'workspace-prep', text: 'line-3' },
      ]))
    })

    await waitFor(() => {
      expect(screen.getByText('line-3')).toBeInTheDocument()
    })

    const lines = Array.from(document.querySelectorAll('[data-testid="task-log-lines"] li')).map((li) => li.textContent)
    expect(lines[0]).toMatch(/line-1/)
    expect(lines[1]).toMatch(/line-2/)
    expect(lines[2]).toMatch(/line-3/)
  })
})
