import { EventEmitter } from 'node:events'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { RunnerControlHandlers } from './runner-control-dispatcher.js'
import { buildControlUrl, RunnerControlWebSocketClient } from './runner-control-websocket.js'
import {
  RUNNER_CONTROL_HANDSHAKE_TIMEOUT_MS,
  RUNNER_CONTROL_MAX_MESSAGE_BYTES,
  runnerControlSocketOptions,
  type RunnerControlSocket,
  type RunnerControlSocketFactory,
} from './runner-control-websocket-resource.js'

class FakeSocket extends EventEmitter {
  readonly sent: string[] = []
  readonly closes: Array<[number, string]> = []
  pings = 0
  terminated = 0
  holdSends = false
  private readonly callbacks: Array<(error?: Error) => void> = []

  send(text: string, callback: (error?: Error) => void): void {
    this.sent.push(text)
    if (this.holdSends) this.callbacks.push(callback)
    else callback()
  }
  completeSend(error?: Error): void {
    this.callbacks.shift()?.(error)
  }
  ping(): void {
    this.pings++
  }
  close(code: number, reason: string): void {
    this.closes.push([code, reason])
    this.emit('close', code, Buffer.from(reason))
  }
  terminate(): void {
    this.terminated++
    this.emit('close', 1006, Buffer.alloc(0))
  }
  open(): void {
    this.emit('open')
  }
  text(value: unknown): void {
    this.emit('message', Buffer.from(JSON.stringify(value)), false)
  }
  textRaw(value: Buffer): void {
    this.emit('message', value, false)
  }
  binary(): void {
    this.emit('message', Buffer.from([1]), true)
  }
  pong(): void {
    this.emit('pong', Buffer.alloc(0))
  }
  peerClose(): void {
    this.emit('close', 1006, Buffer.alloc(0))
  }
}

function handlers(overrides: Partial<RunnerControlHandlers> = {}): RunnerControlHandlers {
  const result = async () => ({ ok: true })
  return {
    workspaceDiff: result,
    workspaceCommits: result,
    workspaceCommitDiff: result,
    workspaceStatus: result,
    workspaceFileContent: result,
    workspaceRemove: result,
    sessionFollowup: result,
    sessionStop: result,
    sessionCommand: result,
    workflowStatusChanged: vi.fn(),
    ...overrides,
  }
}

function fixture(
  options: {
    handlers?: RunnerControlHandlers
    random?: () => number
    outbox?: unknown
    onReconnected?: (id: string) => void
  } = {},
) {
  const sockets: FakeSocket[] = []
  const attempts: Array<{ url: string; credential: string | null; id: string }> = []
  const factory: RunnerControlSocketFactory = (url, credential) => {
    const socket = new FakeSocket()
    const id = `00000000-0000-4000-8000-${String(sockets.length + 1).padStart(12, '0')}`
    sockets.push(socket)
    attempts.push({ url, credential, id })
    return { socket: socket as unknown as RunnerControlSocket, connectionId: id }
  }
  const client = new RunnerControlWebSocketClient(
    'https://server.test/base/',
    'runner / one',
    '/root',
    'git-hash',
    {
      credential: 'secret',
      handlers: options.handlers ?? handlers(),
      socketFactory: factory,
      random: options.random ?? (() => 0.5),
      onReconnected: options.onReconnected,
      agentSessionRuntimeEventOutbox: options.outbox as never,
    },
    {
      gitHash: 'manifest-hash',
      component: 'runner',
      version: '1.2.3',
      sourceRevision: 'source',
      treeHash: 'tree',
      artifactDigest: 'artifact',
      releaseId: 'release',
      generation: 7,
      builtAt: null,
      runnerId: null,
    },
  )
  return { client, sockets, attempts }
}

async function startOpen(value: ReturnType<typeof fixture>): Promise<void> {
  const started = value.client.start()
  await Promise.resolve()
  await Promise.resolve()
  value.sockets[0].open()
  await started
}

describe('RunnerControlWebSocketClient', () => {
  beforeEach(() => vi.useFakeTimers())
  afterEach(() => vi.useRealTimers())

  it('builds the ws URL and production upgrade options exactly', () => {
    const url = buildControlUrl('http://server.test/base/', 'runner / one', 'hash', null)
    expect(url).toBe('ws://server.test/base/api/runner/runner%20%2F%20one/control?buildGitHash=hash')
    expect(new URL(url).searchParams.has('runnerId')).toBe(false)
    expect(runnerControlSocketOptions('id', 'token')).toMatchObject({
      headers: { 'X-Runner-Connection-Id': 'id', Authorization: 'Bearer token' },
      maxPayload: RUNNER_CONTROL_MAX_MESSAGE_BYTES,
      handshakeTimeout: RUNNER_CONTROL_HANDSHAKE_TIMEOUT_MS,
      perMessageDeflate: false,
    })
  })

  it('loads outbox before opening and exposes the ID only for the current open socket', async () => {
    const order: string[] = []
    const outbox = {
      recover: vi.fn(async () => {
        order.push('recover')
      }),
      stop: vi.fn(async () => {}),
    }
    const f = fixture({ outbox })
    const started = f.client.start()
    expect(order).toEqual(['recover'])
    expect(f.sockets).toHaveLength(0)
    await Promise.resolve()
    await Promise.resolve()
    expect(f.sockets).toHaveLength(1)
    expect(f.client.getConnectionId()).toBeNull()
    f.sockets[0].open()
    await started
    expect(f.client.getConnectionId()).toBe(f.attempts[0].id)
    f.sockets[0].peerClose()
    expect(f.client.getConnectionId()).toBeNull()
  })

  it('keeps stop and disconnect outbox behavior distinct', async () => {
    const outbox = { recover: vi.fn(async () => {}), stop: vi.fn(async () => {}) }
    const first = fixture({ outbox })
    await startOpen(first)
    await first.client.disconnect()
    expect(outbox.stop).not.toHaveBeenCalled()
    const second = fixture({ outbox })
    await startOpen(second)
    await second.client.stop()
    expect(outbox.stop).toHaveBeenCalledOnce()
  })

  it('fences startup before blocked recovery completes', async () => {
    let finishRecovery!: () => void
    const outbox = {
      recover: vi.fn(() => new Promise<void>((resolve) => (finishRecovery = resolve))),
      stop: vi.fn(async () => {}),
    }
    const f = fixture({ outbox })
    const started = f.client.start()
    await Promise.resolve()
    await f.client.stop()
    await expect(started).rejects.toThrow('Runner control client stopped')
    finishRecovery()
    await vi.advanceTimersByTimeAsync(60_000)
    expect(f.sockets).toHaveLength(0)
  })

  it('shares one recovery and socket attempt across concurrent starts', async () => {
    const outbox = { recover: vi.fn(async () => {}), stop: vi.fn(async () => {}) }
    const f = fixture({ outbox })
    const first = f.client.start()
    const second = f.client.start()
    await Promise.resolve()
    await Promise.resolve()
    expect(outbox.recover).toHaveBeenCalledOnce()
    expect(f.sockets).toHaveLength(1)
    f.sockets[0].open()
    await expect(Promise.all([first, second])).resolves.toEqual([undefined, undefined])
  })

  it('aborts initial upgrade retries and never creates a later socket', async () => {
    const controller = new AbortController()
    const f = fixture()
    const started = f.client.start(controller.signal)
    await Promise.resolve()
    f.sockets[0].peerClose()
    controller.abort(new Error('host stopped'))
    await expect(started).rejects.toThrow('host stopped')
    await vi.advanceTimersByTimeAsync(60_000)
    expect(f.sockets).toHaveLength(1)
  })

  it('uses 0, 2s, 5s, 10s, then repeated 30s jittered retries until Pong resets it', async () => {
    const f = fixture()
    await startOpen(f)
    f.sockets[0].peerClose()
    await vi.advanceTimersByTimeAsync(0)
    expect(f.sockets).toHaveLength(2)
    f.sockets[1].emit('error', new Error('upgrade'))
    f.sockets[1].peerClose()
    await vi.advanceTimersByTimeAsync(1_999)
    expect(f.sockets).toHaveLength(2)
    await vi.advanceTimersByTimeAsync(1)
    f.sockets[2].peerClose()
    await vi.advanceTimersByTimeAsync(5_000)
    f.sockets[3].open()
    f.sockets[3].pong()
    f.sockets[3].peerClose()
    await vi.advanceTimersByTimeAsync(0)
    expect(f.sockets).toHaveLength(5)
  })

  it('pings, fences on the Pong deadline, and probes liveness', async () => {
    const f = fixture()
    await startOpen(f)
    await vi.advanceTimersByTimeAsync(15_000)
    expect(f.sockets[0].pings).toBe(1)
    await vi.advanceTimersByTimeAsync(10_000)
    expect(f.sockets[0].closes).toContainEqual([1001, 'Pong timeout'])

    const live = fixture()
    await startOpen(live)
    const probe = live.client.probeLiveness(new AbortController().signal)
    live.sockets[0].pong()
    await expect(probe).resolves.toBe(true)
  })

  it('drains queued responses before a Ping deferred by an active send', async () => {
    const f = fixture()
    await startOpen(f)
    const socket = f.sockets[0]
    socket.holdSends = true
    socket.text({ jsonrpc: '2.0', id: 'one', method: 'workspace.diff', params: { query: {} } })
    await Promise.resolve()
    await Promise.resolve()
    socket.text({ jsonrpc: '2.0', id: 'two', method: 'workspace.diff', params: { query: {} } })
    await Promise.resolve()
    await Promise.resolve()
    await vi.advanceTimersByTimeAsync(15_000)
    expect(socket.sent).toHaveLength(1)
    expect(socket.pings).toBe(0)
    socket.completeSend()
    expect(socket.sent).toHaveLength(2)
    expect(socket.pings).toBe(0)
    socket.completeSend()
    expect(socket.pings).toBe(1)
  })

  it('force reconnect waits for a new open and invokes recovery callbacks only after later opens', async () => {
    const reconnected = vi.fn()
    const recover = vi.fn(async () => {})
    const f = fixture({ outbox: { recover, stop: vi.fn() }, onReconnected: reconnected })
    await startOpen(f)
    const reconnect = f.client.forceReconnect(new AbortController().signal)
    await vi.advanceTimersByTimeAsync(0)
    f.sockets[1].open()
    await reconnect
    expect(reconnected).toHaveBeenCalledWith(f.attempts[1].id)
    expect(recover).toHaveBeenCalledTimes(2)
  })

  it('closes on the third protocol error, binary input, and response queue saturation', async () => {
    const pending: Array<(value: unknown) => void> = []
    const f = fixture({ handlers: handlers({ workspaceDiff: () => new Promise((resolve) => pending.push(resolve)) }) })
    await startOpen(f)
    const socket = f.sockets[0]
    socket.text({ jsonrpc: '2.0', id: 'a', method: 'bad', params: {} })
    socket.text({ jsonrpc: '2.0', id: 'b', method: 'bad', params: {} })
    socket.text({ jsonrpc: '2.0', id: 'c', method: 'bad', params: {} })
    expect(socket.closes).toContainEqual([1008, 'Too many protocol errors'])

    const binary = fixture()
    await startOpen(binary)
    binary.sockets[0].binary()
    binary.sockets[0].binary()
    binary.sockets[0].binary()
    expect(binary.sockets[0].closes).toContainEqual([1008, 'Too many protocol errors'])

    const queued = fixture({
      handlers: handlers({ workspaceDiff: () => new Promise((resolve) => pending.push(resolve)) }),
    })
    await startOpen(queued)
    queued.sockets[0].holdSends = true
    for (let index = 0; index < 66; index++)
      queued.sockets[0].text({ jsonrpc: '2.0', id: `q-${index}`, method: 'workspace.diff', params: { query: {} } })
    pending.splice(0).forEach((resolve, index) => resolve(index))
    await Promise.resolve()
    await Promise.resolve()
    queued.sockets[0].completeSend()
    expect(queued.sockets[0].closes).toContainEqual([1013, 'Outgoing queue saturated'])
  })

  it('keeps a request ID live until its held send completes', async () => {
    const workspaceDiff = vi.fn(async () => 'diff')
    const f = fixture({ handlers: handlers({ workspaceDiff }) })
    await startOpen(f)
    const socket = f.sockets[0]
    socket.holdSends = true
    const request = { jsonrpc: '2.0', id: 'same', method: 'workspace.diff', params: { query: {} } }
    socket.text(request)
    await Promise.resolve()
    await Promise.resolve()
    socket.text(request)
    await Promise.resolve()
    await Promise.resolve()
    expect(workspaceDiff).toHaveBeenCalledOnce()
    expect(socket.sent.map((text) => JSON.parse(text))).toEqual([{ jsonrpc: '2.0', id: 'same', result: 'diff' }])
    socket.completeSend()
    expect(socket.sent.map((text) => JSON.parse(text))).toContainEqual({
      jsonrpc: '2.0',
      id: 'same',
      error: { code: -32600, message: 'Invalid Request' },
    })
  })

  it('converts oversized results and drops output when disconnected during a mutating handler', async () => {
    let finish!: (value: unknown) => void
    const effect = vi.fn(
      () =>
        new Promise((resolve) => {
          finish = resolve
        }),
    )
    const f = fixture({ handlers: handlers({ sessionStop: effect }) })
    await startOpen(f)
    f.sockets[0].text({
      jsonrpc: '2.0',
      id: 'stop',
      method: 'session.stop',
      params: {
        target: {
          kind: 'generic',
          projectId: 'p',
          sessionId: 's',
          binding: { runtime: 'r', runtimeSessionId: 'rs', runnerId: 'runner', workDir: null },
        },
        sessionId: 's',
        turnId: 't',
        operationId: 'o',
      },
    })
    await f.client.disconnect()
    finish({ state: 'stopped' })
    await Promise.resolve()
    await Promise.resolve()
    expect(effect).toHaveBeenCalledOnce()
    expect(f.sockets[0].sent).toEqual([])

    const large = fixture({
      handlers: handlers({ workspaceDiff: async () => 'x'.repeat(RUNNER_CONTROL_MAX_MESSAGE_BYTES) }),
    })
    await startOpen(large)
    large.sockets[0].text({ jsonrpc: '2.0', id: 'large', method: 'workspace.diff', params: { query: {} } })
    await Promise.resolve()
    await Promise.resolve()
    expect(JSON.parse(large.sockets[0].sent[0])).toEqual({
      jsonrpc: '2.0',
      id: 'large',
      error: { code: -32001, message: 'Response too large' },
    })

    const emptyEnvelopeBytes = Buffer.byteLength(JSON.stringify({ jsonrpc: '2.0', id: 'exact', result: '' }))
    const exact = fixture({
      handlers: handlers({
        workspaceDiff: async () => 'x'.repeat(RUNNER_CONTROL_MAX_MESSAGE_BYTES - emptyEnvelopeBytes),
      }),
    })
    await startOpen(exact)
    exact.sockets[0].text({ jsonrpc: '2.0', id: 'exact', method: 'workspace.diff', params: { query: {} } })
    await Promise.resolve()
    await Promise.resolve()
    expect(Buffer.byteLength(exact.sockets[0].sent[0])).toBe(RUNNER_CONTROL_MAX_MESSAGE_BYTES)
    expect(JSON.parse(exact.sockets[0].sent[0]).result).toHaveLength(
      RUNNER_CONTROL_MAX_MESSAGE_BYTES - emptyEnvelopeBytes,
    )
  })

  it('shuts down while retrying without opening another socket', async () => {
    const f = fixture()
    await startOpen(f)
    f.sockets[0].peerClose()
    await vi.advanceTimersByTimeAsync(0)
    f.sockets[1].peerClose()
    await f.client.disconnect()
    await vi.advanceTimersByTimeAsync(60_000)
    expect(f.sockets).toHaveLength(2)
  })

  it('owns and closes an in-progress handshake during shutdown', async () => {
    const f = fixture()
    const started = f.client.start()
    await Promise.resolve()
    expect(f.sockets).toHaveLength(1)
    await f.client.disconnect()
    await expect(started).rejects.toThrow('Runner control client stopped')
    expect(f.sockets[0].closes).toContainEqual([1000, 'Shutdown'])
  })

  it('does not report startup success when cancellation races the open event', async () => {
    const f = fixture()
    const controller = new AbortController()
    const started = f.client.start(controller.signal)
    await Promise.resolve()
    f.sockets[0].open()
    controller.abort(new Error('cancelled'))

    await expect(started).rejects.toThrow('cancelled')
    expect(f.client.getConnectionId()).toBeNull()
  })

  it('accepts exactly 4 MiB inbound and closes 1009 above the limit', async () => {
    const exact = fixture()
    await startOpen(exact)
    exact.sockets[0].textRaw(Buffer.alloc(RUNNER_CONTROL_MAX_MESSAGE_BYTES, 0x20))
    expect(exact.sockets[0].closes).toEqual([])

    const oversized = fixture()
    await startOpen(oversized)
    oversized.sockets[0].textRaw(Buffer.alloc(RUNNER_CONTROL_MAX_MESSAGE_BYTES + 1, 0x20))
    expect(oversized.sockets[0].closes).toContainEqual([1009, 'Message too large'])
  })
})
