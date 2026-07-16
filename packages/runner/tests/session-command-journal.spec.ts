import { mkdtemp, rm, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import type * as signalR from "@microsoft/signalr"
import { SessionCommandJournal } from "../src/runtime/session-command-journal.js"
import { registerSessionCommandHandler, type SessionCommandRequest } from "../src/server/session-command-handler.js"

let root: string

function request(): SessionCommandRequest {
  return {
    sessionId: "session-1", runtime: "opencode", runtimeSessionId: "runtime-1",
    runnerId: "runner-1", workDir: "/work", command: "compact", operationId: "compact-1",
  }
}

describe("SessionCommandJournal", () => {
  beforeEach(async () => {
    root = await mkdtemp(join(tmpdir(), "mohist-session-command-"))
  })

  afterEach(async () => {
    await rm(root, { recursive: true, force: true })
  })

  it("persists completed results for a new runner process", async () => {
    const first = new SessionCommandJournal(root)
    await first.load()
    await first.start(request())
    await first.complete(request(), { ok: true })

    const restarted = new SessionCommandJournal(root)
    await restarted.load()
    await expect(restarted.get("session-1", "compact-1")).resolves.toMatchObject({
      state: "completed", result: { ok: true },
    })
  })

  it("retains started operations across restart", async () => {
    const first = new SessionCommandJournal(root)
    await first.load()
    await first.start(request())

    const restarted = new SessionCommandJournal(root)
    await restarted.load()
    await expect(restarted.get("session-1", "compact-1")).resolves.toMatchObject({ state: "started" })
  })

  it("fails closed for corrupt state", async () => {
    const journal = new SessionCommandJournal(root)
    const filePath = join(root, ".mohist", "runner-state", "session-commands.json")
    await import("node:fs/promises").then(async ({ mkdir }) => await mkdir(join(root, ".mohist", "runner-state"), { recursive: true }))
    await writeFile(filePath, "not-json")
    await journal.load()

    await expect(journal.get("session-1", "compact-1")).rejects.toThrow("unavailable")
  })

  it.each([
    { version: 1, operations: [] },
    { version: 1, operations: { "session-1": [] } },
  ])("fails closed for parseable invalid state without invoking the runtime", async (file) => {
    const filePath = join(root, ".mohist", "runner-state", "session-commands.json")
    await import("node:fs/promises").then(async ({ mkdir }) => await mkdir(join(root, ".mohist", "runner-state"), { recursive: true }))
    await writeFile(filePath, JSON.stringify(file))
    const journal = new SessionCommandJournal(root)
    await journal.load()

    const callbacks = new Map<string, (request: SessionCommandRequest) => Promise<unknown>>()
    const connection = {
      on: vi.fn((method: string, callback: (request: SessionCommandRequest) => Promise<unknown>) => {
        callbacks.set(method, callback)
      }),
    } as unknown as signalR.HubConnection
    const handler = vi.fn(async () => ({ ok: true, runtimeSessionId: "runtime-2" }))
    registerSessionCommandHandler(connection, { handler, journal })

    const result = await callbacks.get("SessionCommand")!({
      ...request(),
      command: "reset",
      operationId: "reset-1",
      expectedRuntimeSessionId: "runtime-1",
    })

    expect(result).toEqual({ ok: false, error: "unavailable" })
    expect(handler).not.toHaveBeenCalled()
  })

  it.each([
    { ok: true, error: "missing" },
    { ok: false },
    { ok: true, runtimeSessionId: "runtime-2" },
  ])("fails closed for a semantically invalid completed result after restart", async (result) => {
    const filePath = join(root, ".mohist", "runner-state", "session-commands.json")
    await import("node:fs/promises").then(async ({ mkdir }) => await mkdir(join(root, ".mohist", "runner-state"), { recursive: true }))
    await writeFile(filePath, JSON.stringify({
      version: 1,
      operations: {
        "session-1": {
          "compact-1": { request: request(), state: "completed", result },
        },
      },
    }))
    const journal = new SessionCommandJournal(root)
    await journal.load()

    const callbacks = new Map<string, (request: SessionCommandRequest) => Promise<unknown>>()
    const connection = {
      on: vi.fn((method: string, callback: (request: SessionCommandRequest) => Promise<unknown>) => {
        callbacks.set(method, callback)
      }),
    } as unknown as signalR.HubConnection
    const handler = vi.fn(async () => ({ ok: true }))
    registerSessionCommandHandler(connection, { handler, journal })

    await expect(callbacks.get("SessionCommand")!(request())).resolves.toEqual({ ok: false, error: "unavailable" })
    expect(handler).not.toHaveBeenCalled()
  })
})
