import { afterEach, describe, expect, it, vi } from "vitest"
import * as signalR from "@microsoft/signalr"
import { RunnerSignalRClient, type CancelAgentSessionPayload, setRunnerSignalRExistsCheckerForTest, setRunnerSignalRGitRunnerForTest } from "../src/server/runner-signalr.js"
import type { SessionTarget } from "../src/runtime/acp-connection.js"
import { FOLLOWUP_TARGET_UNAVAILABLE } from "../src/server/session-target.js"


interface CapturedBuilder {
  handlers: Map<string, (...args: unknown[]) => unknown>
  connection: FakeConnection
}

const builders: CapturedBuilder[] = []
let nextConnectionId = 0

afterEach(() => {
  vi.restoreAllMocks()
  builders.length = 0
  nextConnectionId = 0
  setRunnerSignalRGitRunnerForTest(null)
  setRunnerSignalRExistsCheckerForTest(null)
})

interface FakeConnection {
  state: signalR.HubConnectionState
  connectionId: string | null
  start: ReturnType<typeof vi.fn>
  stop: ReturnType<typeof vi.fn>
  invoke: ReturnType<typeof vi.fn>
  on: ReturnType<typeof vi.fn>
  onreconnected: ((cb: (id?: string) => void) => void) | undefined
  _reconnectHandler?: (connectionId?: string) => void
}

function makeFakeConnection(): FakeConnection {
  const conn: FakeConnection = {
    state: signalR.HubConnectionState.Disconnected,
    connectionId: null,
    start: vi.fn(),
    stop: vi.fn(),
    invoke: vi.fn(),
    on: vi.fn(),
    onreconnected: undefined,
  }
  conn.start.mockImplementation(async () => {
    conn.state = signalR.HubConnectionState.Connected
    conn.connectionId = `conn-${++nextConnectionId}`
  })
  conn.stop.mockImplementation(async () => {
    conn.state = signalR.HubConnectionState.Disconnected
    conn.connectionId = null
  })
  conn.onreconnected = ((cb: (id?: string) => void) => {
    conn._reconnectHandler = cb
  }) as FakeConnection["onreconnected"]
  return conn
}

vi.mock("@microsoft/signalr", () => {
  return {
    HubConnectionBuilder: class {
      private _handlers: Map<string, (...args: unknown[]) => unknown> = new Map()
      private _connection: FakeConnection = makeFakeConnection()
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
        return this._connection as unknown as signalR.HubConnection
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

function lastBuilder(): CapturedBuilder {
  const builder = builders.at(-1)
  if (!builder) throw new Error("no captured builder; construct a RunnerSignalRClient first")
  return builder
}

type AnyFn = (...args: any[]) => any

interface MockServerConnection {
  workflowAgentSessionRuntimeEvents: AnyFn
  agentSessionRuntimeEvents?: AnyFn
}

interface MockConnection {
  prompt: AnyFn
  cancel?: AnyFn
}

function buildClient(opts: {
  resolver?: AnyFn | null
  serverConnection?: MockServerConnection | null
}) {
  builders.length = 0
  const defaultServerConnection: MockServerConnection = {
    workflowAgentSessionRuntimeEvents: vi.fn(async () => undefined),
    agentSessionRuntimeEvents: vi.fn(async () => undefined),
  }
  const serverConnection = opts.serverConnection === undefined ? defaultServerConnection : opts.serverConnection
  const resolver = opts.resolver === undefined ? null : opts.resolver
  const client = new RunnerSignalRClient(
    "http://localhost:3456",
    "runner-1",
    "/tmp/mohist/projects",
    null,
    {
      serverConnection: serverConnection as never,
      followupTargetResolver: resolver as never,
    },
  )
  return client
}

function emitCancel(builder: CapturedBuilder, payload: CancelAgentSessionPayload | null | undefined): Promise<unknown> {
  const handler = builder.handlers.get("CancelAgentSession")
  if (!handler) throw new Error("CancelAgentSession handler was not registered")
  return Promise.resolve(handler(payload))
}

describe("RunnerSignalRClient CancelAgentSession handler", () => {
  function genericCancelPayload(sessionId: string): CancelAgentSessionPayload {
    return {
      target: { kind: "generic", projectId: "proj-1", sessionId },
    }
  }

  it("CancellableSession_ResolverHits_ConnectionCancelInvokedAndRepliesCancelled", async () => {
    const cancel = vi.fn(async () => undefined)
    const connection: MockConnection = { prompt: vi.fn(), cancel }
    const resolver = vi.fn((target: SessionTarget) => {
      expect(target.kind).toBe("generic")
      if (target.kind === "generic") {
        expect(target.sessionId).toBe("gen-session-1")
        expect(target.projectId).toBe("proj-1")
      }
      return { connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }
    })

    buildClient({ resolver, serverConnection: null })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("gen-session-1"))) as { state: string }

    expect(reply).toEqual({ state: "cancelled" })
    expect(cancel).toHaveBeenCalledTimes(1)
    expect(cancel).toHaveBeenCalledWith({ sessionId: "acp-1" })
  })

  it("UnknownSession_ResolverReturnsNull_RepliesNotCancellableAndDoesNotCallCancel", async () => {
    const cancel = vi.fn(async () => undefined)
    const connection: MockConnection = { prompt: vi.fn(), cancel }
    const resolver = vi.fn(() => null)

    buildClient({ resolver, serverConnection: null })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("unknown"))) as { state: string }

    expect(reply).toEqual({ state: "not-cancellable" })
    expect(cancel).not.toHaveBeenCalled()
  })

  it("RuntimeInitializing_ResolverReturnsUnavailable_RepliesUnavailable", async () => {
    buildClient({ resolver: () => FOLLOWUP_TARGET_UNAVAILABLE, serverConnection: null })

    await expect(emitCancel(lastBuilder(), genericCancelPayload("gen-session-1")))
      .resolves.toEqual({ state: "unavailable" })
  })

  it("NoResolverRegistered_RepliesNotCancellableAndDoesNotCallCancel", async () => {
    const cancel = vi.fn(async () => undefined)
    const connection: MockConnection = { prompt: vi.fn(), cancel }

    buildClient({ resolver: null, serverConnection: null })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("gen-session-1"))) as { state: string }

    expect(reply).toEqual({ state: "not-cancellable" })
    expect(cancel).not.toHaveBeenCalled()
  })

  it("NoCancelMethodOnConnection_RepliesNotCancellable", async () => {
    // Defensive: the current ACP SDK defines `cancel` on every
    // ClientSideConnection, but the handler must report honestly if a
    // future / custom connection omits the method.
    const connection: MockConnection = { prompt: vi.fn() /* no cancel */ }
    const resolver = vi.fn(() => ({ connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }))

    buildClient({ resolver, serverConnection: null })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("gen-session-1"))) as { state: string }

    expect(reply).toEqual({ state: "not-cancellable" })
  })

  it("ConnectionCancelRejects_RepliesNotCancellableAndLogs", async () => {
    const cancel = vi.fn(async () => {
      throw new Error("transport dropped")
    })
    const connection: MockConnection = { prompt: vi.fn(), cancel }
    const resolver = vi.fn(() => ({ connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }))
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)

    buildClient({ resolver, serverConnection: null })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("gen-session-1"))) as { state: string }

    expect(reply).toEqual({ state: "not-cancellable" })
    expect(cancel).toHaveBeenCalledTimes(1)
    expect(errorSpy).toHaveBeenCalledWith(
      "cancel connection.cancel rejected:",
      expect.stringContaining("transport dropped"),
    )
    errorSpy.mockRestore()
  })

  it("ResolverThrows_RepliesNotCancellableAndLogs", async () => {
    const cancel = vi.fn(async () => undefined)
    const connection: MockConnection = { prompt: vi.fn(), cancel }
    const resolver = vi.fn(() => { throw new Error("resolver boom") })
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)

    buildClient({ resolver, serverConnection: null })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("gen-session-1"))) as { state: string }

    expect(reply).toEqual({ state: "not-cancellable" })
    expect(cancel).not.toHaveBeenCalled()
    expect(errorSpy).toHaveBeenCalledWith("cancel target resolver threw:", expect.any(Error))
    errorSpy.mockRestore()
  })

  it("NullOrMissingPayload_RepliesNotCancellable", async () => {
    const cancel = vi.fn(async () => undefined)
    const connection: MockConnection = { prompt: vi.fn(), cancel }
    const resolver = vi.fn(() => ({ connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }))

    buildClient({ resolver, serverConnection: null })
    const builder = lastBuilder()

    const replyFromNull = (await emitCancel(builder, null)) as { state: string }
    const replyFromMissing = (await emitCancel(builder, undefined)) as { state: string }
    const replyFromNoTarget = (await emitCancel(builder, { target: undefined as unknown as never })) as { state: string }

    expect(replyFromNull).toEqual({ state: "not-cancellable" })
    expect(replyFromMissing).toEqual({ state: "not-cancellable" })
    expect(replyFromNoTarget).toEqual({ state: "not-cancellable" })
    expect(cancel).not.toHaveBeenCalled()
  })

  it("WorkflowShapedTarget_ResolvesAndCancelsTheWorkflowRuntimeSession", async () => {
    const cancel = vi.fn(async () => undefined)
    const connection: MockConnection = { prompt: vi.fn(), cancel }
    const resolver = vi.fn(() => ({ connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }))

    buildClient({ resolver, serverConnection: null })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, {
      target: { kind: "workflow", projectId: "proj-1", workflowRunId: "wr-1", sessionName: "work-1" },
    })) as { state: string }

    expect(reply).toEqual({ state: "cancelled" })
    expect(cancel).toHaveBeenCalledWith({ sessionId: "acp-1" })
  })

  it("GenericTargetWithoutSessionId_RepliesNotCancellable", async () => {
    const cancel = vi.fn(async () => undefined)
    const connection: MockConnection = { prompt: vi.fn(), cancel }
    const resolver = vi.fn(() => ({ connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }))

    buildClient({ resolver, serverConnection: null })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, {
      target: { kind: "generic", projectId: "proj-1" },
    })) as { state: string }

    expect(reply).toEqual({ state: "not-cancellable" })
    expect(cancel).not.toHaveBeenCalled()
  })
})
