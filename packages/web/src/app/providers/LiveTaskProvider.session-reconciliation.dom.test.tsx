import { useMemo } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, cleanup, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { http, HttpResponse } from 'msw'
import { useMswServer } from '../../../tests/support/msw'
import { ProjectProvider } from '../../entities/project'
import {
  unifiedSessionSummaryQueryOptions,
  unifiedSessionTranscriptQueryOptions,
  useUnifiedSessionSummary,
  useUnifiedSessionTranscript,
  type SessionTurn,
  type UnifiedSessionSummaryDto,
} from '../../entities/coder-session'
import { useSessionTimeline, useSessionTranscript } from '../../widgets/session-transcript'
import { LiveTaskProvider } from './LiveTaskProvider'
import { TEST_PROJECT } from './_liveTaskProviderTestUtils'

const at = '2026-09-08T00:00:00.000Z'
const rawText = '<openviking-context>internal context</openviking-context>Readable answer'
function turn(text: string): SessionTurn {
  return {
    id: 'turn-1',
    startedAt: at,
    completedAt: null,
    user: { role: 'mohist', text: '', kind: 'task', sentAt: at },
    assistant: [{ id: 'part-1', type: 'text', text, startedAt: at, completedAt: null }],
  }
}

class FakeWebSocket {
  readyState = 0
  onopen: ((event: Event) => void) | null = null
  onmessage: ((event: MessageEvent) => void) | null = null
  onclose: ((event: CloseEvent) => void) | null = null
  onerror: ((event: Event) => void) | null = null
  sent: string[] = []
  constructor(_url: string) {
    sockets.push(this)
  }
  open() {
    this.readyState = 1
    this.onopen?.({} as Event)
  }
  send(value: string) {
    this.sent.push(value)
  }
  receive(value: unknown) {
    this.onmessage?.({ data: JSON.stringify(value) } as MessageEvent)
  }
  close() {
    this.readyState = 3
  }
  disconnect() {
    this.readyState = 3
    this.onclose?.({} as CloseEvent)
  }
  acknowledge() {
    this.receive({ jsonrpc: '2.0', id: JSON.parse(this.sent.at(-1)!).id, result: {} })
  }
  transcript(event: Record<string, unknown>) {
    this.receive({ jsonrpc: '2.0', method: 'event.transcript', params: { event } })
  }
  hint(sessionId = 'session-1', payload: Record<string, unknown> = {}) {
    this.transcript({ type: 'session.activity', sessionId, runtimeSessionId: null, payload })
  }
  text(text: string, headers: Record<string, unknown> = {}) {
    this.transcript({
      type: 'message.delta',
      sessionId: 'session-1',
      runtimeSessionId: 'runtime-1',
      runtime: 'opencode',
      ...headers,
      payload: { text },
    })
  }
}

const sockets: FakeWebSocket[] = []
const requests: string[] = []
let summary: UnifiedSessionSummaryDto
let publicTurns: SessionTurn[]
let rawTurns: SessionTurn[]
let holdSummary: ReturnType<typeof deferred> | undefined
let client: QueryClient
const viewedIssueHook = () => ({ current: null })
const pathnameReader = () => '/Test/sessions/session-1'

function Harness({ view, historical }: { view: 'public' | 'raw'; historical?: string }) {
  const { data } = useUnifiedSessionSummary('session-1')
  const runtimeSessionId = historical ?? data?.runtimeSessionId ?? ''
  const { data: snapshot } = useUnifiedSessionTranscript('session-1', runtimeSessionId || null, view)
  const summaryKey = useMemo(() => unifiedSessionSummaryQueryOptions(TEST_PROJECT.id, 'session-1').queryKey, [])
  const transcriptKey = useMemo(
    () => unifiedSessionTranscriptQueryOptions(TEST_PROJECT.id, 'session-1', runtimeSessionId || null, view).queryKey,
    [runtimeSessionId, view],
  )
  const transcript = useSessionTranscript({
    issueNumber: 0,
    projectId: TEST_PROJECT.id,
    sessionId: 'session-1',
    runtimeSessionId,
    runtime: data?.runtime,
    view,
    isHistoricalRuntimeView: !!historical,
    initialTurns: snapshot?.turns,
    sessionQueryKeys: [summaryKey, transcriptKey],
    terminalInvalidationKey: summaryKey,
    isRunning: data?.activity === 'active',
  })
  const timeline = useSessionTimeline({
    view,
    turns: transcript.turns,
    liveDetails: transcript.liveDetails,
    summary: data,
  })
  return (
    <>
      <output data-testid="facts">{JSON.stringify(timeline.facts)}</output>
      <output data-testid="activity">{timeline.currentActivity.state}</output>
      <output data-testid="runtime">{runtimeSessionId}</output>
      <output data-testid="details">{JSON.stringify(transcript.liveDetails)}</output>
    </>
  )
}

function mount(view: 'public' | 'raw' = 'public', historical?: string) {
  const tree = (mode: 'public' | 'raw') => (
    <QueryClientProvider client={client}>
      <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
        <LiveTaskProvider viewedIssueHook={viewedIssueHook} pathnameReader={pathnameReader}>
          <Harness view={mode} historical={historical} />
        </LiveTaskProvider>
      </ProjectProvider>
    </QueryClientProvider>
  )
  const result = render(tree(view))
  return { ...result, setView: (mode: 'public' | 'raw') => result.rerender(tree(mode)) }
}
async function settle() {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(0)
  })
}
async function connect() {
  await settle()
  act(() => {
    sockets[0].open()
    sockets[0].acknowledge()
  })
  await settle()
}
function deferred() {
  let resolve!: () => void
  let reject!: (error: Error) => void
  let markStarted!: () => void
  const promise = new Promise<void>((done, fail) => {
    resolve = done
    reject = fail
  })
  const started = new Promise<void>((done) => {
    markStarted = done
  })
  return { promise, resolve, reject, started, markStarted }
}

useMswServer(
  http.get('*/api/projects/test-project/sessions/session-1', async ({ request }) => {
    requests.push(request.url)
    // Capture at request start so a held read cannot observe a later commit.
    const response = HttpResponse.json({ success: true, data: summary })
    const pending = holdSummary
    pending?.markStarted()
    try {
      await pending?.promise
    } catch {
      return HttpResponse.error()
    }
    return response
  }),
  http.get('*/api/projects/test-project/sessions/session-1/transcript', ({ request }) => {
    requests.push(request.url)
    const view = new URL(request.url).searchParams.get('view')
    return HttpResponse.json({
      success: true,
      data: { turns: view === 'raw' ? rawTurns : publicTurns, partCount: 1, lastActivityAt: at },
    })
  }),
)

beforeEach(() => {
  vi.useFakeTimers()
  sockets.length = 0
  requests.length = 0
  holdSummary = undefined
  summary = {
    id: 'session-1',
    source: 'agent-launch',
    runtimeSessionId: null,
    runtime: 'opencode',
    activity: 'idle',
    createdAt: at,
    lastActivityAt: at,
    model: null,
    resolvedModel: null,
    failureCategory: null,
    failureReason: null,
    toolCallCount: 0,
    toolErrorCount: 0,
    contextRefs: null,
    usage: {},
    recoveryAvailable: true,
    inputs: [],
    turns: [],
  }
  publicTurns = [turn('Readable answer')]
  rawTurns = [turn(rawText)]
  client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity } } })
  vi.stubGlobal('WebSocket', FakeWebSocket)
})
afterEach(() => {
  cleanup()
  client.clear()
  vi.unstubAllGlobals()
  vi.useRealTimers()
})

describe('canonical Session saved-state reconciliation through the socket', () => {
  it('refreshes an idle unbound Session on a committed queued-input hint without treating the hint as Activity', async () => {
    mount()
    await connect()
    expect(screen.getByTestId('activity')).toHaveTextContent('idle')
    const before = requests.length
    publicTurns = [turn('Saved queued question')]
    summary = {
      ...summary,
      currentTurnId: 'turn-1',
      inputs: [
        {
          id: 'input-1',
          sequence: 1,
          source: 'slack',
          acceptance: 'accepted',
        },
      ],
      turns: [{ id: 'turn-1', sequence: 1, inputIds: ['input-1'], status: 'queued' }],
    }
    act(() => {
      sockets[0].hint('wrong-session')
      sockets[0].text('unbound detail', { runtimeSessionId: null })
    })
    await settle()
    expect(requests).toHaveLength(before)
    act(() => sockets[0].hint('session-1', { sessionId: 'nested-wrong', runtimeSessionId: 'nested-runtime' }))
    await settle()
    expect(requests.length - before).toBe(2)
    expect(screen.getByTestId('facts')).toHaveTextContent('Saved queued question')
    expect(screen.getByTestId('activity')).toHaveTextContent('queued')
    expect(screen.getByTestId('details')).toHaveTextContent('[]')
  })

  it('coalesces a burst while fetching and performs one trailing read of a newer committed snapshot', async () => {
    mount()
    await connect()
    const before = requests.length
    const gate = deferred()
    holdSummary = gate
    act(() => sockets[0].hint())
    await act(async () => gate.started)
    await settle()
    summary = { ...summary, activity: 'active' }
    publicTurns = [turn('Newest committed text')]
    act(() => {
      for (let index = 0; index < 40; index++) sockets[0].hint()
    })
    expect(requests.length - before).toBe(2)
    holdSummary = undefined
    await act(async () => gate.resolve())
    await settle()
    expect(requests.length - before).toBe(4)
    expect(screen.getByTestId('facts')).toHaveTextContent('Newest committed text')
    expect(screen.getByTestId('activity')).toHaveTextContent('active')
  })

  it('performs a post-commit read when the only hint encounters an existing query request', async () => {
    mount()
    await connect()
    const before = requests.length
    const gate = deferred()
    holdSummary = gate
    let previousQuery!: Promise<void>
    act(() => {
      previousQuery = client.refetchQueries({
        queryKey: unifiedSessionSummaryQueryOptions(TEST_PROJECT.id, 'session-1').queryKey,
        exact: true,
      })
    })
    await act(async () => gate.started)
    expect(requests.length - before).toBe(1)
    summary = { ...summary, activity: 'active' }
    publicTurns = [turn('Saved after the independent read started')]
    act(() => sockets[0].hint())
    await settle()
    expect(requests.length - before).toBe(2)
    expect(screen.getByTestId('activity')).toHaveTextContent('idle')
    holdSummary = undefined
    await act(async () => {
      gate.resolve()
      await previousQuery
    })
    await settle()
    expect(requests.length - before).toBe(4)
    expect(screen.getByTestId('activity')).toHaveTextContent('active')
    expect(screen.getByTestId('facts')).toHaveTextContent('Saved after the independent read started')
  })

  it('keeps Raw details out of Summary, follows Runtime replacement, and never flashes an old Raw tail on mode change', async () => {
    summary = { ...summary, runtimeSessionId: 'runtime-1', activity: 'active' }
    const page = mount()
    await connect()
    const before = requests.length
    act(() => {
      for (let index = 0; index < 30; index++) sockets[0].text('<openviking-context>raw chunk')
    })
    await settle()
    expect(requests).toHaveLength(before)
    expect(screen.getByTestId('facts')).not.toHaveTextContent('openviking-context')
    page.setView('raw')
    await settle()
    expect(screen.getByTestId('facts')).toHaveTextContent('openviking-context')
    act(() => sockets[0].text('raw live tail'))
    expect(screen.getByTestId('facts')).toHaveTextContent('raw live tail')
    act(() =>
      sockets[0].transcript({
        type: 'message.delta',
        sessionId: 'wrong',
        runtimeSessionId: 'old',
        runtime: 'pi',
        payload: { sessionId: 'session-1', runtimeSessionId: 'runtime-1', runtime: 'opencode', text: 'spoofed tail' },
      }),
    )
    expect(screen.getByTestId('facts')).not.toHaveTextContent('spoofed tail')
    page.setView('public')
    expect(screen.getByTestId('facts')).not.toHaveTextContent('openviking-context')
    expect(screen.getByTestId('facts')).not.toHaveTextContent('raw live tail')
    await settle()
    summary = { ...summary, runtimeSessionId: 'runtime-2', runtime: 'pi' }
    publicTurns = [turn('Replacement saved answer')]
    act(() => sockets[0].hint())
    await settle()
    expect(screen.getByTestId('runtime')).toHaveTextContent('runtime-2')
    expect(screen.getByTestId('facts')).toHaveTextContent('Replacement saved answer')
    page.setView('raw')
    await settle()
    act(() => {
      sockets[0].text('late old runtime')
      sockets[0].text('wrong runtime kind', { runtimeSessionId: 'runtime-2' })
      sockets[0].text('missing canonical', { sessionId: undefined, runtimeSessionId: 'runtime-2', runtime: 'pi' })
      sockets[0].text('matching new runtime', { runtimeSessionId: 'runtime-2', runtime: 'pi' })
    })
    expect(screen.getByTestId('facts')).not.toHaveTextContent('late old runtime')
    expect(screen.getByTestId('facts')).not.toHaveTextContent('wrong runtime kind')
    expect(screen.getByTestId('facts')).not.toHaveTextContent('missing canonical')
    expect(screen.getByTestId('facts')).toHaveTextContent('matching new runtime')
  })

  it('keeps a historical Runtime view scoped to its selected physical session', async () => {
    summary = { ...summary, runtimeSessionId: 'runtime-current' }
    mount('raw', 'runtime-old')
    await connect()
    const before = requests.length
    act(() => {
      sockets[0].hint()
      sockets[0].text('current runtime', { runtimeSessionId: 'runtime-current' })
      sockets[0].text('selected historical runtime', { runtimeSessionId: 'runtime-old' })
    })
    await settle()
    expect(requests).toHaveLength(before)
    expect(screen.getByTestId('facts')).not.toHaveTextContent('current runtime')
    expect(screen.getByTestId('facts')).toHaveTextContent('selected historical runtime')
  })

  it('reconciles snapshots before replaying buffered Raw detail after reconnect', async () => {
    summary = { ...summary, runtimeSessionId: 'runtime-1', activity: 'active' }
    mount('raw')
    await connect()
    act(() => sockets[0].disconnect())
    await act(async () => {
      await vi.advanceTimersByTimeAsync(1250)
    })
    const gate = deferred()
    holdSummary = gate
    act(() => {
      sockets[1].open()
      sockets[1].acknowledge()
      sockets[1].text('buffered tail')
    })
    await act(async () => gate.started)
    expect(screen.getByTestId('facts')).not.toHaveTextContent('buffered tail')
    rawTurns = [turn('Reconnected snapshot')]
    holdSummary = undefined
    await act(async () => gate.resolve())
    await settle()
    expect(screen.getByTestId('facts')).toHaveTextContent('Reconnected snapshot')
    expect(screen.getByTestId('facts')).toHaveTextContent('buffered tail')
  })

  it('releases a failed refresh and discards trailing work after unmount', async () => {
    const page = mount()
    await connect()
    const gate = deferred()
    holdSummary = gate
    act(() => sockets[0].hint())
    await act(async () => gate.started)
    holdSummary = undefined
    await act(async () => gate.reject(new Error('read failed')))
    await settle()
    publicTurns = [turn('Recovered committed answer')]
    act(() => sockets[0].hint())
    await settle()
    expect(screen.getByTestId('facts')).toHaveTextContent('Recovered committed answer')
    const lastGate = deferred()
    holdSummary = lastGate
    act(() => {
      sockets[0].hint()
      sockets[0].hint()
    })
    await act(async () => lastGate.started)
    await settle()
    const beforeUnmount = requests.length
    page.unmount()
    holdSummary = undefined
    await act(async () => lastGate.resolve())
    await settle()
    expect(requests).toHaveLength(beforeUnmount)
  })

  it('does not let an old in-flight hint read replace the reconnected snapshot', async () => {
    mount()
    await connect()
    const oldRead = deferred()
    holdSummary = oldRead
    act(() => sockets[0].hint())
    await act(async () => oldRead.started)
    act(() => sockets[0].disconnect())
    await act(async () => {
      await vi.advanceTimersByTimeAsync(1250)
    })
    holdSummary = undefined
    summary = { ...summary, activity: 'active' }
    publicTurns = [turn('New generation snapshot')]
    act(() => {
      sockets[1].open()
      sockets[1].acknowledge()
    })
    await settle()
    await act(async () => oldRead.resolve())
    await settle()
    expect(screen.getByTestId('activity')).toHaveTextContent('active')
    expect(screen.getByTestId('facts')).toHaveTextContent('New generation snapshot')
    const before = requests.length
    act(() => sockets[0].hint())
    await settle()
    expect(requests).toHaveLength(before)
  })
})
