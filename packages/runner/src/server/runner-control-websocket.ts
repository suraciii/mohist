import type { BuildInfo } from '../runtime/build-info.js'
import type { CancelOperationJournalStore } from '../runtime/cancel-operation-journal.js'
import type { FollowupOperationJournalStore } from '../runtime/followup-operation-journal.js'
import type { SessionCommandJournalStore } from '../runtime/session-command-journal.js'
import { runnerLogger } from '../system/logger.js'
import type { AgentSessionRuntimeEventOutbox } from './runtime-event-outbox.js'
import { RunnerControlDispatcher, type RunnerControlHandlers } from './runner-control-dispatcher.js'
import {
  createRunnerControlSocket,
  RUNNER_CONTROL_MAX_MESSAGE_BYTES,
  type RunnerControlSocket,
  type RunnerControlSocketFactory,
} from './runner-control-websocket-resource.js'

const log = runnerLogger.child('connection')
const RETRY_DELAYS_MS = [0, 2_000, 5_000, 10_000, 30_000] as const
const PING_INTERVAL_MS = 15_000
const PONG_DEADLINE_MS = 10_000
const CLOSE_TIMEOUT_MS = 5_000
const RESPONSE_QUEUE_CAPACITY = 64

export interface RunnerControlWebSocketClientOptions {
  credential?: string | null
  handlers: RunnerControlHandlers
  socketFactory?: RunnerControlSocketFactory
  random?: () => number
  onReconnected?: (connectionId: string) => void
  agentSessionRuntimeEventOutbox?: AgentSessionRuntimeEventOutbox | null
  sessionCommandJournal?: SessionCommandJournalStore | null
  followupOperationJournal?: FollowupOperationJournalStore | null
  cancelOperationJournal?: CancelOperationJournalStore | null
  strictExecutionSourceValidation?: boolean
}

interface Connection {
  readonly epoch: number
  readonly socket: RunnerControlSocket
  readonly connectionId: string
  readonly dispatcher: RunnerControlDispatcher
  readonly responses: OutputItem[]
  sending: OutputItem | null
  fenced: boolean
  pongSeen: boolean
  protocolErrors: number
  pingPending: boolean
  closePending: (() => void) | null
  pingTimer: ReturnType<typeof setTimeout> | null
  pongTimer: ReturnType<typeof setTimeout> | null
}

interface OutputItem {
  text: string
  complete?: () => void
}

export class RunnerControlWebSocketClient {
  private readonly url: string
  private readonly factory: RunnerControlSocketFactory
  private readonly random: () => number
  private readonly outbox: AgentSessionRuntimeEventOutbox | null
  private readonly journals: Array<{ load(): Promise<void> }>
  private running = false
  private candidate: Connection | null = null
  private current: Connection | null = null
  private epoch = 0
  private lifecycleGeneration = 0
  private retryIndex = 0
  private retryTimer: ReturnType<typeof setTimeout> | null = null
  private successfulOpens = 0
  private startup: Promise<void> | null = null
  private startupGeneration = 0
  private startupResolve: (() => void) | null = null
  private startupReject: ((error: unknown) => void) | null = null
  private readonly openWaiters = new Set<{
    afterEpoch: number
    resolve(): void
    reject(error: unknown): void
    signal: AbortSignal
  }>()

  constructor(
    serverUrl: string,
    runnerId: string,
    _runnerRoot: string,
    buildGitHash: string | null = null,
    private readonly options: RunnerControlWebSocketClientOptions,
    buildInfo: BuildInfo | null = null,
  ) {
    this.url = buildControlUrl(serverUrl, runnerId, buildGitHash, buildInfo)
    this.factory = options.socketFactory ?? createRunnerControlSocket
    this.random = options.random ?? Math.random
    this.outbox = options.agentSessionRuntimeEventOutbox ?? null
    this.journals = []
    if (options.sessionCommandJournal) this.journals.push(options.sessionCommandJournal)
    if (options.followupOperationJournal) this.journals.push(options.followupOperationJournal)
    if (options.cancelOperationJournal) this.journals.push(options.cancelOperationJournal)
  }

  async start(signal?: AbortSignal): Promise<void> {
    if (signal?.aborted) throw signal.reason
    if (this.current && !this.current.fenced) return
    if (!this.startup) {
      const generation = ++this.lifecycleGeneration
      this.startupGeneration = generation
      this.running = true
      this.startup = this.runStartup(generation)
    }
    const startup = this.startup
    const generation = this.startupGeneration
    const abort = () => {
      if (this.ownsLifecycle(generation)) void this.shutdownConnection(signal?.reason)
    }
    signal?.addEventListener('abort', abort, { once: true })
    if (signal?.aborted) abort()
    try {
      await startup
      if (signal?.aborted) throw signal.reason
    } finally {
      signal?.removeEventListener('abort', abort)
      if (this.startup === startup) this.startup = null
    }
  }

  private async runStartup(generation: number): Promise<void> {
    const opened = new Promise<void>((resolve, reject) => {
      this.startupResolve = resolve
      this.startupReject = reject
    })
    try {
      if (this.outbox) await Promise.race([this.outbox.recover(), opened])
      if (!this.ownsLifecycle(generation)) return await opened
      for (const journal of this.journals) {
        try {
          await Promise.race([journal.load(), opened])
        } catch (error) {
          if (!this.ownsLifecycle(generation)) return await opened
          log.error('control journal failed to load', { exception: error })
        }
        if (!this.ownsLifecycle(generation)) return await opened
      }
      this.connect()
    } catch (error) {
      if (this.ownsLifecycle(generation)) this.startupReject?.(error)
    }
    return await opened
  }

  async stop(): Promise<void> {
    const shutdown = this.shutdownConnection()
    if (this.outbox) await this.outbox.stop()
    await shutdown
  }

  async disconnect(): Promise<void> {
    await this.shutdownConnection()
  }

  getConnectionId(): string | null {
    return this.current && !this.current.fenced ? this.current.connectionId : null
  }

  async probeLiveness(signal: AbortSignal): Promise<boolean> {
    const connection = this.current
    if (!connection || connection.fenced || signal.aborted) return false
    return await new Promise<boolean>((resolve) => {
      const finish = (value: boolean) => {
        clearTimeout(timeout)
        signal.removeEventListener('abort', aborted)
        connection.socket.off('pong', pong)
        connection.socket.off('close', closed)
        resolve(value)
      }
      const aborted = () => finish(false)
      const pong = () => finish(true)
      const closed = () => finish(false)
      const timeout = setTimeout(() => finish(false), PONG_DEADLINE_MS)
      timeout.unref?.()
      signal.addEventListener('abort', aborted, { once: true })
      connection.socket.once('pong', pong)
      connection.socket.once('close', closed)
      this.ping(connection)
    })
  }

  async forceReconnect(signal: AbortSignal): Promise<void> {
    if (signal.aborted) throw signal.reason
    const wait = this.waitForOpen(signal)
    const connection = this.current ?? this.candidate
    if (connection) this.fence(connection, 1012, 'Reconnect')
    else if (this.running && !this.retryTimer) this.scheduleReconnect()
    await wait
  }

  private connect(): void {
    if (!this.running || this.current || this.candidate) return
    const epoch = ++this.epoch
    let attempt
    try {
      attempt = this.factory(this.url, this.options.credential ?? null)
    } catch (error) {
      log.error('control WebSocket construction failed', { exception: error })
      this.scheduleReconnect()
      return
    }
    const connection = {} as Connection
    Object.assign(connection, {
      epoch,
      socket: attempt.socket,
      connectionId: attempt.connectionId,
      responses: [],
      sending: null,
      fenced: false,
      pongSeen: false,
      protocolErrors: 0,
      pingPending: false,
      closePending: null,
      pingTimer: null,
      pongTimer: null,
      dispatcher: new RunnerControlDispatcher(
        this.options.handlers,
        {
          enqueue: (value, complete) => this.enqueue(connection, value, complete),
          protocolError: () => this.protocolError(connection),
          isCurrent: () => this.current === connection && !connection.fenced,
        },
        {
          strictExecutionSourceValidation: this.options.strictExecutionSourceValidation === true,
        },
      ),
    })
    const socket = connection.socket
    this.candidate = connection
    socket.once('open', () => this.opened(connection))
    socket.on('message', (data, isBinary) => this.message(connection, data, isBinary))
    socket.on('pong', () => this.pong(connection))
    socket.once('close', () => this.closed(connection))
    socket.once('error', (error) => log.info('control WebSocket error', { exception: error }))
  }

  private opened(connection: Connection): void {
    if (!this.running || connection.epoch !== this.epoch || this.candidate !== connection || this.current) {
      this.fence(connection, 1000, 'Stale connection')
      return
    }
    this.candidate = null
    this.current = connection
    this.successfulOpens++
    this.startupResolve?.()
    this.startupResolve = null
    this.startupReject = null
    if (this.successfulOpens > 1) {
      try {
        this.options.onReconnected?.(connection.connectionId)
      } catch (error) {
        log.error('control reconnect callback failed', { exception: error })
      }
      void this.finishReconnect(connection)
    } else {
      this.resolveOpenWaiters(connection)
    }
    this.schedulePing(connection)
  }

  private async finishReconnect(connection: Connection): Promise<void> {
    try {
      await this.outbox?.recover()
    } catch (error) {
      log.error('runtime event outbox recovery failed', { exception: error })
    }
    this.resolveOpenWaiters(connection)
  }

  private resolveOpenWaiters(connection: Connection): void {
    if (this.current !== connection || connection.fenced) return
    for (const waiter of this.openWaiters) {
      if (connection.epoch <= waiter.afterEpoch) continue
      this.openWaiters.delete(waiter)
      waiter.resolve()
    }
  }

  private message(connection: Connection, data: Buffer | ArrayBuffer | Buffer[], isBinary: boolean): void {
    if (this.current !== connection || connection.fenced) return
    if (isBinary) {
      this.protocolError(connection)
      return
    }
    const bytes = Array.isArray(data)
      ? Buffer.concat(data)
      : data instanceof ArrayBuffer
        ? Buffer.from(data)
        : Buffer.from(data.buffer, data.byteOffset, data.byteLength)
    if (bytes.byteLength > RUNNER_CONTROL_MAX_MESSAGE_BYTES) {
      this.fence(connection, 1009, 'Message too large')
      return
    }
    connection.dispatcher.receive(bytes.toString('utf8'))
  }

  private enqueue(connection: Connection, value: unknown, complete?: () => void): boolean {
    if (this.current !== connection || connection.fenced) return false
    let text = JSON.stringify(value)
    if (Buffer.byteLength(text) > RUNNER_CONTROL_MAX_MESSAGE_BYTES) {
      const id =
        typeof value === 'object' && value !== null && 'id' in value && typeof value.id === 'string' ? value.id : null
      text = JSON.stringify({ jsonrpc: '2.0', id, error: { code: -32001, message: 'Response too large' } })
    }
    if (connection.responses.length >= RESPONSE_QUEUE_CAPACITY) {
      this.fence(connection, 1013, 'Outgoing queue saturated')
      return false
    }
    connection.responses.push({ text, complete })
    this.drain(connection)
    return true
  }

  private drain(connection: Connection): void {
    if (connection.sending || connection.fenced || this.current !== connection) return
    const item = connection.responses.shift()
    if (item === undefined) return
    connection.sending = item
    connection.socket.send(item.text, (error) => {
      if (connection.sending !== item) return
      connection.sending = null
      this.complete(item)
      if (error) this.fence(connection, 1011, 'Send failed')
      else if (connection.closePending) connection.closePending()
      else if (connection.responses.length > 0) this.drain(connection)
      else if (connection.pingPending) {
        connection.pingPending = false
        this.ping(connection)
      }
    })
  }

  private schedulePing(connection: Connection): void {
    connection.pingTimer = setTimeout(() => {
      connection.pingTimer = null
      this.ping(connection)
      if (!connection.fenced) this.schedulePing(connection)
    }, PING_INTERVAL_MS)
    connection.pingTimer.unref?.()
  }

  private ping(connection: Connection): void {
    if (this.current !== connection || connection.fenced || connection.pongTimer) return
    if (connection.sending || connection.responses.length > 0) {
      connection.pingPending = true
      return
    }
    try {
      connection.socket.ping()
      connection.pongTimer = setTimeout(() => this.fence(connection, 1001, 'Pong timeout'), PONG_DEADLINE_MS)
      connection.pongTimer.unref?.()
    } catch {
      this.fence(connection, 1011, 'Ping failed')
    }
  }

  private pong(connection: Connection): void {
    if (this.current !== connection || connection.fenced) return
    if (connection.pongTimer) clearTimeout(connection.pongTimer)
    connection.pongTimer = null
    if (!connection.pongSeen) {
      connection.pongSeen = true
      this.retryIndex = 0
    }
  }

  private protocolError(connection: Connection): void {
    connection.protocolErrors++
    if (connection.protocolErrors >= 3) this.fence(connection, 1008, 'Too many protocol errors')
  }

  private fence(connection: Connection, code: number, reason: string): void {
    if (connection.fenced) return
    connection.fenced = true
    const wasSending = connection.sending !== null
    if (connection.pingTimer) clearTimeout(connection.pingTimer)
    if (connection.pongTimer) clearTimeout(connection.pongTimer)
    this.releaseOutput(connection)
    if (this.candidate === connection) this.candidate = null
    if (this.current === connection) this.current = null
    const close = () => {
      try {
        connection.socket.close(code, reason)
      } catch {
        connection.socket.terminate()
      }
      const timer = setTimeout(() => connection.socket.terminate(), CLOSE_TIMEOUT_MS)
      timer.unref?.()
      connection.socket.once('close', () => clearTimeout(timer))
    }
    if (wasSending) {
      const timer = setTimeout(() => connection.socket.terminate(), CLOSE_TIMEOUT_MS)
      timer.unref?.()
      connection.closePending = () => {
        clearTimeout(timer)
        close()
      }
    } else close()
  }

  private closed(connection: Connection): void {
    if (connection.pingTimer) clearTimeout(connection.pingTimer)
    if (connection.pongTimer) clearTimeout(connection.pongTimer)
    if (!connection.fenced) {
      connection.fenced = true
      this.releaseOutput(connection)
    }
    if (this.candidate === connection) this.candidate = null
    if (this.current === connection) this.current = null
    if (this.running && connection.epoch === this.epoch) this.scheduleReconnect()
  }

  private scheduleReconnect(): void {
    if (!this.running || this.retryTimer || this.current || this.candidate) return
    const base = RETRY_DELAYS_MS[Math.min(this.retryIndex, RETRY_DELAYS_MS.length - 1)]
    this.retryIndex++
    const delay = base === 0 ? 0 : Math.round(base * (0.8 + 0.4 * this.random()))
    this.retryTimer = setTimeout(() => {
      this.retryTimer = null
      this.connect()
    }, delay)
    this.retryTimer.unref?.()
  }

  private waitForOpen(signal: AbortSignal): Promise<void> {
    return new Promise((resolve, reject) => {
      const waiter = {
        afterEpoch: this.epoch,
        signal,
        resolve: () => {
          signal.removeEventListener('abort', abort)
          resolve()
        },
        reject: (error: unknown) => {
          signal.removeEventListener('abort', abort)
          reject(error)
        },
      }
      const abort = () => {
        this.openWaiters.delete(waiter)
        waiter.reject(signal.reason)
      }
      signal.addEventListener('abort', abort, { once: true })
      this.openWaiters.add(waiter)
    })
  }

  private async shutdownConnection(reason: unknown = new Error('Runner control client stopped')): Promise<void> {
    ++this.lifecycleGeneration
    this.running = false
    if (this.retryTimer) clearTimeout(this.retryTimer)
    this.retryTimer = null
    const error = reason ?? new Error('Runner control client stopped')
    this.startupReject?.(error)
    this.startupResolve = null
    this.startupReject = null
    for (const waiter of this.openWaiters) waiter.reject(error)
    this.openWaiters.clear()
    const connection = this.current ?? this.candidate
    if (!connection) return
    await new Promise<void>((resolve) => {
      connection.socket.once('close', resolve)
      this.fence(connection, 1000, 'Shutdown')
      const timer = setTimeout(resolve, CLOSE_TIMEOUT_MS)
      timer.unref?.()
    })
  }

  private ownsLifecycle(generation: number): boolean {
    return this.running && this.lifecycleGeneration === generation
  }

  private complete(item: OutputItem): void {
    const complete = item.complete
    item.complete = undefined
    complete?.()
  }

  private releaseOutput(connection: Connection): void {
    if (connection.sending) this.complete(connection.sending)
    for (const response of connection.responses) this.complete(response)
    connection.responses.length = 0
  }
}

export function buildControlUrl(
  serverUrl: string,
  runnerId: string,
  buildGitHash: string | null,
  buildInfo: BuildInfo | null,
): string {
  const url = new URL(serverUrl)
  url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:'
  url.pathname = `${url.pathname.replace(/\/$/, '')}/api/runner/${encodeURIComponent(runnerId)}/control`
  url.search = ''
  if (buildGitHash) url.searchParams.set('buildGitHash', buildGitHash)
  if (buildInfo?.component) url.searchParams.set('component', buildInfo.component)
  if (buildInfo?.version) url.searchParams.set('version', buildInfo.version)
  if (buildInfo?.sourceRevision ?? buildInfo?.gitHash)
    url.searchParams.set('sourceRevision', buildInfo.sourceRevision ?? buildInfo.gitHash!)
  if (buildInfo?.treeHash) url.searchParams.set('treeHash', buildInfo.treeHash)
  if (buildInfo?.artifactDigest) url.searchParams.set('artifactDigest', buildInfo.artifactDigest)
  if (buildInfo?.releaseId) url.searchParams.set('releaseId', buildInfo.releaseId)
  if (buildInfo?.generation) url.searchParams.set('generation', String(buildInfo.generation))
  return url.toString()
}
