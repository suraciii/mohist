import { describe, expect, it, vi } from "vitest"
import type * as signalR from "@microsoft/signalr"
import {
  registerSessionCommandHandler,
  type SessionCommandError,
  type SessionCommandHandler,
  type SessionCommandRequest,
  type SessionCommandResult,
} from "../src/server/session-command-handler.js"

function register(handler: SessionCommandHandler | null) {
  const handlers = new Map<string, (...args: unknown[]) => unknown>()
  const connection = {
    on: vi.fn((method: string, callback: (...args: unknown[]) => unknown) => {
      handlers.set(method, callback)
    }),
  } as unknown as signalR.HubConnection
  registerSessionCommandHandler(connection, { handler })

  const callback = handlers.get("SessionCommand")
  if (!callback) throw new Error("SessionCommand handler was not registered")
  return (request: SessionCommandRequest) => Promise.resolve(callback(request) as SessionCommandResult)
}

function request(command: "compact" | "reset"): SessionCommandRequest {
  return {
    sessionId: "session-1",
    runtime: "opencode",
    runtimeSessionId: "runtime-1",
    runnerId: "runner-1",
    workDir: "/work/project",
    command,
    ...(command === "reset" ? { expectedRuntimeSessionId: "runtime-1", operationId: "reset-1" } : {}),
  }
}

describe("SessionCommand contract", () => {
  it("compact accepts the recovery reservation and returns no new runtime id", async () => {
    const fakeHandler = vi.fn(async (received: SessionCommandRequest): Promise<SessionCommandResult> => {
      expect(Object.keys(received).sort()).toEqual([
        "command",
        "operationId",
        "runnerId",
        "runtime",
        "runtimeSessionId",
        "sessionId",
        "workDir",
      ])
      return { ok: true }
    })
    const invoke = register(fakeHandler)
    const compact = { ...request("compact"), operationId: "compact-1" }

    const result = await invoke(compact)

    expect(fakeHandler).toHaveBeenCalledWith(compact)
    expect(result).toEqual({ ok: true })
    expect(result).not.toHaveProperty("runtimeSessionId")
  })

  it("reset returns the replacement runtime session id", async () => {
    const fakeHandler = vi.fn(async (): Promise<SessionCommandResult> => ({
      ok: true,
      runtimeSessionId: "runtime-2",
    }))
    const invoke = register(fakeHandler)
    const reset = request("reset")

    const result = await invoke(reset)

    expect(fakeHandler).toHaveBeenCalledWith(reset)
    expect(result).toEqual({ ok: true, runtimeSessionId: "runtime-2" })
  })

  it("deduplicates duplicate reset operation ids before invoking the runtime handler", async () => {
    let release!: () => void
    const deferred = new Promise<void>((resolve) => { release = resolve })
    const fakeHandler = vi.fn(async (): Promise<SessionCommandResult> => {
      await deferred
      return { ok: true, runtimeSessionId: "runtime-2" }
    })
    const invoke = register(fakeHandler)

    const first = invoke(request("reset"))
    const duplicate = invoke(request("reset"))
    release()

    await expect(Promise.all([first, duplicate])).resolves.toEqual([
      { ok: true, runtimeSessionId: "runtime-2" },
      { ok: true, runtimeSessionId: "runtime-2" },
    ])
    expect(fakeHandler).toHaveBeenCalledTimes(1)
  })

  it("rejects a reset without the reserved expected binding", async () => {
    const fakeHandler = vi.fn(async (): Promise<SessionCommandResult> => ({ ok: true }))
    const invoke = register(fakeHandler)
    const invalid = { ...request("reset"), expectedRuntimeSessionId: "runtime-stale" }

    await expect(invoke(invalid)).resolves.toEqual({ ok: false, error: "unavailable" })
    expect(fakeHandler).not.toHaveBeenCalled()
  })

  it("rejects a successful reset response without a replacement runtime session id", async () => {
    const fakeHandler = vi.fn(async (): Promise<SessionCommandResult> => ({ ok: true }))
    const invoke = register(fakeHandler)

    await expect(invoke(request("reset"))).resolves.toEqual({ ok: false, error: "unavailable" })
  })

  it("bounds retained completed reset operations", async () => {
    const fakeHandler = vi.fn(async (received: SessionCommandRequest): Promise<SessionCommandResult> => ({
      ok: true,
      runtimeSessionId: `${received.operationId}-replacement`,
    }))
    const invoke = register(fakeHandler)

    for (let index = 0; index <= 256; index += 1) {
      await invoke({ ...request("reset"), operationId: `reset-${index}` })
    }
    await invoke({ ...request("reset"), operationId: "reset-256" })
    await invoke({ ...request("reset"), operationId: "reset-0" })

    expect(fakeHandler).toHaveBeenCalledTimes(258)
  })

  it.each<SessionCommandError>(["conflict", "missing"])(
    "passes through the %s error vocabulary",
    async (error) => {
      const fakeHandler = vi.fn(async (): Promise<SessionCommandResult> => ({ ok: false, error }))
      const invoke = register(fakeHandler)

      await expect(invoke(request("compact"))).resolves.toEqual({ ok: false, error })
    },
  )

  it("reports unavailable when no runtime handler is installed", async () => {
    const invoke = register(null)

    await expect(invoke(request("compact"))).resolves.toEqual({ ok: false, error: "unavailable" })
  })
})
