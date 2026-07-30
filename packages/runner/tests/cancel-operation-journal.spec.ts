import { mkdtemp, rm, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { afterEach, beforeEach, describe, expect, it } from "vitest"
import { CancelOperationJournal } from "../src/runtime/cancel-operation-journal.js"
import type { CancelAgentSessionPayload } from "../src/server/session-target.js"

let root: string

function request(): CancelAgentSessionPayload {
  return {
    sessionId: "session-1",
    operationId: "stop-1",
    turnId: "turn-1",
    target: {
      kind: "generic",
      projectId: "project-1",
      sessionId: "session-1",
      binding: {
        runtime: "opencode",
        runtimeSessionId: "runtime-1",
        runnerId: "runner-1",
        workDir: "/work",
      },
    },
  }
}

describe("CancelOperationJournal", () => {
  beforeEach(async () => {
    root = await mkdtemp(join(tmpdir(), "mohist-cancel-operation-"))
  })

  afterEach(async () => {
    await rm(root, { recursive: true, force: true })
  })

  it("persists a completed cancel reply for a restarted runner", async () => {
    const first = new CancelOperationJournal(root)
    await first.load()
    await first.start("session-1", request())
    await first.complete("session-1", request(), { state: "stopped" })

    const restarted = new CancelOperationJournal(root)
    await restarted.load()

    await expect(restarted.get("session-1", "stop-1")).resolves.toMatchObject({
      state: "completed",
      reply: { state: "stopped" },
    })
  })

  it("retains an unconfirmed cancel across restart", async () => {
    const first = new CancelOperationJournal(root)
    await first.load()
    await first.start("session-1", request())

    const restarted = new CancelOperationJournal(root)
    await restarted.load()

    await expect(restarted.get("session-1", "stop-1")).resolves.toMatchObject({ state: "started" })
  })

  it("fails closed for corrupt state", async () => {
    const filePath = join(root, ".mohist", "runner-state", "cancel-operations.json")
    await import("node:fs/promises").then(async ({ mkdir }) => await mkdir(join(root, ".mohist", "runner-state"), { recursive: true }))
    await writeFile(filePath, "not-json")
    const journal = new CancelOperationJournal(root)
    await journal.load()

    await expect(journal.get("session-1", "stop-1")).rejects.toThrow("unavailable")
  })
})
