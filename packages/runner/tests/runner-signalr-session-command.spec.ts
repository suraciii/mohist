import { describe, expect, it, vi, afterEach } from "vitest"
import {
  RunnerSignalRClient,
  setRunnerSignalRExistsCheckerForTest,
  setRunnerSignalRGitRunnerForTest,
  type SessionCommandRequest,
} from "../src/server/runner-signalr.js"
import { makeFakeRuntime, type FakeRuntimeHandles } from "./support/opencode-runtime-fixture.js"
import { makeFakePiRuntime, type FakePiRuntimeHandles } from "./support/pi-runtime-fixture.js"
import type { HubConnection } from "@microsoft/signalr"

interface FakeBuilder {
  handlers: Map<string, (...args: unknown[]) => unknown>
}

const builders: FakeBuilder[] = []

vi.mock("@microsoft/signalr", () => {
  return {
    HubConnectionBuilder: class {
      private _handlers: Map<string, (...args: unknown[]) => unknown> = new Map()
      withUrl(_url: string) {
        builders.push({ handlers: this._handlers })
        return this
      }
      withAutomaticReconnect(_reconnectPolicy: number[]) {
        return this
      }
      build(): HubConnection {
        const noop = () => undefined
        return {
          on: (event: string, handler: (...args: unknown[]) => unknown) => {
            this._handlers.set(event, handler)
            return undefined
          },
          onreconnected: noop,
          start: async () => undefined,
          stop: async () => undefined,
          invoke: async () => undefined,
          state: "Disconnected",
          connectionId: null,
        } as unknown as HubConnection
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

afterEach(() => {
  vi.restoreAllMocks()
  builders.length = 0
  setRunnerSignalRGitRunnerForTest(null)
  setRunnerSignalRExistsCheckerForTest(null)
})

function lastBuilder(): FakeBuilder {
  const builder = builders.at(-1)
  if (!builder) throw new Error("no captured builder; construct a RunnerSignalRClient first")
  return builder
}

function newClient(opts: { openCodeRuntime: unknown; piRuntime: unknown; journal?: unknown }) {
  builders.length = 0
  return new RunnerSignalRClient(
    "http://localhost:3456",
    "runner-1",
    "/tmp/mohist/projects",
    null,
    {
      openCodeRuntime: opts.openCodeRuntime as never,
      piRuntime: opts.piRuntime as never,
      ...(opts.journal !== undefined ? { sessionCommandJournal: opts.journal as never } : {}),
    },
  )
}

function makeMemoryJournal() {
  const entries = new Map<string, { request: SessionCommandRequest; state: "started" | "completed"; result?: unknown }>()
  return {
    async load() { /* no-op */ },
    async get(sessionId: string, operationId: string) {
      const entry = entries.get(`${sessionId}:${operationId}`) ?? null
      return entry
    },
    async start(request: SessionCommandRequest) {
      const key = `${request.sessionId}:${request.operationId}`
      const existing = entries.get(key)
      if (existing) return existing
      const entry = { request: { ...request }, state: "started" as const }
      entries.set(key, entry)
      return entry
    },
    async complete(request: SessionCommandRequest, result: unknown) {
      const key = `${request.sessionId}:${request.operationId}`
      const existing = entries.get(key)
      if (existing) {
        existing.state = "completed"
        existing.result = result
      } else {
        entries.set(key, { request: { ...request }, state: "completed", result })
      }
    },
  }
}

function makeCompactRequest(runtime: string): SessionCommandRequest {
  return {
    sessionId: "session-1",
    runtime,
    runtimeSessionId: "runtime-1",
    runnerId: "runner-1",
    workDir: "/work/project",
    command: "compact",
    operationId: "operation-compact-1",
  }
}

function makeResetRequest(runtime: string, expectedBinding = "runtime-1"): SessionCommandRequest {
  return {
    sessionId: "session-1",
    runtime,
    runtimeSessionId: expectedBinding,
    runnerId: "runner-1",
    workDir: "/work/project",
    command: "reset",
    expectedRuntimeSessionId: expectedBinding,
    operationId: "operation-reset-1",
  }
}

describe("RunnerSignalRClient routes SessionCommand by persisted binding runtime", () => {
  function setup(opts?: { openCode?: unknown; pi?: unknown; journal?: unknown }) {
    const opencode = makeFakeRuntime()
    const pi = makeFakePiRuntime()
    const openCodeRuntime = opts && "openCode" in opts ? opts.openCode : opencode.runtime
    const piRuntime = opts && "pi" in opts ? opts.pi : pi.runtime
    newClient({
      openCodeRuntime,
      piRuntime,
      journal: opts?.journal ?? makeMemoryJournal(),
    })
    return { builder: lastBuilder(), pi, opencode }
  }

  it("PiBinding_Compact_DispatchesToPiRuntime_AndReturnsOk", async () => {
    const { builder, pi, opencode } = setup()
    const handler = builder.handlers.get("SessionCommand")
    if (!handler) throw new Error("SessionCommand handler not registered")

    const result = (await handler(makeCompactRequest("pi"))) as { ok: boolean; runtimeSessionId?: string; error?: string }

    expect(result).toEqual({ ok: true })
    expect(pi.compactCalls).toHaveLength(1)
    expect(pi.compactCalls[0].target.runtime).toBe("pi")
    expect(pi.compactCalls[0].target.runtimeSessionId).toBe("runtime-1")
    expect(opencode.cancelCalls).toHaveLength(0)
  })

  it("PiBinding_Reset_DispatchesToPiRuntime_AndReturnsReplacementId", async () => {
    const { builder, pi } = setup()
    const handler = builder.handlers.get("SessionCommand")
    if (!handler) throw new Error("SessionCommand handler not registered")

    const result = (await handler(makeResetRequest("pi"))) as { ok: boolean; runtimeSessionId?: string; error?: string }

    expect(result).toEqual({ ok: true, runtimeSessionId: "/virtual/sessions/two.jsonl" })
    expect(pi.resetCalls).toHaveLength(1)
    expect(pi.resetCalls[0].target.runtime).toBe("pi")
  })

  it("OpenCodeBinding_Compact_ReturnsUnavailable_WithoutInvokingEitherRuntime", async () => {
    const { builder, pi } = setup()
    const handler = builder.handlers.get("SessionCommand")
    if (!handler) throw new Error("SessionCommand handler not registered")

    const result = (await handler(makeCompactRequest("opencode"))) as { ok: boolean; runtimeSessionId?: string; error?: string }

    expect(result).toEqual({ ok: false, error: "unavailable" })
    expect(pi.compactCalls).toHaveLength(0)
    expect(pi.resetCalls).toHaveLength(0)
  })

  it("OpenCodeBinding_Reset_ReturnsUnavailable_WithoutInvokingEitherRuntime", async () => {
    const { builder, pi } = setup()
    const handler = builder.handlers.get("SessionCommand")
    if (!handler) throw new Error("SessionCommand handler not registered")

    const result = (await handler(makeResetRequest("opencode"))) as { ok: boolean; runtimeSessionId?: string; error?: string }

    expect(result).toEqual({ ok: false, error: "unavailable" })
    expect(pi.compactCalls).toHaveLength(0)
    expect(pi.resetCalls).toHaveLength(0)
  })

  it("UnknownRuntime_ReturnsUnavailable_AndPreservesErrorVocabulary", async () => {
    const { builder, pi } = setup()
    const handler = builder.handlers.get("SessionCommand")
    if (!handler) throw new Error("SessionCommand handler not registered")

    const result = (await handler(makeCompactRequest("acp"))) as { ok: boolean; runtimeSessionId?: string; error?: string }

    expect(result).toEqual({ ok: false, error: "unavailable" })
    expect(pi.compactCalls).toHaveLength(0)
  })

  it("MissingRuntimeAccessor_ReturnsUnavailable_WithoutThrowing", async () => {
    const { builder } = setup({ pi: null })
    const handler = builder.handlers.get("SessionCommand")
    if (!handler) throw new Error("SessionCommand handler not registered")

    const result = (await handler(makeCompactRequest("pi"))) as { ok: boolean; runtimeSessionId?: string; error?: string }

    expect(result).toEqual({ ok: false, error: "unavailable" })
  })

  it("RedeliveryOfCompletedOp_ReplaysPriorResult_WithoutBlindReExecute", async () => {
    const { builder, pi } = setup()
    const handler = builder.handlers.get("SessionCommand")
    if (!handler) throw new Error("SessionCommand handler not registered")

    const request = makeResetRequest("pi")
    const first = (await handler(request)) as { ok: boolean; runtimeSessionId?: string; error?: string }
    const second = (await handler(request)) as { ok: boolean; runtimeSessionId?: string; error?: string }

    expect(first).toEqual({ ok: true, runtimeSessionId: "/virtual/sessions/two.jsonl" })
    expect(second).toEqual(first)
    expect(pi.resetCalls).toHaveLength(1)
  })

  it("PiCompact_MissingSession_ReportsMissingError", async () => {
    const { builder, pi } = setup()
    pi.setCompactResult({
      ok: false,
      error: { kind: "missing-session", message: "The bound Pi Session is missing", diagnostics: [] },
      diagnostics: [],
    })
    const handler = builder.handlers.get("SessionCommand")
    if (!handler) throw new Error("SessionCommand handler not registered")

    const result = (await handler(makeCompactRequest("pi"))) as { ok: boolean; runtimeSessionId?: string; error?: string }

    expect(result).toEqual({ ok: false, error: "missing" })
    expect(pi.compactCalls).toHaveLength(1)
  })

  it("PiCompact_Streaming_ReportsConflict", async () => {
    const { builder, pi } = setup()
    pi.setCompactResult({
      ok: false,
      error: { kind: "conflict", message: "physical session is still streaming", diagnostics: [] },
      diagnostics: [],
    })
    const handler = builder.handlers.get("SessionCommand")
    if (!handler) throw new Error("SessionCommand handler not registered")

    const result = (await handler(makeCompactRequest("pi"))) as { ok: boolean; runtimeSessionId?: string; error?: string }

    expect(result).toEqual({ ok: false, error: "conflict" })
  })

  it("PostResetBinding_DispatchesByNewRuntime", async () => {
    const { builder, pi } = setup()
    const handler = builder.handlers.get("SessionCommand")
    if (!handler) throw new Error("SessionCommand handler not registered")

    const oldSession = "/virtual/sessions/one.jsonl"
    const request: SessionCommandRequest = {
      ...makeResetRequest("pi", oldSession),
      runtimeSessionId: oldSession,
      expectedRuntimeSessionId: oldSession,
    }
    const result = (await handler(request)) as { ok: boolean; runtimeSessionId?: string; error?: string }

    expect(result).toEqual({ ok: true, runtimeSessionId: "/virtual/sessions/two.jsonl" })
    expect(pi.resetCalls[0].target.runtimeSessionId).toBe(oldSession)
  })
})
