import { join } from "node:path"
import { describe, expect, it as vitestIt, vi } from "vitest"
import { registerFollowupHandler } from "./followup-handler.js"
import { MemoryFileSystem } from "../../tests/support/memory-filesystem.js"
import { withTestRunnerResources } from "../../tests/support/test-resources.js"

function it(name: string, body: (fileSystem: MemoryFileSystem) => Promise<void>): void {
  vitestIt(name, async () => {
    const fileSystem = new MemoryFileSystem()
    try {
      await withTestRunnerResources(async () => await body(fileSystem), { fileSystem })
    } finally {
      await fileSystem.deleteDirectory("/")
      if (fileSystem.exists("/")) throw new Error("follow-up test filesystem was not cleaned up")
    }
  })
}

describe("follow-up attachment delivery", () => {
  it("executes an attachment-only turn through the owning input scope", async (fileSystem) => {
    const workDir = "/virtual/mohist-followup-attachment"
    let receive: ((payload: unknown) => Promise<{ accepted: boolean }>) | undefined
    const connection = {
      on: vi.fn((_name: string, handler: (payload: unknown) => Promise<{ accepted: boolean }>) => {
        receive = handler
      }),
    }
    const runtimeFollowup = vi.fn(async (request: { prompt: string; fileParts?: readonly unknown[] }) => ({
      ok: true as const,
      value: { facts: { runtimeSessionId: "runtime-1", workDir }, diagnostics: [] },
      diagnostics: [],
    }))
    const runtime = {
      ready: () => true,
      resolveSession: vi.fn(async () => ({ ok: true as const, value: { activeTurn: false } })),
      followup: runtimeFollowup,
    }
    const records: unknown[] = []
    const outbox = {
      ready: () => true,
      enqueueBeforeExecution: vi.fn(async (record: unknown) => {
        records.push(record)
      }),
      enqueueProducedFact: vi.fn(async (record: unknown) => {
        records.push(record)
      }),
    }
    const serverConnection = {
      runnerId: "runner-1",
      openAgentInputAttachment: vi.fn(async (
        projectId: string,
        sessionId: string,
        inputId: string,
        attachmentId: string,
      ) => {
        expect([projectId, sessionId, inputId, attachmentId]).toEqual([
          "project-1",
          "session-1",
          "input-1",
          "attachment-1",
        ])
        return {
          bytes: new TextEncoder().encode("follow-up attachment"),
          contentType: "text/plain",
          contentDisposition: null,
        }
      }),
    }

    registerFollowupHandler(connection as never, {
      followupTargetResolver: () => ({
        runtimeSessionId: "runtime-1",
        workDir,
        projectId: "project-1",
      }),
      agentSessionRuntimeEventOutbox: outbox as never,
      openCodeRuntime: runtime as never,
      connection: serverConnection as never,
      runnerId: "runner-1",
    })

    const result = await receive?.({
      target: {
        kind: "generic",
        projectId: "project-1",
        sessionId: "session-1",
        binding: {
          runtime: "opencode",
          runtimeSessionId: "runtime-1",
          runnerId: "runner-1",
          workDir,
        },
      },
      text: "",
      inputId: "input-1",
      turnId: "turn-1",
      attachments: [{ id: "attachment-1", name: "notes.txt", contentType: "text/plain", size: 20 }],
      callerTempUrl: "https://provider.invalid/temp-token",
      providerToken: "secret-token",
      rawPlatformEvent: { token: "secret-token" },
    })

    expect(result).toEqual({ accepted: true })
    expect(runtimeFollowup).toHaveBeenCalledOnce()
    const request = runtimeFollowup.mock.calls[0]?.[0]
    expect(request.prompt).toContain("[mohist-attachments]")
    expect(request.prompt).toContain("notes.txt")
    expect(request.prompt).not.toContain("provider.invalid")
    expect(request.prompt).not.toContain("secret-token")
    expect(request.fileParts).toBeUndefined()
    expect(await fileSystem.readText(join(workDir, ".mohist/attachments/input-1/attachment-1/notes.txt")))
      .toBe("follow-up attachment")
    expect(JSON.stringify(records)).not.toContain("secret-token")
    expect(JSON.stringify(records)).not.toContain("rawPlatformEvent")
  })
})
