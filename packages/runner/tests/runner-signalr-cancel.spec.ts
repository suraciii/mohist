import { describe, expect, it as vitestIt, vi } from "vitest"
import * as signalR from "@microsoft/signalr"
import type { CancelAgentSessionPayload } from "../src/server/runner-signalr.js"
import type { RunnerFileSystem, RunnerResourceContext } from "../src/system/filesystem.js"
import type { SessionTarget } from "../src/server/session-target.js"
import type { AgentSessionRuntimeEventOutbox } from "../src/server/runtime-event-outbox.js"
import type { OpenCodeRuntime } from "../src/runtime/opencode/index.js"
import { makeFakePiRuntime, type FakePiRuntimeHandles } from "./support/pi-runtime-fixture.js"
import { capturedLogs } from "./support/logger-test.js"
import { MemoryFileSystem } from "./support/memory-filesystem.js"
import { currentSignalRTestState, withSignalRTestResources } from "./support/signalr-test-resources.js"
import {
  buildClient,
  emitCancel,
  genericCancelPayload,
  lastBuilder,
  makeFakeRuntime,
  MemoryCancelOperationJournal,
  readyOutbox,
} from "./support/cancel-handler-fixture.js"

type SignalRResources = {
  fileSystem: RunnerFileSystem
  signalRGitRunner?: NonNullable<RunnerResourceContext["signalRGitRunner"]>
  signalRExistsChecker?: (path: string) => boolean
}

function it(name: string, body: (resources: SignalRResources) => Promise<void> | void): void {
  vitestIt(name, async () => {
    const resources: SignalRResources = { fileSystem: new MemoryFileSystem() }
    await withSignalRTestResources(resources, async () => await body(resources))
  })
}

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
    conn.connectionId = `conn-${++currentSignalRTestState().nextConnectionId}`
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
        currentSignalRTestState().builders.push({ handlers: this._handlers, connection: this._connection })
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

describe("RunnerSignalRClient CancelAgentSession handler", () => {
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

  it("UnknownSession_ResolverReturnsNull_SettlesEndedByIdentity", async () => {
    const runtime = makeFakeRuntime()
    runtime.setResolveResult({
      ok: false,
      error: { kind: "missing-session", message: "no physical session", diagnostics: [] },
      diagnostics: [],
    })
    const resolver = vi.fn(() => null)

    buildClient({ resolver, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("unknown"))) as { state: string }

    expect(reply).toEqual({ state: "ended" })
    expect(runtime.cancelCalls).toHaveLength(0)
  })

  it("UnknownSession_ResolverReturnsNull_SettlesIdleByIdentity", async () => {
    const runtime = makeFakeRuntime()
    runtime.setResolveResult({
      ok: true,
      value: { runtimeSessionId: "runtime-1", workDir: "/work/project", activeTurn: false },
      diagnostics: [],
    })
    const resolver = vi.fn(() => null)

    buildClient({ resolver, openCodeRuntime: runtime.runtime })

    await expect(emitCancel(lastBuilder(), genericCancelPayload("idle"))).resolves.toEqual({ state: "idle" })
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

    buildClient({
      resolver,
      outbox,
      openCodeRuntime: runtime.runtime,
      cancelOperationJournal: new MemoryCancelOperationJournal(),
    })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, {
      ...genericCancelPayload("gen-session-1"),
      sessionId: "gen-session-1",
      turnId: "turn-1",
      operationId: "stop-outbox-unhealthy",
    })) as { state: string }

    expect(reply).toEqual({ state: "stopped" })
    expect(runtime.cancelCalls).toHaveLength(1)
  })

  it("NoResolverRegistered_RepliesUnavailable", async () => {
    const runtime = makeFakeRuntime()

    buildClient({ resolver: null, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("gen-session-1"))) as { state: string }

    expect(reply).toEqual({ state: "unavailable" })
    expect(runtime.cancelCalls).toHaveLength(0)
  })

  it("NoRuntimeRegistered_RepliesUnavailable", async () => {
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))

    buildClient({ resolver, openCodeRuntime: null })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("gen-session-1"))) as { state: string }

    expect(reply).toEqual({ state: "unavailable" })
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

    buildClient({ resolver, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("gen-session-1"))) as { state: string }

    expect(reply).toEqual({ state: "stop-requested" })
    expect(runtime.cancelCalls).toHaveLength(1)
    expect(capturedLogs()).toEqual(expect.arrayContaining([
      expect.objectContaining({ level: "ERROR", message: "cancel runtime.cancel rejected", fields: expect.objectContaining({ reason: expect.stringContaining("transport dropped"), session: "runtime-1" }) }),
    ]))
  })

  it("RuntimeMissingSession_SettlesByIdentity", async () => {
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
    runtime.setResolveResult({
      ok: false,
      error: { kind: "missing-session", message: "no physical session", diagnostics: [] },
      diagnostics: [],
    })
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))

    buildClient({ resolver, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("gen-session-1"))) as { state: string }

    expect(reply).toEqual({ state: "ended" })
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

  it("ResolverThrows_RepliesUnavailableAndLogs", async () => {
    const runtime = makeFakeRuntime()
    const resolver = vi.fn(() => { throw new Error("resolver boom") })

    buildClient({ resolver, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("gen-session-1"))) as { state: string }

    expect(reply).toEqual({ state: "unavailable" })
    expect(runtime.cancelCalls).toHaveLength(0)
    expect(capturedLogs()).toEqual(expect.arrayContaining([
      expect.objectContaining({ level: "ERROR", message: "cancel target resolver threw", fields: expect.objectContaining({ exception: expect.objectContaining({ message: "resolver boom" }) }) }),
    ]))
  })

  it("NullOrMissingPayload_RepliesNotCancellable", async () => {
    const runtime = makeFakeRuntime()
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))

    buildClient({ resolver, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    const replyFromNull = (await emitCancel(builder, null)) as { state: string }
    const replyFromMissing = (await emitCancel(builder, undefined)) as { state: string }
    const replyFromNoTarget = (await emitCancel(builder, { target: undefined as unknown as never })) as { state: string }

    expect(replyFromNull).toEqual({ state: "unavailable" })
    expect(replyFromMissing).toEqual({ state: "unavailable" })
    expect(replyFromNoTarget).toEqual({ state: "unavailable" })
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

  it("GenericTargetWithoutSessionId_RepliesUnavailable", async () => {
    const runtime = makeFakeRuntime()
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))

    buildClient({ resolver, openCodeRuntime: runtime.runtime })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, {
      target: { kind: "generic", projectId: "proj-1" },
    })) as { state: string }

    expect(reply).toEqual({ state: "unavailable" })
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
  function runtimeFixtures(): { opencode: ReturnType<typeof makeFakeRuntime>; pi: FakePiRuntimeHandles } {
    return { opencode: makeFakeRuntime(), pi: makeFakePiRuntime() }
  }

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

  it("PiBinding_CancelConfirmed_RepliesCancelled_WithoutInterruptUnconfirmed", async () => {
    const { opencode, pi } = runtimeFixtures()
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
    const { opencode, pi } = runtimeFixtures()
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
    const { opencode, pi } = runtimeFixtures()
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

  it("OpenCodeBinding_CancelRepliesStopRequestedWhenRuntimeCannotConfirm", async () => {
    const { opencode, pi } = runtimeFixtures()
    opencode.setCancelResult({
      ok: true,
      value: { facts: { runtimeSessionId: "runtime-1", workDir: "/work/project", cancelled: true, stopConfirmed: false }, diagnostics: [] },
      diagnostics: [],
    })
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    buildClient({ resolver, openCodeRuntime: opencode.runtime, piRuntime: pi.runtime })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, opencodeCancelPayload("gen-session-1"))) as { state: string; interruptUnconfirmed?: boolean }

    expect(reply).toEqual({ state: "stop-requested" })
    expect(opencode.cancelCalls).toHaveLength(1)
    expect(pi.cancelCalls).toHaveLength(0)
  })

  it("UnknownBinding_RepliesNotCancellable_AndDoesNotCallAnyRuntime", async () => {
    const { opencode, pi } = runtimeFixtures()
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

    expect(reply).toEqual({ state: "unavailable" })
    expect(opencode.cancelCalls).toHaveLength(0)
    expect(pi.cancelCalls).toHaveLength(0)
  })
})
