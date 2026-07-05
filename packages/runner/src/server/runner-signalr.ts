import { existsSync as defaultExistsSync } from "node:fs"
import { resolve } from "node:path"
import * as signalR from "@microsoft/signalr"
import type { ClientSideConnection } from "@agentclientprotocol/sdk"
import { deleteDirectory } from "../system/process.js"
import { WorkspaceManager } from "../runtime/workspace.js"
import type { WorkspaceRegistry } from "../runtime/workspace-registry.js"
import { isTerminalWorkflowStatus } from "../runtime/workflow-terminal-status.js"
import type { ServerConnection } from "./connection.js"
import type { SessionTarget } from "../runtime/acp-connection.js"
import {
  isUnderRunnerRoot,
  resolveWorkspaceQuery,
  type WorkspaceQuery,
} from "../runtime/workspace-query.js"
import { resolveSessionTarget, type CancelAgentSessionPayload, type CancelAgentSessionReply, type ReceiveFollowupPayload, type ReceiveWorkflowRunStatusPayload } from "./session-target.js"
import { forceReconnect, notifyReconnected, probeLiveness } from "./liveness-probe.js"
import {
  registerWorkspaceGitHandlers,
  setRunnerSignalRExistsCheckerForTest,
  setRunnerSignalRGitRunnerForTest,
} from "./workspace-git-handlers.js"

export {
  isUnderRunnerRoot,
  resolveWorkspaceQuery,
  resolveSessionTarget,
  setRunnerSignalRExistsCheckerForTest,
  setRunnerSignalRGitRunnerForTest,
}
export type {
  CancelAgentSessionPayload,
  CancelAgentSessionReply,
  ReceiveFollowupPayload,
  ReceiveWorkflowRunStatusPayload,
  WorkspaceQuery,
}

export interface FollowupTarget {
  readonly connection: ClientSideConnection
  readonly sessionId: string
  readonly projectId: string
}

// Issue-129 T-004: the resolver takes a discriminated SessionTarget so a
// single resolver can dispatch both workflow-shaped followups
// (`{ kind: "workflow", projectId, workflowRunId, sessionName }`) and
// generic (non-workflow) followups
// (`{ kind: "generic", projectId, sessionId }`). Older runners that only
// handle workflow followups are reached through the issue-scoped route
// whose SignalR payload still carries the top-level `workflowRunId` /
// `sessionName` fields; the T-004 build of the runner prefers `target`
// when present and falls back to the top-level fields otherwise so the
// wire is forward + backward compatible.
export type FollowupTargetResolver = (target: SessionTarget) => FollowupTarget | null

export interface RunnerSignalRClientOptions {
  probeTimeoutMs?: number
  onReconnected?: (connectionId: string) => void
  serverConnection?: ServerConnection | null
  followupTargetResolver?: FollowupTargetResolver | null
  registry?: WorkspaceRegistry | null
}

export class RunnerSignalRClient {
  private connection: signalR.HubConnection
  private readonly workspaceManager: WorkspaceManager
  private readonly registry: WorkspaceRegistry | null
  private readonly probeTimeoutMs: number
  private readonly onReconnected: ((connectionId: string) => void) | undefined
  private readonly serverConnection: ServerConnection | null
  private readonly followupTargetResolver: FollowupTargetResolver | null

  constructor(
    serverUrl: string,
    runnerId: string,
    private readonly runnerRoot: string,
    buildGitHash: string | null = null,
    options: RunnerSignalRClientOptions = {},
  ) {
    const baseUrl = serverUrl.replace(/\/$/, "")
    const params = new URLSearchParams()
    params.set("runnerId", runnerId)
    if (buildGitHash) params.set("buildGitHash", buildGitHash)
    this.probeTimeoutMs = options.probeTimeoutMs ?? 5_000
    this.onReconnected = options.onReconnected
    this.connection = new signalR.HubConnectionBuilder()
      .withUrl(`${baseUrl}/hubs/runner?${params.toString()}`)
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .build()
    this.registry = options.registry ?? null
    this.workspaceManager = new WorkspaceManager(runnerRoot, this.registry)
    this.serverConnection = options.serverConnection ?? null
    this.followupTargetResolver = options.followupTargetResolver ?? null

    this.registerHandlers()
    this.registerLifecycleCallbacks()
  }

  async start(): Promise<void> {
    await this.connection.start()
  }

  async stop(): Promise<void> {
    await this.connection.stop()
  }

  getConnectionId(): string | null {
    return this.connection.connectionId
  }

  async probeLiveness(signal: AbortSignal): Promise<boolean> {
    return probeLiveness(this.connection, this.probeTimeoutMs, signal)
  }

  async forceReconnect(signal: AbortSignal): Promise<void> {
    return forceReconnect(this.connection, this.onReconnected, signal)
  }

  private registerLifecycleCallbacks(): void {
    this.connection.onreconnected((connectionId) => {
      notifyReconnected(this.connection, this.onReconnected, connectionId)
    })
  }

  private registerHandlers(): void {
    registerWorkspaceGitHandlers(this.connection, {
      resolveQuery: resolveWorkspaceQuery,
    })

    this.connection.on("RemoveWorkspace", async (query: WorkspaceQuery) => {
      if (!query?.workspacePath) {
        await this.dropRegistryEntryForPath(null)
        return removal(false, "missing", query?.workspacePath ?? null, "workspace_missing", "Workspace already removed")
      }
      const workspacePath = resolve(query.workspacePath)
      // Pre-resolve any matching registry entry up front. When the path
      // exists on disk we still drop the entry after a successful delete;
      // when it is missing we still drop the entry (the task notes
      // require `safeRemove` to tolerate already-missing directories —
      // the registry must stay consistent with disk reality).
      await this.dropRegistryEntryForPath(workspacePath)
      if (!defaultExistsSync(workspacePath)) return removal(false, "missing", workspacePath, "workspace_missing", "Workspace already removed")
      if (!isUnderRunnerRoot(this.runnerRoot, workspacePath)) {
        return removal(false, "failed", workspacePath, "workspace_cleanup_refused", "Workspace path is outside the runner-managed root")
      }
      try {
        await deleteDirectory(workspacePath)
        return removal(true, "removed", workspacePath, null, "Workspace removed")
      } catch (error) {
        return removal(false, "failed", workspacePath, "workspace_cleanup_failed", error instanceof Error ? error.message : String(error))
      }
    })

    this.connection.on("ReceiveFollowup", (payload: ReceiveFollowupPayload | null | undefined) => {
      void this.handleFollowup(payload)
    })

    this.connection.on("CancelAgentSession", async (payload: CancelAgentSessionPayload | null | undefined) => {
      return await this.handleCancel(payload)
    })

    this.connection.on("ReceiveWorkflowRunStatus", async (payload: ReceiveWorkflowRunStatusPayload | null | undefined) => {
      await this.handleWorkflowRunStatus(payload)
    })
  }

  // Server-pushed terminal workflow run status. Transitions the matching
  // registry entry from `active` to `eligible` and stamps `terminalAt`.
  // Idempotent: an already-eligible entry is returned unchanged and the
  // on-disk file is not rewritten (per T-003 acceptance criteria).
  //
  // Push is a latency optimization. If the push is missed (runner offline
  // at the moment of the event, transport drop, race with assignment),
  // the convergence backstop wired into RunnerHost.startup / onReconnected
  // / periodic timer is the authoritative catch-all — see
  // `cleanup-convergence.ts`. This handler MUST NOT throw to the SignalR
  // transport: lifecycle events must never crash the connection.
  private async handleWorkflowRunStatus(payload: ReceiveWorkflowRunStatusPayload | null | undefined): Promise<void> {
    if (!payload) return
    const workflowRunId = payload.workflowRunId
    const status = payload.status
    if (!workflowRunId || typeof workflowRunId !== "string") return
    if (!isTerminalWorkflowStatus(status)) {
      // Server only pushes terminal statuses today (see
      // RunnerWorkflowStatusRouter), but guard defensively: an unknown /
      // non-terminal status leaves the entry active. Convergence will
      // re-check on its next tick if needed.
      return
    }
    if (!this.registry) return
    try {
      const updated = await this.registry.markEligible(workflowRunId)
      if (!updated) {
        // Push for a run the runner never materialized (e.g. an event for
        // a workflow whose workspace lives on another runner). The runner
        // only tracks workspaces it owns; nothing to do.
        return
      }
      console.log(
        `workspace cleanup: ${workflowRunId} transitioned to eligible (status=${status}, terminalAt=${updated.terminalAt})`,
      )
    } catch (error) {
      console.error(`workspace cleanup: failed to mark ${workflowRunId} eligible from push:`, error)
    }
  }

  // Drop the registry entry whose workspace path resolves to
  // `workspacePath`. Called by the manual RemoveWorkspace handler so the
  // registry stays consistent with disk reality: the entry is dropped
  // regardless of whether the directory existed on disk, matching the
  // T-002 contract "safeRemove must tolerate an already-missing
  // directory (treat as removed, delete the entry)". `null` is accepted
  // to cover the "query.workspacePath missing" branch — there is no path
  // to match, so the registry is left untouched.
  private async dropRegistryEntryForPath(workspacePath: string | null): Promise<void> {
    if (!this.registry || !workspacePath) return
    const entry = this.registry.findByWorkspacePath(workspacePath)
    if (!entry) return
    try {
      await this.registry.remove(entry.workflowRunId)
    } catch (error) {
      console.error("workspace registry remove failed:", error)
    }
  }

  private async handleFollowup(payload: ReceiveFollowupPayload | null | undefined): Promise<void> {
    if (!payload || typeof payload.text !== "string" || payload.text.length === 0) return
    if (!this.followupTargetResolver || !this.serverConnection) return

    // Issue-129 T-004: branch on the discriminated `target.kind` so a
    // single handler can deliver followups to either a workflow-shaped
    // session or a generic (non-workflow) AgentSession. The
    // server-side payload always carries the unified `target` shape
    // (T-004 / D3); when the target is absent we fall back to the
    // legacy top-level workflowRunId / sessionName fields so older
    // server builds (no `target` field) keep working against the
    // workflow followup route.
    const sessionTarget = resolveSessionTarget(payload)
    if (!sessionTarget) return

    let target: FollowupTarget | null
    try {
      target = this.followupTargetResolver(sessionTarget)
    } catch (error) {
      console.error("followup target resolver threw:", error)
      return
    }
    if (!target) return

    if (sessionTarget.kind === "workflow") {
      void this.serverConnection.workflowAgentSessionRuntimeEvents(
        target.projectId,
        sessionTarget.workflowRunId,
        sessionTarget.sessionName,
        {
          workId: null,
          workType: null,
          stage: null,
          runtimeEvents: [
            {
              type: "session.input",
              payload: {
                role: "user",
                text: payload.text,
                kind: "followup",
                sentAt: new Date().toISOString(),
                acpSessionId: target.sessionId,
                source: "followup",
              },
            },
          ],
        },
        new AbortController().signal,
      ).catch((error) => {
        console.error("failed to emit followup session.input event:", error)
      })
    } else {
      void this.serverConnection.agentSessionRuntimeEvents(
        target.projectId,
        sessionTarget.sessionId,
        {
          workId: null,
          workType: null,
          stage: null,
          runtimeEvents: [
            {
              type: "session.input",
              payload: {
                role: "user",
                text: payload.text,
                kind: "followup",
                sentAt: new Date().toISOString(),
                acpSessionId: target.sessionId,
                source: "followup",
              },
            },
          ],
        },
        new AbortController().signal,
      ).catch((error) => {
        console.error("failed to emit followup session.input event:", error)
      })
    }

    void target.connection
      .prompt({
        sessionId: target.sessionId,
        prompt: [{ type: "text", text: payload.text }],
      })
      .catch((error) => {
        console.error("followup connection.prompt rejected:", error instanceof Error ? error.message : String(error))
      })
  }

  // Server-invoked cancel (issue-129 T-005 / design D6). The server
  // pushes a `CancelAgentSession` SignalR invocation carrying a
  // `SessionTarget` and expects a `{ state: ... }` reply that the HTTP
  // endpoint mirrors verbatim. The handler branches on the same
  // `target.kind` discriminator introduced in T-004 (workflow vs generic)
  // but today only the generic path is reachable from the product API
  // because the issue-scoped session lifecycle has no cancel surface.
  //
  // The runner reports the state it actually observed:
  //   - `cancelled` — a live ACP session entry exists for the target AND
  //     the connection advertises a `cancel` method. The handler fires
  //     the `session/cancel` notification (best-effort) and replies
  //     `cancelled`. Whether the agent actually honours the cancellation
  //     is the agent's decision; the runner is honest about the attempt.
  //   - `not-cancellable` — the runner has no live ACP session entry for
  //     the target, OR the connection has no `cancel` method. There is
  //     nothing to cancel.
  //
  // The server already short-circuits terminal sessions before invoking
  // the runner (T-005 / design D6), so a `terminal-state` reply from the
  // runner is rare but reserved (e.g. for a race window where the agent
  // reports the session as terminal in the same instant we sent the
  // cancel). The handler does not invent terminal states — the server is
  // the source of truth.
  private async handleCancel(payload: CancelAgentSessionPayload | null | undefined): Promise<CancelAgentSessionReply> {
    if (!payload || !payload.target) {
      return { state: "not-cancellable" }
    }

    // The cancel endpoint only addresses generic sessions today, so any
    // other `target.kind` (or missing kind) is treated as not-cancellable.
    const target = payload.target
    if (target.kind !== "generic" || !target.sessionId) {
      return { state: "not-cancellable" }
    }

    if (!this.followupTargetResolver) {
      return { state: "not-cancellable" }
    }

    const sessionTarget: SessionTarget = {
      kind: "generic",
      projectId: target.projectId ?? "",
      sessionId: target.sessionId,
    }

    let resolved: FollowupTarget | null
    try {
      resolved = this.followupTargetResolver(sessionTarget)
    } catch (error) {
      console.error("cancel target resolver threw:", error)
      return { state: "not-cancellable" }
    }

    if (!resolved) {
      // No live ACP session entry for this target. There is nothing to
      // cancel — the API must report that honestly.
      return { state: "not-cancellable" }
    }

    // `ClientSideConnection.cancel` is a notification, not a request —
    // the call resolves once the message is on the wire, not when the
    // agent honours it. The agent decides what to do; the runner is
    // honest about the attempt. The `?.` guard handles a hypothetical
    // older connection that did not advertise cancel (the current SDK
    // always defines it on `ClientSideConnection`).
    const cancel = resolved.connection.cancel?.bind(resolved.connection) as
      | ((params: { sessionId: string }) => Promise<void>)
      | undefined
    if (typeof cancel !== "function") {
      return { state: "not-cancellable" }
    }

    try {
      await cancel({ sessionId: resolved.sessionId })
    } catch (error) {
      // The transport-level cancel send failed (e.g. the connection died
      // between the resolver hit and the send). Surface this as
      // `not-cancellable` rather than fabricating a `cancelled` reply;
      // the caller can retry against a freshly-opened session.
      console.error("cancel connection.cancel rejected:", error instanceof Error ? error.message : String(error))
      return { state: "not-cancellable" }
    }

    return { state: "cancelled" }
  }
}

function removal(removed: boolean, status: string, path: string | null, reason: string | null, message: string) {
  return { removed, status, path, reason, message }
}
