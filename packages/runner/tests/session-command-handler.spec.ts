import { describe, expect, it, vi } from "vitest"
import type * as signalR from "@microsoft/signalr"
import type { SessionCommandJournalEntry, SessionCommandJournalStore } from "../src/runtime/session-command-journal.js"
import {
  registerSessionCommandHandler,
  type SessionCommandError,
  type SessionCommandHandler,
  type SessionCommandReconciler,
  type SessionCommandRequest,
  type SessionCommandResult,
} from "../src/server/session-command-handler.js"

class MemoryJournal implements SessionCommandJournalStore {
  private readonly entries = new Map<string, SessionCommandJournalEntry>()

  async load(): Promise<void> {}

  async get(sessionId: string, operationId: string): Promise<SessionCommandJournalEntry | null> {
    return this.entries.get(`${sessionId}:${operationId}`) ?? null
  }

  async start(request: SessionCommandRequest): Promise<SessionCommandJournalEntry> {
    const key = `${request.sessionId}:${request.operationId}`
    const existing = this.entries.get(key)
    if (existing) return existing
    const entry: SessionCommandJournalEntry = { request: { ...request }, state: "started" }
    this.entries.set(key, entry)
    return entry
  }

  async complete(request: SessionCommandRequest, result: SessionCommandResult): Promise<void> {
    const key = `${request.sessionId}:${request.operationId}`
    this.entries.set(key, { request: { ...request }, state: "completed", result: { ...result } })
  }
}

function register(
  handler: SessionCommandHandler | null,
  journal = new MemoryJournal(),
  reconcileStarted?: SessionCommandReconciler,
) {
  const handlers = new Map<string, (...args: unknown[]) => unknown>()
  const connection = {
    on: vi.fn((method: string, callback: (...args: unknown[]) => unknown) => {
      handlers.set(method, callback)
    }),
  } as unknown as signalR.HubConnection
  registerSessionCommandHandler(connection, { handler, journal, reconcileStarted })

  const callback = handlers.get("SessionCommand")
  if (!callback) throw new Error("SessionCommand handler was not registered")
  return (request: SessionCommandRequest) => Promise.resolve(callback(request) as SessionCommandResult)
}

function request(command: "compact" | "reset", operationId = `${command}-1`): SessionCommandRequest {
  return {
    sessionId: "session-1",
    runtime: "opencode",
    runtimeSessionId: "runtime-1",
    runnerId: "runner-1",
    workDir: "/work/project",
    command,
    operationId,
    ...(command === "reset" ? { expectedRuntimeSessionId: "runtime-1" } : {}),
  }
}

describe("SessionCommand contract", () => {
  it("compact accepts the durable reservation and returns no new runtime id", async () => {
    const fakeHandler = vi.fn(async (received: SessionCommandRequest): Promise<SessionCommandResult> => {
      expect(Object.keys(received).sort()).toEqual([
        "command", "operationId", "runnerId", "runtime", "runtimeSessionId", "sessionId", "workDir",
      ])
      return { ok: true }
    })
    const compact = request("compact")

    const result = await register(fakeHandler)(compact)

    expect(fakeHandler).toHaveBeenCalledWith(compact)
    expect(result).toEqual({ ok: true })
  })

  it("reset returns the replacement runtime session id", async () => {
    const result = await register(async () => ({ ok: true, runtimeSessionId: "runtime-2" }))(request("reset"))

    expect(result).toEqual({ ok: true, runtimeSessionId: "runtime-2" })
  })

  it.each(["compact", "reset"] as const)("deduplicates concurrent %s delivery", async (command) => {
    let release!: () => void
    const deferred = new Promise<void>((resolve) => { release = resolve })
    const fakeHandler = vi.fn(async (): Promise<SessionCommandResult> => {
      await deferred
      return command === "compact" ? { ok: true } : { ok: true, runtimeSessionId: "runtime-2" }
    })
    const invoke = register(fakeHandler)
    const commandRequest = request(command)

    const first = invoke(commandRequest)
    const duplicate = invoke(commandRequest)
    release()

    await expect(Promise.all([first, duplicate])).resolves.toEqual(command === "compact"
      ? [{ ok: true }, { ok: true }]
      : [{ ok: true, runtimeSessionId: "runtime-2" }, { ok: true, runtimeSessionId: "runtime-2" }])
    expect(fakeHandler).toHaveBeenCalledTimes(1)
  })

  it("rejects a mismatched command that reuses an in-flight operation id", async () => {
    let release!: () => void
    const deferred = new Promise<void>((resolve) => { release = resolve })
    const handler = vi.fn(async (): Promise<SessionCommandResult> => {
      await deferred
      return { ok: true }
    })
    const invoke = register(handler)
    const compact = request("compact", "operation-1")
    const reset = {
      ...request("reset", "operation-1"),
      runtimeSessionId: "runtime-2",
      expectedRuntimeSessionId: "runtime-2",
    }

    const inFlightCompact = invoke(compact)
    await expect(invoke(reset)).resolves.toEqual({ ok: false, error: "unavailable" })
    release()

    await expect(inFlightCompact).resolves.toEqual({ ok: true })
    expect(handler).toHaveBeenCalledTimes(1)
    expect(handler).toHaveBeenCalledWith(compact)
  })

  it.each(["compact", "reset"] as const)("replays a completed %s after handler restart", async (command) => {
    const journal = new MemoryJournal()
    const commandRequest = request(command)
    const firstHandler = vi.fn(async (): Promise<SessionCommandResult> => command === "compact"
      ? { ok: true }
      : { ok: true, runtimeSessionId: "runtime-2" })
    await register(firstHandler, journal)(commandRequest)

    const restartedHandler = vi.fn(async (): Promise<SessionCommandResult> => ({ ok: false, error: "unavailable" }))
    const replay = await register(restartedHandler, journal)(commandRequest)

    expect(replay).toEqual(command === "compact" ? { ok: true } : { ok: true, runtimeSessionId: "runtime-2" })
    expect(restartedHandler).not.toHaveBeenCalled()
  })

  it("fails closed instead of replaying an invalid completed result", async () => {
    const journal = new MemoryJournal()
    const commandRequest = request("compact")
    await journal.complete(commandRequest, { ok: true, error: "missing" } as SessionCommandResult)
    const handler = vi.fn(async (): Promise<SessionCommandResult> => ({ ok: true }))

    await expect(register(handler, journal)(commandRequest)).resolves.toEqual({ ok: false, error: "unavailable" })
    expect(handler).not.toHaveBeenCalled()
  })

  it("reconciles a started operation without blind execution", async () => {
    const journal = new MemoryJournal()
    const commandRequest = request("reset")
    await journal.start(commandRequest)
    const handler = vi.fn(async (): Promise<SessionCommandResult> => ({ ok: true, runtimeSessionId: "runtime-2" }))

    const unavailable = await register(handler, journal)(commandRequest)
    expect(unavailable).toEqual({ ok: false, error: "unavailable" })
    expect(handler).not.toHaveBeenCalled()

    const reconciled = await register(handler, journal, async () => ({
      state: "completed",
      result: { ok: true, runtimeSessionId: "runtime-2" },
    }))(commandRequest)
    expect(reconciled).toEqual({ ok: true, runtimeSessionId: "runtime-2" })
    expect(handler).not.toHaveBeenCalled()
  })

  it("rejects a reset without the reserved expected binding", async () => {
    const fakeHandler = vi.fn(async (): Promise<SessionCommandResult> => ({ ok: true }))
    const invalid = { ...request("reset"), expectedRuntimeSessionId: "runtime-stale" }

    await expect(register(fakeHandler)(invalid)).resolves.toEqual({ ok: false, error: "unavailable" })
    expect(fakeHandler).not.toHaveBeenCalled()
  })

  it.each<SessionCommandError>(["conflict", "missing", "notStarted"])("persists the %s error vocabulary", async (error) => {
    const invoke = register(async () => ({ ok: false, error }))

    await expect(invoke(request("compact"))).resolves.toEqual({ ok: false, error })
  })

  it("reports unavailable when no runtime handler is installed", async () => {
    await expect(register(null)(request("compact"))).resolves.toEqual({ ok: false, error: "unavailable" })
  })
})
