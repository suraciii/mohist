import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import * as signalR from "@microsoft/signalr"
import { RunnerSignalRClient, type CancelAgentSessionPayload, setRunnerSignalRExistsCheckerForTest, setRunnerSignalRGitRunnerForTest } from "../src/server/runner-signalr.js"
import type { SessionTarget } from "../src/server/session-target.js"
import type { AgentSessionRuntimeEventOutbox } from "../src/server/runtime-event-outbox.js"
import type { CancelOperationJournalEntry, CancelOperationJournalStore } from "../src/runtime/cancel-operation-journal.js"
import type {
  OpenCodeRuntime,
  RuntimeCancelRequest,
  RuntimeCancelResult,
  RuntimeResult,
} from "../src/runtime/opencode/index.js"
import { makeFakePiRuntime, type FakePiRuntimeHandles } from "./support/pi-runtime-fixture.js"


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
  outbox?: AgentSessionRuntimeEventOutbox | null
  openCodeRuntime?: OpenCodeRuntime | (() => OpenCodeRuntime | null) | null
  piRuntime?: unknown
  cancelOperationJournal?: CancelOperationJournalStore | null
}) {
  builders.length = 0
  const resolver = opts.resolver === undefined ? null : opts.resolver
  const openCodeRuntime = opts.openCodeRuntime === undefined ? makeFakeRuntime().runtime : opts.openCodeRuntime
  const client = new RunnerSignalRClient(
    "http://localhost:3456",
    "runner-1",
    "/tmp/mohist/projects",
    null,
    {
      followupTargetResolver: resolver as never,
      agentSessionRuntimeEventOutbox: opts.outbox ?? null,
      openCodeRuntime: openCodeRuntime as never,
      ...(opts.piRuntime !== undefined ? { piRuntime: opts.piRuntime as never } : {}),
      ...(opts.cancelOperationJournal !== undefined ? { cancelOperationJournal: opts.cancelOperationJournal } : {}),
    },
  )
  return client
}

class MemoryCancelOperationJournal implements CancelOperationJournalStore {
  private readonly entries = new Map<string, CancelOperationJournalEntry>()

  async load(): Promise<void> {}
  async get(sessionId: string, operationId: string): Promise<CancelOperationJournalEntry | null> {
    return this.entries.get(`${sessionId}:${operationId}`) ?? null
  }
  async start(sessionId: string, payload: CancelAgentSessionPayload): Promise<CancelOperationJournalEntry> {
    const key = `${sessionId}:${payload.operationId}`
    const existing = this.entries.get(key)
    if (existing) return existing
    const entry: CancelOperationJournalEntry = { request: structuredClone(payload), state: "started" }
    this.entries.set(key, entry)
    return entry
  }
  async complete(sessionId: string, payload: CancelAgentSessionPayload, reply: { state: string; interruptUnconfirmed?: boolean }): Promise<void> {
    this.entries.set(`${sessionId}:${payload.operationId}`, { request: structuredClone(payload), state: "completed", reply })
  }
}

function readyOutbox(): AgentSessionRuntimeEventOutbox {
  return {
    ready: () => true,
    load: async () => {},
    recover: async () => {},
    enqueueBeforeExecution: async () => {},
    enqueueProducedFact: async () => {},
    enqueueProducedFactBatch: async () => {},
    kick: async () => {},
    stop: async () => {},
    snapshot: () => [],
  }
}

function emitCancel(builder: CapturedBuilder, payload: CancelAgentSessionPayload | null | undefined): Promise<unknown> {
  const handler = builder.handlers.get("CancelAgentSession")
  if (!handler) throw new Error("CancelAgentSession handler was not registered")
  return Promise.resolve(handler(payload))
}

describe("RunnerSignalRClient CancelAgentSession handler", () => {
  function genericCancelPayload(sessionId: string): CancelAgentSessionPayload {
    return {
      target: {
        kind: "generic",
        projectId: "proj-1",
        sessionId,
        binding: { runtime: "opencode", runtimeSessionId: "runtime-1", runnerId: "runner-1", workDir: "/work/project" },
      },
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
      return { runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }
    })

    buildClient({ resolver, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("gen-session-1"))) as { state: string }

    expect(reply).toEqual({ state: "stopped" })
    expect(runtime.cancelCalls).toHaveLength(1)
    expect(runtime.cancelCalls[0]).toEqual({
      target: { runtime: "opencode", runtimeSessionId: "runtime-1", workDir: "/work/project" },
    })
  })

  it("ConfirmedCancel_ReplayedOperationDoesNotAbortTheSessionTwice", async () => {
    const runtime = makeFakeRuntime()
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({
      resolver,
      outbox: readyOutbox(),
      openCodeRuntime: runtime.runtime,
      cancelOperationJournal: new MemoryCancelOperationJournal(),
    })
    const builder = lastBuilder()
    const payload: CancelAgentSessionPayload = {
      ...genericCancelPayload("gen-session-1"),
      sessionId: "gen-session-1",
      turnId: "turn-1",
      operationId: "stop-1",
    }

    await expect(emitCancel(builder, payload)).resolves.toEqual({ state: "stopped" })
    await expect(emitCancel(builder, payload)).resolves.toEqual({ state: "stopped" })
    expect(runtime.cancelCalls).toHaveLength(1)
  })

  it("UnknownSession_ResolverReturnsNull_RepliesNotCancellableAndDoesNotCallCancel", async () => {
    const runtime = makeFakeRuntime()
    const resolver = vi.fn(() => null)

    buildClient({ resolver, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("unknown"))) as { state: string }

    expect(reply).toEqual({ state: "not-cancellable" })
    expect(runtime.cancelCalls).toHaveLength(0)
  })

  it("CancelRemainsAvailable_WhenOutboxUnhealthy", async () => {
    const runtime = makeFakeRuntime()
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    const outbox: AgentSessionRuntimeEventOutbox = {
      ready: () => false,
      load: async () => {},
      recover: async () => {},
      enqueueBeforeExecution: async () => {},
      enqueueProducedFact: async () => {},
      enqueueProducedFactBatch: async () => {},
      kick: async () => {},
      stop: async () => {},
      snapshot() { return [] },
    }

    buildClient({ resolver, outbox, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("gen-session-1"))) as { state: string }

    expect(reply).toEqual({ state: "stopped" })
    expect(runtime.cancelCalls).toHaveLength(1)
  })

  it("NoResolverRegistered_RepliesNotCancellableAndDoesNotCallCancel", async () => {
    const runtime = makeFakeRuntime()

    buildClient({ resolver: null, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("gen-session-1"))) as { state: string }

    expect(reply).toEqual({ state: "not-cancellable" })
    expect(runtime.cancelCalls).toHaveLength(0)
  })

  it("NoRuntimeRegistered_RepliesNotCancellable", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))

    buildClient({ resolver, openCodeRuntime: null })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("gen-session-1"))) as { state: string }

    expect(reply).toEqual({ state: "not-cancellable" })
  })

  it("RuntimeReadyIsFalse_RepliesUnavailable", async () => {
    const runtime = makeFakeRuntime()
    runtime.setReady(false)
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))

    buildClient({ resolver, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("gen-session-1"))) as { state: string }

    expect(reply).toEqual({ state: "unavailable" })
    expect(runtime.cancelCalls).toHaveLength(0)
  })

  it("RuntimeCancelRejects_LeavesStopRequested", async () => {
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
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)

    buildClient({ resolver, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("gen-session-1"))) as { state: string }

    expect(reply).toEqual({ state: "stop-requested" })
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
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))

    buildClient({ resolver, openCodeRuntime: runtime.runtime })
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
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))

    buildClient({ resolver, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("gen-session-1"))) as { state: string }

    expect(reply).toEqual({ state: "unavailable" })
  })

  it("ResolverThrows_RepliesNotCancellableAndLogs", async () => {
    const runtime = makeFakeRuntime()
    const resolver = vi.fn(() => { throw new Error("resolver boom") })
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)

    buildClient({ resolver, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("gen-session-1"))) as { state: string }

    expect(reply).toEqual({ state: "not-cancellable" })
    expect(runtime.cancelCalls).toHaveLength(0)
    expect(errorSpy).toHaveBeenCalledWith("cancel target resolver threw:", expect.any(Error))
    errorSpy.mockRestore()
  })

  it("NullOrMissingPayload_RepliesNotCancellable", async () => {
    const runtime = makeFakeRuntime()
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))

    buildClient({ resolver, openCodeRuntime: runtime.runtime })
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
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))

    buildClient({ resolver, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, {
      target: {
        kind: "workflow",
        projectId: "proj-1",
        workflowRunId: "wr-1",
        sessionName: "work-1",
        binding: { runtime: "opencode", runtimeSessionId: "runtime-1", runnerId: "runner-1", workDir: "/work/project" },
      },
    })) as { state: string }

    expect(reply).toEqual({ state: "stopped" })
    expect(runtime.cancelCalls).toHaveLength(1)
    expect(runtime.cancelCalls[0].target.runtimeSessionId).toBe("runtime-1")
  })

  it("GenericTargetWithoutSessionId_RepliesNotCancellable", async () => {
    const runtime = makeFakeRuntime()
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))

    buildClient({ resolver, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, {
      target: { kind: "generic", projectId: "proj-1" },
    })) as { state: string }

    expect(reply).toEqual({ state: "not-cancellable" })
    expect(runtime.cancelCalls).toHaveLength(0)
  })

  it("Cancel_ResolvesRuntimeViaInvocationTimeAccessor", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    const initialRuntime: OpenCodeRuntime | null = null
    const replacement = makeFakeRuntime().runtime

    const accessor = () => replacement
    // First verify the accessor returns the initial null
    expect(accessor()).toBe(replacement)

    buildClient({ resolver, openCodeRuntime: accessor as never })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("gen-session-1"))) as { state: string }
    expect(reply).toEqual({ state: "stopped" })
    expect(initialRuntime).toBeNull()
  })
})

describe("RunnerSignalRClient CancelAgentSession routes by persisted binding runtime", () => {
  let opencode: ReturnType<typeof makeFakeRuntime>
  let pi: FakePiRuntimeHandles

  function piCancelPayload(sessionId: string): CancelAgentSessionPayload {
    return {
      target: {
        kind: "generic",
        projectId: "proj-1",
        sessionId,
        binding: { runtime: "pi", runtimeSessionId: "/virtual/sessions/one.jsonl", runnerId: "runner-1", workDir: "/workspace" },
      },
    }
  }

  function opencodeCancelPayload(sessionId: string): CancelAgentSessionPayload {
    return {
      target: {
        kind: "generic",
        projectId: "proj-1",
        sessionId,
        binding: { runtime: "opencode", runtimeSessionId: "runtime-1", runnerId: "runner-1", workDir: "/work/project" },
      },
    }
  }

  beforeEach(() => {
    builders.length = 0
    opencode = makeFakeRuntime()
    pi = makeFakePiRuntime()
  })

  afterEach(() => {
    vi.restoreAllMocks()
    builders.length = 0
    setRunnerSignalRGitRunnerForTest(null)
    setRunnerSignalRExistsCheckerForTest(null)
  })

  it("PiBinding_CancelConfirmed_RepliesCancelled_WithoutInterruptUnconfirmed", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "/virtual/sessions/one.jsonl", workDir: "/workspace", projectId: "proj-1" }))
    buildClient({ resolver, openCodeRuntime: opencode.runtime, piRuntime: pi.runtime })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, piCancelPayload("gen-session-1"))) as { state: string; interruptUnconfirmed?: boolean }

    expect(reply.state).toBe("stopped")
    expect(reply.interruptUnconfirmed).toBeUndefined()
    expect(pi.cancelCalls).toHaveLength(1)
    expect(pi.cancelCalls[0].target.runtime).toBe("pi")
    expect(opencode.cancelCalls).toHaveLength(0)
  })

  it("PiBinding_CancelStopUnconfirmed_RepliesCancelledWithInterruptUnconfirmedTrue", async () => {
    pi.setCancelResult({
      ok: true,
      value: { runtimeSessionId: "/virtual/sessions/one.jsonl", workDir: "/workspace", cancelled: true, stopConfirmed: false },
      diagnostics: [{ severity: "error", code: "abort-unconfirmed", message: "still streaming" }],
    })
    const resolver = vi.fn(() => ({ runtimeSessionId: "/virtual/sessions/one.jsonl", workDir: "/workspace", projectId: "proj-1" }))
    buildClient({ resolver, openCodeRuntime: opencode.runtime, piRuntime: pi.runtime })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, piCancelPayload("gen-session-1"))) as { state: string; interruptUnconfirmed?: boolean }

    expect(reply).toEqual({ state: "unknown", interruptUnconfirmed: true })
    expect(pi.cancelCalls).toHaveLength(1)
  })

  it("PiBinding_CancelRequestWithoutConfirmation_ReturnsStopRequested", async () => {
    pi.setCancelResult({
      ok: true,
      value: { runtimeSessionId: "/virtual/sessions/one.jsonl", workDir: "/workspace", cancelled: true } as never,
      diagnostics: [],
    })
    const resolver = vi.fn(() => ({ runtimeSessionId: "/virtual/sessions/one.jsonl", workDir: "/workspace", projectId: "proj-1" }))
    buildClient({ resolver, openCodeRuntime: opencode.runtime, piRuntime: pi.runtime })
    const builder = lastBuilder()

    const reply = await emitCancel(builder, piCancelPayload("gen-session-1"))

    expect(reply).toEqual({ state: "stop-requested" })
  })

  it("OpenCodeBinding_CancelRepliesCancelled_WithoutInterruptUnconfirmed", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({ resolver, openCodeRuntime: opencode.runtime, piRuntime: pi.runtime })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, opencodeCancelPayload("gen-session-1"))) as { state: string; interruptUnconfirmed?: boolean }

    expect(reply).toEqual({ state: "stopped" })
    expect(opencode.cancelCalls).toHaveLength(1)
    expect(pi.cancelCalls).toHaveLength(0)
  })

  it("UnknownBinding_RepliesNotCancellable_AndDoesNotCallAnyRuntime", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-x", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({ resolver, openCodeRuntime: opencode.runtime, piRuntime: pi.runtime })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, {
      target: {
        kind: "generic",
        projectId: "proj-1",
        sessionId: "gen-session-1",
        binding: { runtime: "acp", runtimeSessionId: "runtime-x", runnerId: "runner-1", workDir: "/work/project" },
      },
    })) as { state: string; interruptUnconfirmed?: boolean }

    expect(reply).toEqual({ state: "not-cancellable" })
    expect(opencode.cancelCalls).toHaveLength(0)
    expect(pi.cancelCalls).toHaveLength(0)
  })
})
