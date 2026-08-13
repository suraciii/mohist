import { join } from "node:path"
import { describe, expect, it } from "vitest"
import {
  CancelOperationJournal,
  type CancelOperationJournalFileSystem,
} from "../src/runtime/cancel-operation-journal.js"
import type { CancelAgentSessionPayload } from "../src/server/session-target.js"

const root = "/runner"
const journalPath = join(root, ".mohist", "runner-state", "cancel-operations.json")

class InMemoryJournalFileSystem implements CancelOperationJournalFileSystem {
  readonly files = new Map<string, string>()

  async readText(path: string): Promise<string | null> {
    return this.files.get(path) ?? null
  }

  async writeAtomicText(path: string, body: string): Promise<void> {
    this.files.set(path, body)
  }

  async rename(source: string, destination: string): Promise<void> {
    const body = this.files.get(source)
    if (body === undefined) throw new Error("source missing")
    this.files.delete(source)
    this.files.set(destination, body)
  }
}

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
  it("persists a completed cancel reply for a restarted runner", async () => {
    const fileSystem = new InMemoryJournalFileSystem()
    const first = new CancelOperationJournal(root, journalPath, fileSystem)
    await first.load()
    await first.start("session-1", request())
    await first.complete("session-1", request(), { state: "stopped" })

    const restarted = new CancelOperationJournal(root, journalPath, fileSystem)
    await restarted.load()

    await expect(restarted.get("session-1", "stop-1")).resolves.toMatchObject({
      state: "completed",
      reply: { state: "stopped" },
    })
  })

  it("retains an unconfirmed cancel across restart", async () => {
    const fileSystem = new InMemoryJournalFileSystem()
    const first = new CancelOperationJournal(root, journalPath, fileSystem)
    await first.load()
    await first.start("session-1", request())

    const restarted = new CancelOperationJournal(root, journalPath, fileSystem)
    await restarted.load()

    await expect(restarted.get("session-1", "stop-1")).resolves.toMatchObject({ state: "started" })
  })

  it("quarantines corrupt state and starts empty", async () => {
    const fileSystem = new InMemoryJournalFileSystem()
    await fileSystem.writeAtomicText(journalPath, "not-json")
    const journal = new CancelOperationJournal(root, journalPath, fileSystem)
    await journal.load()

    await expect(journal.get("session-1", "stop-1")).resolves.toBeNull()
    expect(fileSystem.files.get(`${journalPath}.corrupt`)).toBe("not-json")
    await expect(journal.start("session-1", request())).resolves.toMatchObject({ state: "started" })
  })

  it("discards entries loaded before a corrupt entry", async () => {
    const fileSystem = new InMemoryJournalFileSystem()
    await fileSystem.writeAtomicText(journalPath, JSON.stringify({
      version: 1,
      operations: {
        "session-1": { "stop-1": { request: request(), state: "started" } },
        "session-2": { "stop-2": { request: request(), state: "invalid" } },
      },
    }))
    const journal = new CancelOperationJournal(root, journalPath, fileSystem)
    await journal.load()

    await expect(journal.get("session-1", "stop-1")).resolves.toBeNull()
    expect(fileSystem.files.has(`${journalPath}.corrupt`)).toBe(true)
  })
})
