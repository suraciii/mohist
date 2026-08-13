import { describe, expect, it as vitestIt, vi } from "vitest"
import * as signalR from "@microsoft/signalr"
import type { CancelAgentSessionPayload } from "../src/server/runner-signalr.js"
import type { RunnerFileSystem, RunnerResourceContext } from "../src/system/filesystem.js"
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

describe("RunnerSignalRClient CancelAgentSession redelivery", () => {
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

  it("StartedCancel_ReplayedAfterRunnerRestartRetriesTheRuntimeAbort", async () => {
    const runtime = makeFakeRuntime()
    runtime.setCancelResult({
      ok: false,
      error: {
        kind: "turn-failed",
        message: "transport dropped",
        diagnostics: [],
      },
      diagnostics: [],
    })
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    const journal = new MemoryCancelOperationJournal()
    const payload: CancelAgentSessionPayload = {
      ...genericCancelPayload("gen-session-1"),
      sessionId: "gen-session-1",
      turnId: "turn-1",
      operationId: "stop-1",
    }

    buildClient({ resolver, outbox: readyOutbox(), openCodeRuntime: runtime.runtime, cancelOperationJournal: journal })
    await expect(emitCancel(lastBuilder(), payload)).resolves.toEqual({ state: "stop-requested" })
    await expect(journal.get("gen-session-1", "stop-1")).resolves.toMatchObject({ state: "started" })

    runtime.setCancelResult({
      ok: true,
      value: {
        facts: { runtimeSessionId: "runtime-1", workDir: "/work/project", cancelled: true, stopConfirmed: true },
        diagnostics: [],
      },
      diagnostics: [],
    })
    buildClient({ resolver, outbox: readyOutbox(), openCodeRuntime: runtime.runtime, cancelOperationJournal: journal })
    await expect(emitCancel(lastBuilder(), payload)).resolves.toEqual({ state: "stopped" })

    expect(runtime.cancelCalls).toHaveLength(2)
  })

  it("StartedCancel_ReplayedAfterTargetBecameIdle_CompletesWithoutAbortingAgain", async () => {
    const runtime = makeFakeRuntime()
    runtime.setCancelResult({
      ok: false,
      error: {
        kind: "turn-failed",
        message: "transport dropped",
        diagnostics: [],
      },
      diagnostics: [],
    })
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    const journal = new MemoryCancelOperationJournal()
    const payload: CancelAgentSessionPayload = {
      ...genericCancelPayload("gen-session-1"),
      sessionId: "gen-session-1",
      turnId: "turn-1",
      operationId: "stop-idle",
    }

    buildClient({ resolver, outbox: readyOutbox(), openCodeRuntime: runtime.runtime, cancelOperationJournal: journal })
    await expect(emitCancel(lastBuilder(), payload)).resolves.toEqual({ state: "stop-requested" })

    runtime.setResolveResult({
      ok: true,
      value: { runtimeSessionId: "runtime-1", workDir: "/work/project", activeTurn: false },
      diagnostics: [],
    })
    buildClient({ resolver, outbox: readyOutbox(), openCodeRuntime: runtime.runtime, cancelOperationJournal: journal })
    await expect(emitCancel(lastBuilder(), payload)).resolves.toEqual({ state: "idle" })

    expect(runtime.cancelCalls).toHaveLength(1)
    await expect(journal.get("gen-session-1", "stop-idle")).resolves.toMatchObject({
      state: "completed",
      reply: { state: "idle" },
    })
  })

  it("StartedCancel_ReplayedAfterTargetEnded_CompletesAsEndedWithoutFlippingVerdict", async () => {
    const runtime = makeFakeRuntime()
    runtime.setCancelResult({
      ok: false,
      error: {
        kind: "turn-failed",
        message: "transport dropped",
        diagnostics: [],
      },
      diagnostics: [],
    })
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    const journal = new MemoryCancelOperationJournal()
    const payload: CancelAgentSessionPayload = {
      ...genericCancelPayload("gen-session-1"),
      sessionId: "gen-session-1",
      turnId: "turn-1",
      operationId: "stop-fulfilled",
    }

    buildClient({ resolver, outbox: readyOutbox(), openCodeRuntime: runtime.runtime, cancelOperationJournal: journal })
    await expect(emitCancel(lastBuilder(), payload)).resolves.toEqual({ state: "stop-requested" })

    runtime.setResolveResult({
      ok: false,
      error: {
        kind: "missing-session",
        message: "no physical session",
        diagnostics: [],
      },
      diagnostics: [],
    })
    buildClient({ resolver, outbox: readyOutbox(), openCodeRuntime: runtime.runtime, cancelOperationJournal: journal })
    await expect(emitCancel(lastBuilder(), payload)).resolves.toEqual({ state: "ended" })

    expect(runtime.cancelCalls).toHaveLength(1)
    await expect(journal.get("gen-session-1", "stop-fulfilled")).resolves.toMatchObject({
      state: "completed",
      reply: { state: "ended" },
    })
  })

  it("StartedCancel_ReplayedNotCancellable_CompletesAsNotCancellable", async () => {
    const runtime = makeFakeRuntime()
    runtime.setCancelResult({
      ok: true,
      value: { facts: { runtimeSessionId: "runtime-1", workDir: "/work/project", cancelled: false }, diagnostics: [] } as never,
      diagnostics: [],
    })
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    const journal = new MemoryCancelOperationJournal()
    const payload: CancelAgentSessionPayload = {
      ...genericCancelPayload("gen-session-1"),
      sessionId: "gen-session-1",
      turnId: "turn-1",
      operationId: "stop-not-cancellable",
    }

    await journal.start(payload.sessionId!, payload)
    buildClient({ resolver, openCodeRuntime: runtime.runtime, cancelOperationJournal: journal })
    await expect(emitCancel(lastBuilder(), payload)).resolves.toEqual({ state: "not-cancellable" })
    await expect(journal.get("gen-session-1", "stop-not-cancellable")).resolves.toMatchObject({
      state: "completed",
      reply: { state: "not-cancellable" },
    })
  })

  it("StartedCancel_ReplayedWithIndeterminateProbe_LeavesUnavailableOutstanding", async () => {
    const runtime = makeFakeRuntime()
    runtime.setReady(false)
    const resolver = vi.fn(() => ({ runtimeSessionId: "runtime-1", workDir: "/work/project", projectId: "proj-1" }))
    const journal = new MemoryCancelOperationJournal()
    const payload: CancelAgentSessionPayload = {
      ...genericCancelPayload("gen-session-1"),
      sessionId: "gen-session-1",
      turnId: "turn-1",
      operationId: "stop-probe-unavailable",
    }

    await journal.start(payload.sessionId!, payload)
    buildClient({ resolver, openCodeRuntime: runtime.runtime, cancelOperationJournal: journal })
    await expect(emitCancel(lastBuilder(), payload)).resolves.toEqual({ state: "unavailable" })
    await expect(journal.get("gen-session-1", "stop-probe-unavailable")).resolves.toMatchObject({ state: "started" })
  })
})
