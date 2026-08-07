import { describe, expect, it, vi } from "vitest"
import type * as signalR from "@microsoft/signalr"
import type { AgentWorkspaceManager } from "../src/runtime/agent-workspace.js"
import {
  registerAgentWorkspaceHandler,
  type MaterializeAgentWorkspaceReply,
  type ReleaseAgentWorkspaceReply,
} from "../src/server/agent-workspace-handler.js"

const CHILD_ID = "00000000000000000000000000000001"

function register(manager: AgentWorkspaceManager | null) {
  const handlers = new Map<string, (...args: unknown[]) => unknown>()
  const connection = {
    on: vi.fn((method: string, callback: (...args: unknown[]) => unknown) => {
      handlers.set(method, callback)
    }),
  } as unknown as signalR.HubConnection
  registerAgentWorkspaceHandler(connection, { manager })
  return {
    materialize: handlers.get("MaterializeAgentWorkspace") as (payload: unknown) => Promise<MaterializeAgentWorkspaceReply>,
    release: handlers.get("ReleaseAgentWorkspace") as (payload: unknown) => Promise<ReleaseAgentWorkspaceReply>,
  }
}

describe("registerAgentWorkspaceHandler", () => {
  it("Materialize_ForwardsToTheManager_AndMapsTheResult", async () => {
    const manager = {
      materialize: vi.fn(async () => ({
        kind: "materialized" as const,
        workspaceIdentity: `agent-wt:${CHILD_ID}`,
        workDir: `/runner/agent-workspaces/${CHILD_ID}`,
      })),
    } as unknown as AgentWorkspaceManager
    const handler = register(manager)
    const payload = {
      projectId: "project-1",
      childSessionId: CHILD_ID,
      parentWorkDir: "/runner/workspaces/wr-1",
      repository: { name: "main", gitUrl: "https://example.test/mohist.git", baseBranch: "master" },
    }

    const reply = await handler.materialize(payload)

    expect(reply).toEqual({
      ok: true,
      kind: "materialized",
      workspaceIdentity: `agent-wt:${CHILD_ID}`,
      workDir: `/runner/agent-workspaces/${CHILD_ID}`,
    })
    expect(manager.materialize).toHaveBeenCalledWith(payload, expect.any(AbortSignal))
  })

  it("Materialize_InvalidPayload_IsRejectedInvalid", async () => {
    const manager = { materialize: vi.fn() } as unknown as AgentWorkspaceManager
    const handler = register(manager)

    const reply = await handler.materialize({ childSessionId: CHILD_ID })

    expect(reply).toEqual({ ok: false, kind: "rejected", reason: "invalid", message: "request shape is invalid" })
    expect(manager.materialize).not.toHaveBeenCalled()
  })

  it("Materialize_ManagerThrow_IsUnavailable", async () => {
    const manager = {
      materialize: vi.fn(async () => {
        throw new Error("boom")
      }),
    } as unknown as AgentWorkspaceManager
    const handler = register(manager)

    const reply = await handler.materialize({
      childSessionId: CHILD_ID,
      parentWorkDir: "/runner/workspaces/wr-1",
      repository: { name: "main", gitUrl: "https://example.test/mohist.git", baseBranch: "master" },
    })

    expect(reply).toEqual({ ok: false, kind: "unavailable" })
  })

  it("Release_ForwardsToTheManager_AndMapsTheResult", async () => {
    const manager = {
      release: vi.fn(async () => ({ kind: "released" as const })),
    } as unknown as AgentWorkspaceManager
    const handler = register(manager)

    const reply = await handler.release({ childSessionId: CHILD_ID, workspaceIdentity: `agent-wt:${CHILD_ID}` })

    expect(reply).toEqual({ ok: true, kind: "released" })
    expect(manager.release).toHaveBeenCalledWith({ childSessionId: CHILD_ID, workspaceIdentity: `agent-wt:${CHILD_ID}` })
  })

  it("Release_NotFound_IsMapped", async () => {
    const manager = {
      release: vi.fn(async () => ({ kind: "not-found" as const })),
    } as unknown as AgentWorkspaceManager
    const handler = register(manager)

    const reply = await handler.release({ childSessionId: CHILD_ID, workspaceIdentity: `agent-wt:${CHILD_ID}` })

    expect(reply).toEqual({ ok: false, kind: "not-found" })
  })

  it("Release_InvalidIdentity_IsMapped", async () => {
    const manager = {
      release: vi.fn(async () => ({ kind: "invalid" as const, message: "workspaceIdentity does not match childSessionId" })),
    } as unknown as AgentWorkspaceManager
    const handler = register(manager)

    const reply = await handler.release({ childSessionId: CHILD_ID, workspaceIdentity: "agent-wt:someone-else" })

    expect(reply).toEqual({ ok: false, kind: "invalid", message: "workspaceIdentity does not match childSessionId" })
  })

  it("WithoutManager_BothMethodsAreUnavailable", async () => {
    const handler = register(null)

    expect(await handler.materialize({})).toEqual({ ok: false, kind: "unavailable" })
    expect(await handler.release({})).toEqual({ ok: false, kind: "unavailable" })
  })
})
