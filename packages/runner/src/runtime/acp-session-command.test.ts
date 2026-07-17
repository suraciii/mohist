import { describe, expect, it, vi } from "vitest"
import type { ClientSideConnection } from "@agentclientprotocol/sdk"
import { executeAcpSessionCommand } from "./acp-session-command.js"
import type { SessionCommandRequest } from "../server/session-command-handler.js"

function resetRequest(overrides: Partial<SessionCommandRequest> = {}): SessionCommandRequest {
  return {
    sessionId: "session-1",
    runtime: "opencode",
    runtimeSessionId: "runtime-1",
    runnerId: "runner-1",
    workDir: "/work/project",
    command: "reset",
    expectedRuntimeSessionId: "runtime-1",
    operationId: "reset-1",
    ...overrides,
  }
}

function connection(newSession: ReturnType<typeof vi.fn>): ClientSideConnection {
  return { newSession } as unknown as ClientSideConnection
}

describe("executeAcpSessionCommand", () => {
  it("creates an empty replacement session in the reserved work directory", async () => {
    const newSession = vi.fn(async () => ({ sessionId: "runtime-2" }))

    await expect(executeAcpSessionCommand(resetRequest(), connection(newSession))).resolves.toEqual({
      ok: true,
      runtimeSessionId: "runtime-2",
    })
    expect(newSession).toHaveBeenCalledWith({ cwd: "/work/project", mcpServers: [] })
  })

  it("rejects a stale reset before creating a replacement", async () => {
    const newSession = vi.fn()

    await expect(executeAcpSessionCommand(
      resetRequest({ expectedRuntimeSessionId: "runtime-stale" }),
      connection(newSession),
    )).resolves.toEqual({ ok: false, error: "conflict" })
    expect(newSession).not.toHaveBeenCalled()
  })

  it("reports an unavailable runtime without claiming success", async () => {
    const newSession = vi.fn(async () => { throw new Error("runtime unavailable") })

    await expect(executeAcpSessionCommand(resetRequest(), connection(newSession))).resolves.toEqual({
      ok: false,
      error: "unavailable",
    })
  })

  it("reports that the command did not start when no runtime connection exists", async () => {
    await expect(executeAcpSessionCommand(resetRequest(), null)).resolves.toEqual({
      ok: false,
      error: "notStarted",
    })
  })
})
