import { mkdtemp, rm, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { afterEach, beforeEach, describe, expect, it } from "vitest"
import { SessionCommandJournal } from "../src/runtime/session-command-journal.js"
import type { SessionCommandRequest } from "../src/server/session-command-handler.js"

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
})
