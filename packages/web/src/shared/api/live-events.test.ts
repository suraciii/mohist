import { QueryClient } from '@tanstack/react-query'
import { describe, expect, it, vi } from 'vitest'
import { DOMAIN_EVENT_TYPES, TRANSCRIPT_EVENT_TYPES } from '../lib/canonical-event-types'
import { LiveEventsController, type WebSocketFactory } from './live-events'

class FakeWebSocket {
  readyState = 0
  onopen: ((event: Event) => void) | null = null
  onmessage: ((event: MessageEvent) => void) | null = null
  onclose: ((event: CloseEvent) => void) | null = null
  onerror: ((event: Event) => void) | null = null
  readonly sent: string[] = []
  readonly closeCalls: Array<{ code?: number; reason?: string }> = []

  open() {
    this.readyState = 1
    this.onopen?.({} as Event)
  }

  receive(value: unknown) {
    this.onmessage?.({ data: JSON.stringify(value) } as MessageEvent)
  }

  send(data: string) {
    this.sent.push(data)
  }

  close(code?: number, reason?: string) {
    this.readyState = 3
    this.closeCalls.push({ code, reason })
  }

  disconnect(code = 1006) {
    this.readyState = 3
    this.onclose?.({ code } as CloseEvent)
  }
}

function deferred() {
  let resolve!: () => void
  const promise = new Promise<void>((done) => {
    resolve = done
  })
  return { promise, resolve }
}

function setup(options: { random?: () => number } = {}) {
  const sockets: FakeWebSocket[] = []
  const urls: string[] = []
  const domain = vi.fn()
  const transcript = vi.fn()
  const status = vi.fn()
  const acknowledged = vi.fn()
  const timers: Array<{ callback: () => void; delay: number }> = []
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const controller = new LiveEventsController({
    projectId: 'project/one',
    queryClient,
    onDomainEvent: domain,
    onTranscriptEvent: transcript,
    onStatus: status,
    onAcknowledged: acknowledged,
    createWebSocket: ((url: string) => {
      urls.push(url)
      const socket = new FakeWebSocket()
      sockets.push(socket)
      return socket
    }) as WebSocketFactory,
    setTimer: ((callback: () => void, delay?: number) => {
      timers.push({ callback, delay: delay ?? 0 })
      return timers.length as unknown as ReturnType<typeof setTimeout>
    }) as typeof setTimeout,
    clearTimer: vi.fn() as unknown as typeof clearTimeout,
    random: options.random ?? (() => 0.5),
    location: { protocol: 'https:', host: 'mohist.test' },
  })
  controller.start()
  return { controller, queryClient, sockets, urls, domain, transcript, status, acknowledged, timers }
}

function request(socket: FakeWebSocket, index = 0) {
  return JSON.parse(socket.sent[index]) as {
    jsonrpc: '2.0'
    id: string
    method: string
    params: {
      domain: { types: string[]; match: null }
      transcript: { types: string[] }
      taskLogs: Array<{ workflowRunId: string; taskId: string }>
    }
  }
}

async function acknowledge(socket: FakeWebSocket, index = 0) {
  const sent = request(socket, index)
  socket.receive({ jsonrpc: '2.0', id: sent.id, result: {} })
  await flushPromises()
}

async function flushPromises() {
  for (let index = 0; index < 10; index += 1) await Promise.resolve()
}

describe('LiveEventsController', () => {
  it('opens the project socket and sends the fixed complete subscription first', () => {
    const { sockets, urls } = setup()
    expect(urls).toEqual(['wss://mohist.test/api/projects/project%2Fone/events/socket'])

    sockets[0].open()

    expect(request(sockets[0])).toEqual({
      jsonrpc: '2.0',
      id: 'req_1',
      method: 'subscription.set',
      params: {
        domain: { types: [...DOMAIN_EVENT_TYPES], match: null },
        transcript: { types: [...TRANSCRIPT_EVENT_TYPES] },
        taskLogs: [],
      },
    })
  })

  it('routes the standard structured CloudEvent object without reshaping it', async () => {
    const { sockets, domain } = setup()
    sockets[0].open()
    await acknowledge(sockets[0])
    const event = {
      specversion: '1.0',
      id: 'evt-1',
      source: '/mohist/projects/project-one/issues/42',
      type: 'com.mohist.issue.completed',
      data: { outcome: 'completed' },
      projectid: 'project/one',
      issue: '42',
    }

    sockets[0].receive({ jsonrpc: '2.0', method: 'event.domain', params: { event } })

    expect(domain).toHaveBeenCalledWith(event.type, event)
  })

  it('serializes subscription.set and coalesces changes made while one is in flight', async () => {
    const { controller, sockets } = setup()
    sockets[0].open()
    controller.registerTaskLogScope({ workflowRunId: 'run-1', taskId: 'task-1' }, vi.fn(), async () => {})
    controller.registerTaskLogScope({ workflowRunId: 'run-2', taskId: 'task-2' }, vi.fn(), async () => {})
    expect(sockets[0].sent).toHaveLength(1)

    await acknowledge(sockets[0])

    expect(sockets[0].sent).toHaveLength(2)
    expect(request(sockets[0], 1).params.taskLogs).toEqual([
      { workflowRunId: 'run-1', taskId: 'task-1' },
      { workflowRunId: 'run-2', taskId: 'task-2' },
    ])
  })

  it('keeps the acknowledged snapshot in flight until reconciliation finishes and coalesces the latest pending snapshot', async () => {
    const { controller, sockets } = setup()
    const reconciliation = deferred()
    controller.registerTranscriptReconciliation('session-1', 'runtime-1', () => reconciliation.promise)
    sockets[0].open()
    const ack = acknowledge(sockets[0])

    controller.registerTaskLogScope({ workflowRunId: 'run-1', taskId: 'task-1' }, vi.fn(), async () => {})
    const removed = controller.registerTaskLogScope(
      { workflowRunId: 'run-2', taskId: 'task-2' },
      vi.fn(),
      async () => {},
    )
    if (removed.admitted) removed.dispose()
    expect(sockets[0].sent).toHaveLength(1)

    reconciliation.resolve()
    await ack
    await flushPromises()

    expect(sockets[0].sent).toHaveLength(2)
    expect(request(sockets[0], 1).params.taskLogs).toEqual([{ workflowRunId: 'run-1', taskId: 'task-1' }])
  })

  it('reference-counts duplicate scopes and rejects only the 129th unique scope', async () => {
    const { controller, sockets } = setup()
    sockets[0].open()
    const first = controller.registerTaskLogScope({ workflowRunId: 'run', taskId: 'task-0' }, vi.fn(), async () => {})
    const duplicate = controller.registerTaskLogScope(
      { workflowRunId: 'run', taskId: 'task-0' },
      vi.fn(),
      async () => {},
    )
    for (let index = 1; index < 128; index += 1) {
      expect(
        controller.registerTaskLogScope({ workflowRunId: 'run', taskId: `task-${index}` }, vi.fn(), async () => {})
          .admitted,
      ).toBe(true)
    }
    const rejected = controller.registerTaskLogScope(
      { workflowRunId: 'run', taskId: 'task-128' },
      vi.fn(),
      async () => {},
    )

    expect(first.admitted).toBe(true)
    expect(duplicate.admitted).toBe(true)
    expect(rejected).toEqual({ admitted: false })
    await acknowledge(sockets[0])
    expect(request(sockets[0], 1).params.taskLogs).toHaveLength(128)
    expect(request(sockets[0], 1).params.taskLogs).not.toContainEqual({ workflowRunId: 'run', taskId: 'task-128' })
    if (first.admitted && duplicate.admitted) {
      first.dispose()
      first.dispose()
      expect(sockets[0].sent).toHaveLength(2)
      duplicate.dispose()
      await acknowledge(sockets[0], 1)
      expect(request(sockets[0], 2).params.taskLogs).toHaveLength(127)
      expect(request(sockets[0], 2).params.taskLogs).not.toContainEqual({ workflowRunId: 'run', taskId: 'task-0' })
    }
  })

  it('buffers current-generation transcript events until the authoritative refetch completes', async () => {
    const { controller, sockets, transcript } = setup()
    const refetch = deferred()
    controller.registerTranscriptReconciliation('session-1', 'runtime-1', () => refetch.promise)
    sockets[0].open()
    const ack = acknowledge(sockets[0])
    const event = {
      id: 'part-1',
      sessionId: 'session-1',
      runtimeSessionId: 'runtime-1',
      runtime: 'opencode',
      sequence: 1,
      type: 'message.delta',
      payload: { text: 'after snapshot' },
      createdAt: '2026-08-20T12:00:00Z',
    }
    sockets[0].receive({ jsonrpc: '2.0', method: 'event.transcript', params: { event } })
    expect(transcript).not.toHaveBeenCalled()

    refetch.resolve()
    await ack
    await flushPromises()

    expect(transcript).toHaveBeenCalledWith(event)
  })

  it('refetches duplicate transcript consumers but replays each identity event once', async () => {
    const { controller, sockets, transcript } = setup()
    const first = deferred()
    const second = deferred()
    const firstRefetch = vi.fn(() => first.promise)
    const secondRefetch = vi.fn(() => second.promise)
    controller.registerTranscriptReconciliation('session-1', 'runtime-1', firstRefetch)
    controller.registerTranscriptReconciliation('session-1', 'runtime-1', secondRefetch)
    sockets[0].open()
    const ack = acknowledge(sockets[0])
    const event = {
      sessionId: 'session-1',
      runtimeSessionId: 'runtime-1',
      type: 'message.delta',
      sequence: 7,
    }
    sockets[0].receive({ jsonrpc: '2.0', method: 'event.transcript', params: { event } })

    first.resolve()
    second.resolve()
    await ack
    await flushPromises()

    expect(firstRefetch).toHaveBeenCalledOnce()
    expect(secondRefetch).toHaveBeenCalledOnce()
    expect(transcript).toHaveBeenCalledTimes(1)
    expect(transcript).toHaveBeenCalledWith(event)
  })

  it('buffers task-log deltas until the authoritative read settles, then replays them to every owner', async () => {
    const { controller, sockets, queryClient } = setup()
    const refetch = deferred()
    queryClient.setQueryData(['task-log'], { lines: [{ seq: 1, text: 'older' }] })
    const firstOwner = vi.fn((delta: { entries: Array<{ seq: number; text: string }> }) => {
      queryClient.setQueryData<{ lines: Array<{ seq: number; text: string }> }>(['task-log'], (page) => ({
        lines: [...(page?.lines ?? []), ...delta.entries].sort((left, right) => left.seq - right.seq),
      }))
    })
    const secondOwner = vi.fn()
    controller.registerTaskLogScope({ workflowRunId: 'run-1', taskId: 'task-1' }, firstOwner, async () => {
      await refetch.promise
      queryClient.setQueryData(['task-log'], { lines: [{ seq: 1, text: 'stale response' }] })
    })
    controller.registerTaskLogScope({ workflowRunId: 'run-1', taskId: 'task-1' }, secondOwner, async () => {})
    sockets[0].open()
    const ack = acknowledge(sockets[0])
    const delta = {
      ownerKind: 'workflow',
      ownerId: 'run-1',
      projectId: 'project/one',
      workId: 'work-1',
      taskId: 'task-1',
      entries: [{ seq: 2, timestamp: '2026-08-20T12:00:00Z', source: 'runner', text: 'newer' }],
      truncated: false,
    }
    sockets[0].receive({ jsonrpc: '2.0', method: 'event.task-log', params: { delta } })
    expect(firstOwner).not.toHaveBeenCalled()
    expect(secondOwner).not.toHaveBeenCalled()

    refetch.resolve()
    await ack
    await flushPromises()

    expect(firstOwner).toHaveBeenCalledWith(delta)
    expect(secondOwner).toHaveBeenCalledWith(delta)
    expect(queryClient.getQueryData(['task-log'])).toEqual({
      lines: [
        { seq: 1, text: 'stale response' },
        { seq: 2, timestamp: '2026-08-20T12:00:00Z', source: 'runner', text: 'newer' },
      ],
    })
  })

  it('drops buffered reconciliation work after stop', async () => {
    const { controller, sockets, transcript } = setup()
    const refetch = deferred()
    controller.registerTranscriptReconciliation('session-1', 'runtime-1', () => refetch.promise)
    sockets[0].open()
    const ack = acknowledge(sockets[0])
    sockets[0].receive({
      jsonrpc: '2.0',
      method: 'event.transcript',
      params: { event: { sessionId: 'session-1', runtimeSessionId: 'runtime-1', type: 'message.delta' } },
    })

    controller.stop()
    refetch.resolve()
    await ack
    await flushPromises()

    expect(transcript).not.toHaveBeenCalled()
    expect(sockets[0].sent).toHaveLength(1)
  })

  it('replaces stale transcript buffers when a newer socket generation begins reconciliation', async () => {
    const { controller, sockets, timers, transcript } = setup()
    const first = deferred()
    const second = deferred()
    let reconciliationCount = 0
    controller.registerTranscriptReconciliation('session-1', 'runtime-1', () => {
      reconciliationCount += 1
      return reconciliationCount === 1 ? first.promise : second.promise
    })
    sockets[0].open()
    const firstAck = acknowledge(sockets[0])
    await firstAck
    sockets[0].receive({
      jsonrpc: '2.0',
      method: 'event.transcript',
      params: { event: { sessionId: 'session-1', runtimeSessionId: 'runtime-1', sequence: 1 } },
    })
    sockets[0].disconnect()
    timers[0].callback()
    sockets[1].open()
    const secondAck = acknowledge(sockets[1])
    const currentEvent = { sessionId: 'session-1', runtimeSessionId: 'runtime-1', sequence: 2 }
    sockets[1].receive({ jsonrpc: '2.0', method: 'event.transcript', params: { event: currentEvent } })

    first.resolve()
    await flushPromises()
    expect(transcript).not.toHaveBeenCalled()

    second.resolve()
    await secondAck
    await flushPromises()
    expect(transcript).toHaveBeenCalledTimes(1)
    expect(transcript).toHaveBeenCalledWith(currentEvent)
  })

  it('does not start stream refetch callbacks after stop during project-query reconciliation', async () => {
    const { controller, sockets, queryClient } = setup()
    const invalidation = deferred()
    vi.spyOn(queryClient, 'invalidateQueries').mockImplementation(() => invalidation.promise)
    const reconcile = vi.fn(async () => {})
    controller.registerTranscriptReconciliation('session-1', 'runtime-1', reconcile)
    sockets[0].open()
    const ack = acknowledge(sockets[0])

    controller.stop()
    invalidation.resolve()
    await ack
    await flushPromises()

    expect(reconcile).not.toHaveBeenCalled()
  })

  it('invalidates project queries and reconciles every registered stream after acknowledgement', async () => {
    const { controller, queryClient, sockets, acknowledged } = setup()
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries')
    const transcriptRefetch = vi.fn(async () => {})
    const taskLogRefetch = vi.fn(async () => {})
    controller.registerTranscriptReconciliation('session-1', 'runtime-1', transcriptRefetch)
    controller.registerTaskLogScope({ workflowRunId: 'run-1', taskId: 'task-1' }, vi.fn(), taskLogRefetch)
    sockets[0].open()

    await acknowledge(sockets[0])

    expect(acknowledged).toHaveBeenCalledOnce()
    expect(invalidate).toHaveBeenCalledOnce()
    expect(transcriptRefetch).toHaveBeenCalledOnce()
    expect(taskLogRefetch).toHaveBeenCalledOnce()
  })

  it('uses bounded exponential retry with deterministic jitter and resets only after a valid acknowledgement', async () => {
    const randomValues = [0, 1, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5]
    const { sockets, timers, status } = setup({ random: () => randomValues.shift() ?? 0.5 })
    sockets[0].open()
    sockets[0].disconnect(1013)
    expect(status).toHaveBeenLastCalledWith('reconnecting')
    expect(timers[0].delay).toBe(750)
    timers[0].callback()
    sockets[1].open()
    sockets[1].disconnect(1013)
    expect(timers[1].delay).toBe(2500)

    for (let index = 2; index < 7; index += 1) {
      timers[index - 1].callback()
      sockets[index].open()
      sockets[index].disconnect(1013)
    }
    expect(timers.map((timer) => timer.delay)).toEqual([750, 2500, 4000, 8000, 16000, 30000, 30000])

    timers[6].callback()
    sockets[7].open()
    await acknowledge(sockets[7])
    sockets[7].disconnect(1013)
    expect(timers[7].delay).toBe(1000)
  })

  it.each([
    ['missing result and error', {}],
    ['wrong JSON-RPC version', { jsonrpc: '1.0', result: {} }],
    ['both result and error', { result: {}, error: { code: -1 } }],
    ['non-object result', { result: true }],
    ['error response', { error: { code: -1 } }],
  ])('rejects a matching-ID %s without acknowledging or reconciling', async (_name, response) => {
    const { controller, sockets, acknowledged, status } = setup()
    const reconcile = vi.fn(async () => {})
    controller.registerTranscriptReconciliation('session-1', 'runtime-1', reconcile)
    sockets[0].open()
    const sent = request(sockets[0])

    sockets[0].receive({ jsonrpc: '2.0', id: sent.id, ...response })
    await Promise.resolve()

    expect(sockets[0].closeCalls).toHaveLength(1)
    expect(acknowledged).not.toHaveBeenCalled()
    expect(reconcile).not.toHaveBeenCalled()
    expect(status).not.toHaveBeenCalledWith('connected')
  })
})
