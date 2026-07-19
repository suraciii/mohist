import { afterEach, describe, expect, it, vi } from "vitest"
import * as signalR from "@microsoft/signalr"
import { RunnerSignalRClient, type CancelAgentSessionPayload, setRunnerSignalRExistsCheckerForTest, setRunnerSignalRGitRunnerForTest } from "../src/server/runner-signalr.js"
import type { SessionTarget } from "../src/server/session-target.js"
import { FOLLOWUP_TARGET_UNAVAILABLE } from "../src/server/session-target.js"
import type {
  OpenCodeRuntime,
  RuntimeCancelRequest,
  RuntimeCancelResult,
  RuntimeResult,
} from "../src/runtime/opencode/index.js"


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

interface FakeRuntimeHandles {
  runtime: OpenCodeRuntime
  cancelCalls: RuntimeCancelRequest[]
  setCancelResult: (result: RuntimeResult<RuntimeCancelResult>) => void
  setReady: (ready: boolean) => void
}

function makeFakeRuntime(): FakeRuntimeHandles {
  const cancelCalls: RuntimeCancelRequest[] = []
  let ready = true
  let nextResult: RuntimeResult<RuntimeCancelResult> = {
    ok: true,
    value: {
      facts: { runtimeSessionId: "ses_runtime", workDir: "/work/project", cancelled: true },
      diagnostics: [],
    },
    diagnostics: [],
  }
  const runtime: Partial<OpenCodeRuntime> = {
    ready: () => ready,
    diagnostic: () => null,
    async cancel(request: RuntimeCancelRequest): Promise<RuntimeResult<RuntimeCancelResult>> {
      cancelCalls.push(request)
      return nextResult
    },
  }
  return {
    runtime: runtime as OpenCodeRuntime,
    cancelCalls,
    setCancelResult(result) { nextResult = result },
    setReady(value) { ready = value },
  }
}

function buildClient(opts: {
  resolver?: AnyFn | null
  serverConnection?: MockServerConnection | null
  openCodeRuntime?: OpenCodeRuntime | null
}) {
  builders.length = 0
  const defaultServerConnection: MockServerConnection = {
    workflowAgentSessionRuntimeEvents: vi.fn(async () => undefined),
    agentSessionRuntimeEvents: vi.fn(async () => undefined),
  }
  const serverConnection = opts.serverConnection === undefined ? defaultServerConnection : opts.serverConnection
  const resolver = opts.resolver === undefined ? null : opts.resolver
  const openCodeRuntime = opts.openCodeRuntime === undefined ? makeFakeRuntime().runtime : opts.openCodeRuntime
  const client = new RunnerSignalRClient(
    "http://localhost:3456",
    "runner-1",
    "/tmp/mohist/projects",
    null,
    {
      serverConnection: serverConnection as never,
      followupTargetResolver: resolver as never,
      openCodeRuntime: openCodeRuntime as never,
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

  it("CancellableSession_ResolverHits_RuntimeCancelInvokedAndRepliesCancelled", async () => {
    const runtime = makeFakeRuntime()
    const resolver = vi.fn((target: SessionTarget) => {
      expect(target.kind).toBe("generic")
      if (target.kind === "generic") {
        expect(target.sessionId).toBe("gen-session-1")
        expect(target.projectId).toBe("proj-1")
      }
      return { runtimeSessionId: "acp-1", workDir: "/work/project", projectId: "proj-1" }
    })

    buildClient({ resolver, serverConnection: null, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("gen-session-1"))) as { state: string }

    expect(reply).toEqual({ state: "cancelled" })
    expect(runtime.cancelCalls).toHaveLength(1)
    expect(runtime.cancelCalls[0]).toEqual({
      target: { runtime: "opencode", runtimeSessionId: "acp-1", workDir: "/work/project" },
    })
  })

  it("UnknownSession_ResolverReturnsNull_RepliesNotCancellableAndDoesNotCallCancel", async () => {
    const runtime = makeFakeRuntime()
    const resolver = vi.fn(() => null)

    buildClient({ resolver, serverConnection: null, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("unknown"))) as { state: string }

    expect(reply).toEqual({ state: "not-cancellable" })
    expect(runtime.cancelCalls).toHaveLength(0)
  })

  it("RuntimeInitializing_ResolverReturnsUnavailable_RepliesUnavailable", async () => {
    buildClient({ resolver: () => FOLLOWUP_TARGET_UNAVAILABLE, serverConnection: null, openCodeRuntime: makeFakeRuntime().runtime })

    await expect(emitCancel(lastBuilder(), genericCancelPayload("gen-session-1")))
      .resolves.toEqual({ state: "unavailable" })
  })

  it("NoResolverRegistered_RepliesNotCancellableAndDoesNotCallCancel", async () => {
    const runtime = makeFakeRuntime()

    buildClient({ resolver: null, serverConnection: null, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("gen-session-1"))) as { state: string }

    expect(reply).toEqual({ state: "not-cancellable" })
    expect(runtime.cancelCalls).toHaveLength(0)
  })

  it("NoRuntimeRegistered_RepliesNotCancellable", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "acp-1", workDir: "/work/project", projectId: "proj-1" }))

    buildClient({ resolver, serverConnection: null, openCodeRuntime: null })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("gen-session-1"))) as { state: string }

    expect(reply).toEqual({ state: "not-cancellable" })
  })

  it("RuntimeReadyIsFalse_RepliesUnavailable", async () => {
    const runtime = makeFakeRuntime()
    runtime.setReady(false)
    const resolver = vi.fn(() => ({ runtimeSessionId: "acp-1", workDir: "/work/project", projectId: "proj-1" }))

    buildClient({ resolver, serverConnection: null, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("gen-session-1"))) as { state: string }

    expect(reply).toEqual({ state: "unavailable" })
    expect(runtime.cancelCalls).toHaveLength(0)
  })

  it("RuntimeCancelRejects_RepliesNotCancellable", async () => {
    const runtime = makeFakeRuntime()
    runtime.setCancelResult({
      ok: false,
      error: {
        kind: "turn-failed",
        message: "transport dropped",
        diagnostics: [{ severity: "error", code: "turn-failed", message: "transport dropped" }],
      },
      diagnostics: [{ severity: "error", code: "turn-failed", message: "transport dropped" }],
    })
    const resolver = vi.fn(() => ({ runtimeSessionId: "acp-1", workDir: "/work/project", projectId: "proj-1" }))
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)

    buildClient({ resolver, serverConnection: null, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("gen-session-1"))) as { state: string }

    expect(reply).toEqual({ state: "not-cancellable" })
    expect(runtime.cancelCalls).toHaveLength(1)
    expect(errorSpy).toHaveBeenCalledWith("cancel runtime.cancel rejected:", expect.stringContaining("transport dropped"))
    errorSpy.mockRestore()
  })

  it("RuntimeMissingSession_RepliesNotCancellable", async () => {
    const runtime = makeFakeRuntime()
    runtime.setCancelResult({
      ok: false,
      error: {
        kind: "missing-session",
        message: "no physical session",
        diagnostics: [{ severity: "error", code: "missing-session", message: "no physical session" }],
      },
      diagnostics: [{ severity: "error", code: "missing-session", message: "no physical session" }],
    })
    const resolver = vi.fn(() => ({ runtimeSessionId: "acp-1", workDir: "/work/project", projectId: "proj-1" }))

    buildClient({ resolver, serverConnection: null, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("gen-session-1"))) as { state: string }

    expect(reply).toEqual({ state: "not-cancellable" })
  })

  it("RuntimeUnavailableRuntime_RepliesUnavailable", async () => {
    const runtime = makeFakeRuntime()
    runtime.setCancelResult({
      ok: false,
      error: {
        kind: "unavailable-runtime",
        message: "runtime down",
        diagnostics: [{ severity: "error", code: "unavailable-runtime", message: "runtime down" }],
      },
      diagnostics: [{ severity: "error", code: "unavailable-runtime", message: "runtime down" }],
    })
    const resolver = vi.fn(() => ({ runtimeSessionId: "acp-1", workDir: "/work/project", projectId: "proj-1" }))

    buildClient({ resolver, serverConnection: null, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("gen-session-1"))) as { state: string }

    expect(reply).toEqual({ state: "unavailable" })
  })

  it("ResolverThrows_RepliesNotCancellableAndLogs", async () => {
    const runtime = makeFakeRuntime()
    const resolver = vi.fn(() => { throw new Error("resolver boom") })
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)

    buildClient({ resolver, serverConnection: null, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("gen-session-1"))) as { state: string }

    expect(reply).toEqual({ state: "not-cancellable" })
    expect(runtime.cancelCalls).toHaveLength(0)
    expect(errorSpy).toHaveBeenCalledWith("cancel target resolver threw:", expect.any(Error))
    errorSpy.mockRestore()
  })

  it("NullOrMissingPayload_RepliesNotCancellable", async () => {
    const runtime = makeFakeRuntime()
    const resolver = vi.fn(() => ({ runtimeSessionId: "acp-1", workDir: "/work/project", projectId: "proj-1" }))

    buildClient({ resolver, serverConnection: null, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    const replyFromNull = (await emitCancel(builder, null)) as { state: string }
    const replyFromMissing = (await emitCancel(builder, undefined)) as { state: string }
    const replyFromNoTarget = (await emitCancel(builder, { target: undefined as unknown as never })) as { state: string }

    expect(replyFromNull).toEqual({ state: "not-cancellable" })
    expect(replyFromMissing).toEqual({ state: "not-cancellable" })
    expect(replyFromNoTarget).toEqual({ state: "not-cancellable" })
    expect(runtime.cancelCalls).toHaveLength(0)
  })

  it("WorkflowShapedTarget_ResolvesAndCancelsTheWorkflowRuntimeSession", async () => {
    const runtime = makeFakeRuntime()
    const resolver = vi.fn(() => ({ runtimeSessionId: "acp-1", workDir: "/work/project", projectId: "proj-1" }))

    buildClient({ resolver, serverConnection: null, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, {
      target: { kind: "workflow", projectId: "proj-1", workflowRunId: "wr-1", sessionName: "work-1" },
    })) as { state: string }

    expect(reply).toEqual({ state: "cancelled" })
    expect(runtime.cancelCalls).toHaveLength(1)
    expect(runtime.cancelCalls[0].target.runtimeSessionId).toBe("acp-1")
  })

  it("GenericTargetWithoutSessionId_RepliesNotCancellable", async () => {
    const runtime = makeFakeRuntime()
    const resolver = vi.fn(() => ({ runtimeSessionId: "acp-1", workDir: "/work/project", projectId: "proj-1" }))

    buildClient({ resolver, serverConnection: null, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, {
      target: { kind: "generic", projectId: "proj-1" },
    })) as { state: string }

    expect(reply).toEqual({ state: "not-cancellable" })
    expect(runtime.cancelCalls).toHaveLength(0)
  })
})