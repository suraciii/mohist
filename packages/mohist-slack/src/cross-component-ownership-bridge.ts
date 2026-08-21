import { createInterface } from 'node:readline'
import { SlackAdapter } from './adapter.js'
import { FakeSocket, FakeWeb } from './_adapterTestSupport.js'
import { HttpAdapterTransport } from './transport.js'

interface BridgeMessage {
  readonly type: string
  readonly id?: number
  readonly body?: unknown
  readonly status?: number
  readonly responseBody?: string
  readonly scenario?: string
}

const socket = new FakeSocket()
const web = new FakeWeb()
const pending = new Map<number, (response: Response) => void>()
let nextRequestId = 1
let adapter: SlackAdapter | undefined
let controller: AbortController | undefined

function write(message: Record<string, unknown>): void {
  process.stdout.write(`${JSON.stringify(message)}\n`)
}

async function fetchThroughParent(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const id = nextRequestId++
  const request = new Promise<Response>((resolve) => pending.set(id, resolve))
  write({
    type: 'request',
    id,
    url: String(input),
    method: init?.method ?? 'GET',
    body: typeof init?.body === 'string' ? init.body : undefined,
  })
  return await request
}

async function start(): Promise<void> {
  const transport = new HttpAdapterTransport({
    serverUrl: 'http://localhost',
    operatorToken: 'test-operator-token-0123456789abcdef',
    operatorId: 'spec-operator',
    fetch: fetchThroughParent,
  })
  adapter = new SlackAdapter({
    adapterId: 'adapter-spec',
    transport,
    socketFactory: () => socket,
    webFactory: () => web,
    heartbeatIntervalMs: 60_000,
    deliveryPollIntervalMs: 60_000,
  })
  controller = new AbortController()
  await adapter.start(controller.signal)
  write({ type: 'ready' })
}

async function emit(body: unknown): Promise<void> {
  if (!socket) throw new Error('Slack adapter bridge is not started')
  const acknowledged = await socket.emit(body)
  write({
    type: 'emitResult',
    acknowledged,
    acknowledgementCount: socket.acknowledgementCount,
    posted: web.posted,
  })
}

async function stop(): Promise<void> {
  controller?.abort()
  await adapter?.stop()
  write({ type: 'stopped' })
}

const input = createInterface({ input: process.stdin })
input.on('line', (line) => {
  let message: BridgeMessage
  try {
    message = JSON.parse(line) as BridgeMessage
  } catch (error) {
    write({ type: 'error', message: error instanceof Error ? error.message : String(error) })
    return
  }

  if (message.type === 'response' && message.id !== undefined) {
    const resolve = pending.get(message.id)
    if (!resolve) return
    pending.delete(message.id)
    resolve(new Response(message.responseBody ?? '', { status: message.status ?? 500 }))
    return
  }
  if (message.type === 'scenario') {
    write({ type: 'scenarioApplied', scenario: message.scenario })
    return
  }
  if (message.type === 'inspect') {
    write({ type: 'snapshot-requested' })
    return
  }
  if (message.type === 'emit') {
    void emit(message.body).catch((error) =>
      write({ type: 'error', message: error instanceof Error ? error.message : String(error) }),
    )
    return
  }
  if (message.type === 'stop') {
    void stop().then(() => process.exit(0))
  }
})

void start().catch((error) => {
  write({ type: 'error', message: error instanceof Error ? error.message : String(error) })
  process.exitCode = 1
})
