import { vi } from 'vitest'

/**
 * @microsoft/signalr 的测试替身，经 vite.config.ts 的 test.alias 全局生效——
 * 所有测试文件（含被测 src）看到同一个模块实现，不产生 per-file 注册表分叉，
 * 与 isolate:false 终局兼容（openspec/changes/web-test-boundary-mocks plan §波3）。
 *
 * SignalR 是出站实时连接边界：产品契约是"以什么 URL/hub 建立连接、订阅哪些
 * 事件、invoke 了哪些方法、连接状态如何流转"。由记录 + 控制端口断言表达；
 * 真实 WebSocket 传输不属于被测行为。
 *
 * 用法：渲染使用 useEventsConnection 的组件后，从 fakeConnections 取最近一条，
 * 再通过 conn.handlers.get('OnEvent')?.(...) 模拟服务端事件，或 conn.emit('reconnecting')
 * 触发重连回调。resetSignalrFake() 由 setup.ts 全局 afterEach 复位。
 */
type Listener = (...args: unknown[]) => void

export interface FakeConnection {
  state: number
  on: (event: string, handler: Listener) => void
  onreconnecting: (handler?: Listener) => Listener | null | void
  onreconnected: (handler?: Listener) => Listener | null | void
  onclose: (handler?: Listener) => Listener | null | void
  start: () => Promise<void>
  completeStart: () => void
  stop: () => Promise<void>
  invoke: (...args: unknown[]) => Promise<unknown>
  waitForStart: () => Promise<void>
  waitForInvoke: (method: string, count?: number) => Promise<void>
  emit: (kind: 'reconnecting' | 'reconnected' | 'close') => void
  handlers: Map<string, Listener>
  invokes: Array<{ method: string; args: unknown[] }>
}

export const fakeConnections: FakeConnection[] = []
export const recordedInvokes: Array<{ method: string; args: unknown[] }> = []
let rejectNextInvokeError: Error | null = null
let deferNextStart = false
const connectionWaiters: Array<{
  count: number
  resolve: (connection: FakeConnection) => void
  reject: (error: Error) => void
}> = []

function resolveConnectionWaiters() {
  for (let index = connectionWaiters.length - 1; index >= 0; index -= 1) {
    const waiter = connectionWaiters[index]
    const connection = fakeConnections[waiter.count - 1]
    if (connection) {
      connectionWaiters.splice(index, 1)
      waiter.resolve(connection)
    }
  }
}

export function waitForFakeConnection(count = 1): Promise<FakeConnection> {
  const connection = fakeConnections[count - 1]
  if (connection) return Promise.resolve(connection)
  return new Promise<FakeConnection>((resolve, reject) => {
    connectionWaiters.push({ count, resolve, reject })
  })
}

export function rejectNextInvoke(error: Error) {
  rejectNextInvokeError = error
}

export function deferNextFakeConnectionStart() {
  deferNextStart = true
}

export function makeFakeConnection(): FakeConnection {
  let onReconnectingHandler: Listener | null = null
  let onReconnectedHandler: Listener | null = null
  let onCloseHandler: Listener | null = null
  const handlers = new Map<string, Listener>()
  const invokes: Array<{ method: string; args: unknown[] }> = []
  let resolveStart!: () => void
  const started = new Promise<void>((resolve) => {
    resolveStart = resolve
  })
  let completeDeferredStart!: () => void
  const deferredStart = new Promise<void>((resolve) => {
    completeDeferredStart = resolve
  })
  const startIsDeferred = deferNextStart
  deferNextStart = false
  const invokeWaiters: Array<{ method: string; count: number; resolve: () => void }> = []

  function resolveInvokeWaiters() {
    for (let index = invokeWaiters.length - 1; index >= 0; index -= 1) {
      const waiter = invokeWaiters[index]
      if (invokes.filter((call) => call.method === waiter.method).length >= waiter.count) {
        invokeWaiters.splice(index, 1)
        waiter.resolve()
      }
    }
  }

  const conn: FakeConnection = {
    state: 0,
    on: vi.fn((event: string, handler: Listener) => {
      handlers.set(event, handler)
    }),
    onreconnecting(handler) {
      if (handler === undefined) return onReconnectingHandler
      onReconnectingHandler = handler
    },
    onreconnected(handler) {
      if (handler === undefined) return onReconnectedHandler
      onReconnectedHandler = handler
    },
    onclose(handler) {
      if (handler === undefined) return onCloseHandler
      onCloseHandler = handler
    },
    start: vi.fn(async () => {
      if (startIsDeferred) await deferredStart
      conn.state = 1
      resolveStart()
    }),
    completeStart() {
      completeDeferredStart()
    },
    stop: vi.fn(async () => {
      conn.state = 0
    }),
    invoke: vi.fn(async (...callArgs: unknown[]) => {
      const [method, ...args] = callArgs
      const m = String(method)
      invokes.push({ method: m, args })
      recordedInvokes.push({ method: m, args })
      resolveInvokeWaiters()
      if (rejectNextInvokeError) {
        const err = rejectNextInvokeError
        rejectNextInvokeError = null
        throw err
      }
      return undefined
    }),
    waitForStart() {
      return started
    },
    waitForInvoke(method, count = 1) {
      if (invokes.filter((call) => call.method === method).length >= count) return Promise.resolve()
      return new Promise<void>((resolve) => {
        invokeWaiters.push({ method, count, resolve })
      })
    },
    emit(kind) {
      if (kind === 'reconnecting') {
        onReconnectingHandler?.()
      }
      if (kind === 'reconnected') {
        onReconnectedHandler?.()
      }
      if (kind === 'close') {
        onCloseHandler?.()
      }
    },
    handlers,
    invokes,
  }
  fakeConnections.push(conn)
  resolveConnectionWaiters()
  return conn
}

export interface FakeBuilderChain {
  withUrl: ReturnType<typeof vi.fn>
  withAutomaticReconnect: ReturnType<typeof vi.fn>
  configureLogging: ReturnType<typeof vi.fn>
  build: ReturnType<typeof vi.fn>
}

export const lastBuilderChain: FakeBuilderChain = {
  withUrl: vi.fn(() => lastBuilderChain),
  withAutomaticReconnect: vi.fn(() => lastBuilderChain),
  configureLogging: vi.fn(() => lastBuilderChain),
  build: vi.fn((): FakeConnection => makeFakeConnection()),
}

export const HubConnectionBuilder = vi.fn(function HubConnectionBuilder() {
  return lastBuilderChain
})

export const HubConnectionState = {
  Connected: 'Connected',
  Reconnecting: 'Reconnecting',
  Connecting: 'Connecting',
  Disconnected: 'Disconnected',
  Disconnecting: 'Disconnecting',
} as const

export const LogLevel = {
  Trace: 'Trace',
  Debug: 'Debug',
  Information: 'Information',
  Warning: 'Warning',
  Error: 'Error',
  Critical: 'Critical',
  None: 'None',
} as const

export function resetSignalrFake() {
  const resetError = new Error('SignalR fake reset before the requested connection was created')
  for (const waiter of connectionWaiters.splice(0)) waiter.reject(resetError)
  fakeConnections.length = 0
  recordedInvokes.length = 0
  rejectNextInvokeError = null
  deferNextStart = false
  lastBuilderChain.withUrl.mockClear()
  lastBuilderChain.withAutomaticReconnect.mockClear()
  lastBuilderChain.configureLogging.mockClear()
  lastBuilderChain.build.mockClear()
}
