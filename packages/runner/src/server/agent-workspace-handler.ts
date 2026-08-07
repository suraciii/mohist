// Server-invoked `MaterializeAgentWorkspace` / `ReleaseAgentWorkspace`
// SignalR methods, registered through the free-function
// `registerAgentWorkspaceHandler(conn, deps)` so the dependency surface
// is explicit and the handler can be exercised independently from the
// connection lifecycle. The manager's registry entry is the
// idempotency record — no second operation log.
//
// Reply contract:
//   - MaterializeAgentWorkspace →
//     `{ ok: true, kind: "materialized", workspaceIdentity, workDir }` |
//     `{ ok: false, kind: "rejected", reason, message }` | unavailable
//   - ReleaseAgentWorkspace →
//     `{ ok: true, kind: "released" }` | `{ ok: false, kind: "not-found" }` |
//     `{ ok: false, kind: "invalid", message }` | unavailable

import * as signalR from "@microsoft/signalr"
import type { AgentWorkspaceManager, MaterializeAgentWorkspaceRequest, MaterializeRejectionReason } from "../runtime/agent-workspace.js"
import { runnerLogger } from "../system/logger.js"

const log = runnerLogger.child("workspace")

export interface MaterializeAgentWorkspacePayload {
  projectId?: string | null
  childSessionId?: string | null
  parentWorkDir?: string | null
  repository?: { name?: string | null; gitUrl?: string | null; baseBranch?: string | null } | null
}

export interface ReleaseAgentWorkspacePayload {
  childSessionId?: string | null
  workspaceIdentity?: string | null
}

export type MaterializeAgentWorkspaceReply =
  | { ok: true; kind: "materialized"; workspaceIdentity: string; workDir: string }
  | { ok: false; kind: "rejected"; reason: MaterializeRejectionReason; message: string }
  | { ok: false; kind: "unavailable" }

export type ReleaseAgentWorkspaceReply =
  | { ok: true; kind: "released" }
  | { ok: false; kind: "not-found" }
  | { ok: false; kind: "invalid"; message: string }
  | { ok: false; kind: "unavailable" }

export interface AgentWorkspaceHandlerDeps {
  manager?: AgentWorkspaceManager | null
}

export function registerAgentWorkspaceHandler(
  conn: signalR.HubConnection,
  deps: AgentWorkspaceHandlerDeps,
): void {
  conn.on("MaterializeAgentWorkspace", async (payload: MaterializeAgentWorkspacePayload | null | undefined) => {
    const manager = deps.manager
    if (!manager) return unavailable()
    const request = parseMaterializePayload(payload)
    if (!request) {
      return { ok: false, kind: "rejected", reason: "invalid", message: "request shape is invalid" } satisfies MaterializeAgentWorkspaceReply
    }
    try {
      const result = await manager.materialize(request, new AbortController().signal)
      if (result.kind === "materialized") {
        return { ok: true, kind: "materialized", workspaceIdentity: result.workspaceIdentity, workDir: result.workDir } satisfies MaterializeAgentWorkspaceReply
      }
      return { ok: false, kind: "rejected", reason: result.reason, message: result.message } satisfies MaterializeAgentWorkspaceReply
    } catch (error) {
      log.error("materialize agent workspace failed", { session: request.childSessionId, exception: error })
      return unavailable()
    }
  })

  conn.on("ReleaseAgentWorkspace", async (payload: ReleaseAgentWorkspacePayload | null | undefined) => {
    const manager = deps.manager
    if (!manager) return unavailable()
    const request = parseReleasePayload(payload)
    if (!request) {
      return { ok: false, kind: "invalid", message: "request shape is invalid" } satisfies ReleaseAgentWorkspaceReply
    }
    try {
      const result = await manager.release(request)
      if (result.kind === "released") {
        return { ok: true, kind: "released" } satisfies ReleaseAgentWorkspaceReply
      }
      if (result.kind === "not-found") {
        return { ok: false, kind: "not-found" } satisfies ReleaseAgentWorkspaceReply
      }
      return { ok: false, kind: "invalid", message: result.message } satisfies ReleaseAgentWorkspaceReply
    } catch (error) {
      log.error("release agent workspace failed", { session: request.childSessionId, exception: error })
      return unavailable()
    }
  })
}

function parseMaterializePayload(payload: MaterializeAgentWorkspacePayload | null | undefined): MaterializeAgentWorkspaceRequest | null {
  if (!payload || typeof payload !== "object") return null
  const { childSessionId, parentWorkDir, repository } = payload
  if (typeof childSessionId !== "string" || childSessionId.length === 0) return null
  if (typeof parentWorkDir !== "string" || parentWorkDir.length === 0) return null
  if (!repository || typeof repository !== "object") return null
  const { name, gitUrl, baseBranch } = repository
  if (typeof name !== "string" || name.length === 0) return null
  if (typeof gitUrl !== "string" || gitUrl.length === 0) return null
  if (typeof baseBranch !== "string" || baseBranch.length === 0) return null
  return {
    projectId: typeof payload.projectId === "string" && payload.projectId.length > 0 ? payload.projectId : null,
    childSessionId,
    parentWorkDir,
    repository: { name, gitUrl, baseBranch },
  }
}

function parseReleasePayload(payload: ReleaseAgentWorkspacePayload | null | undefined): { childSessionId: string; workspaceIdentity: string } | null {
  if (!payload || typeof payload !== "object") return null
  const { childSessionId, workspaceIdentity } = payload
  if (typeof childSessionId !== "string" || childSessionId.length === 0) return null
  if (typeof workspaceIdentity !== "string" || workspaceIdentity.length === 0) return null
  return { childSessionId, workspaceIdentity }
}

function unavailable(): MaterializeAgentWorkspaceReply & ReleaseAgentWorkspaceReply {
  return { ok: false, kind: "unavailable" }
}
