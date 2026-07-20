/**
 * Shared test fixture for the runner-side Follow-up / Cancel SignalR
 * handler tests. Issue-410 T-003 keeps the runner push handlers on the
 * `RunnerSignalRClient` boundary but routes them at the
 * `OpenCodeRuntime`; the wire-level scenarios (handler registration,
 * resolver / runtime plumbing, event-channel fan-out) need a single
 * fake-injection seam so multiple spec files can exercise them
 * without re-implementing the `@microsoft/signalr` mock.
 */
import { vi } from "vitest"
import { HubConnectionState, type HubConnection } from "@microsoft/signalr"
import { RunnerSignalRClient, type ReceiveFollowupPayload } from "../../src/server/runner-signalr.js"
import type {
  OpenCodeRuntime,
  RuntimeCancelRequest,
  RuntimeCancelResult,
  RuntimeFollowupRequest,
  RuntimeFollowupResult,
  RuntimeResult,
} from "../../src/runtime/opencode/index.js"

vi.mock("@microsoft/signalr", () => {
  return {
    HubConnectionBuilder: class {
      private _handlers: Map<string, (...args: unknown[]) => unknown> = new Map()
      private _connection = makeFakeConnection()
      withUrl(_url: string) {
        builders.push({ handlers: this._handlers, connection: this._connection })
        return this
      }
      withAutomaticReconnect(_reconnectPolicy: number[]) {
        return this
      }
      build() {
        this._connection.on.mockImplementation((event: string, handler: (...args: unknown[]) => unknown) => {
          this._handlers.set(event, handler)
          return this._connection
        })
        return this._connection as unknown as HubConnection
      }
    },
    HubConnectionState: {
      Disconnected: "Disconnected",
      Connecting: "Connecting",
      Connected: "Connected",
      Disconnecting: "Disconnecting",
      Reconnecting: "Reconnecting",
    },
  }
})

type AnyFn = (...args: unknown[]) => unknown

export interface CapturedBuilder {
  handlers: Map<string, (...args: unknown[]) => unknown>
  connection: FakeConnection
}

export interface FakeConnection {
  state: HubConnectionState
  connectionId: string | null
  start: ReturnType<typeof vi.fn>
  stop: ReturnType<typeof vi.fn>
  invoke: ReturnType<typeof vi.fn>
  on: ReturnType<typeof vi.fn>
  onreconnected: ((cb: (id?: string) => void) => void) | undefined
  _reconnectHandler?: (connectionId?: string) => void
}

export interface MockServerConnection {
  workflowAgentSessionRuntimeEvents: AnyFn
  agentSessionRuntimeEvents?: AnyFn
}

export interface FakeRuntimeHandles {
  runtime: OpenCodeRuntime
  followupCalls: RuntimeFollowupRequest[]
  setFollowupResult: (result: RuntimeResult<RuntimeFollowupResult>) => void
  setReady: (ready: boolean) => void
}

const builders: CapturedBuilder[] = []
let nextConnectionId = 0

function makeFakeConnection(): FakeConnection {
  const conn: FakeConnection = {
    state: HubConnectionState.Disconnected,
    connectionId: null,
    start: vi.fn(),
    stop: vi.fn(),
    invoke: vi.fn(),
    on: vi.fn(),
    onreconnected: undefined,
  }
  conn.start.mockImplementation(async () => {
    conn.state = HubConnectionState.Connected
    conn.connectionId = `conn-${++nextConnectionId}`
  })
  conn.stop.mockImplementation(async () => {
    conn.state = HubConnectionState.Disconnected
    conn.connectionId = null
  })
  conn.onreconnected = ((cb: (id?: string) => void) => {
    conn._reconnectHandler = cb
  }) as FakeConnection["onreconnected"]
  return conn
}

export function resetBuilders(): void {
  builders.length = 0
  nextConnectionId = 0
}

export function lastBuilder(): CapturedBuilder {
  const builder = builders.at(-1)
  if (!builder) throw new Error("no captured builder; construct a RunnerSignalRClient first")
  return builder
}

export function makeFakeRuntime(): FakeRuntimeHandles {
  const followupCalls: RuntimeFollowupRequest[] = []
  let ready = true
  let nextResult: RuntimeResult<RuntimeFollowupResult> = {
    ok: true,
    value: {
      facts: { runtimeSessionId: "ses_runtime", workDir: "/work/project" },
      diagnostics: [],
    },
    diagnostics: [],
  }
  const runtime: Partial<OpenCodeRuntime> = {
    ready: () => ready,
    diagnostic: () => null,
    async followup(request: RuntimeFollowupRequest): Promise<RuntimeResult<RuntimeFollowupResult>> {
      followupCalls.push(request)
      return nextResult
    },
    async cancel(_request: RuntimeCancelRequest): Promise<RuntimeResult<RuntimeCancelResult>> {
      return {
        ok: true,
        value: { facts: { runtimeSessionId: "ses_runtime", workDir: "/work/project", cancelled: true }, diagnostics: [] },
        diagnostics: [],
      }
    },
  }
  return {
    runtime: runtime as OpenCodeRuntime,
    followupCalls,
    setFollowupResult(result) { nextResult = result },
    setReady(value) { ready = value },
  }
}

export function buildClient(opts: {
  resolver?: AnyFn | null
  serverConnection?: MockServerConnection | null
  followupFailureOutbox?: { record: AnyFn } | null
  openCodeRuntime?: OpenCodeRuntime | null
}): RunnerSignalRClient {
  builders.length = 0
  const defaultServerConnection: MockServerConnection = {
    workflowAgentSessionRuntimeEvents: vi.fn(async () => undefined),
    agentSessionRuntimeEvents: vi.fn(async () => undefined),
  }
  const serverConnection = opts.serverConnection === undefined ? defaultServerConnection : opts.serverConnection
  const resolver = opts.resolver === undefined ? null : opts.resolver
  const openCodeRuntime = opts.openCodeRuntime === undefined ? makeFakeRuntime().runtime : opts.openCodeRuntime
  return new RunnerSignalRClient(
    "http://localhost:3456",
    "runner-1",
    "/tmp/mohist/projects",
    null,
    {
      serverConnection: serverConnection as never,
      followupTargetResolver: resolver as never,
      followupFailureOutbox: opts.followupFailureOutbox as never,
      openCodeRuntime: openCodeRuntime as never,
    },
  )
}

export function emitFollowup(builder: CapturedBuilder, payload: ReceiveFollowupPayload | null | undefined): void {
  const handler = builder.handlers.get("ReceiveFollowup")
  if (!handler) throw new Error("ReceiveFollowup handler was not registered")
  handler(payload)
}

export async function invokeFollowup(builder: CapturedBuilder, payload: ReceiveFollowupPayload | null | undefined): Promise<unknown> {
  const handler = builder.handlers.get("ReceiveFollowup")
  if (!handler) throw new Error("ReceiveFollowup handler was not registered")
  return await handler(payload)
}

export async function flush(): Promise<void> {
  await new Promise((resolve) => setImmediate(resolve))
}

export function genericPayload(text: string): ReceiveFollowupPayload {
  return {
    target: { kind: "generic", projectId: "proj-1", sessionId: "gen-session-1" },
    text,
  }
}
