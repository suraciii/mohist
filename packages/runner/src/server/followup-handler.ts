// Issue-313 T-008 / design P7 / D2 / D3 / D5: the server-pushed
// `ReceiveFollowup` SignalR method is extracted from `runner-signalr.ts`
// into a free-function `registerFollowupHandler(conn, deps)` so the
// cluster's dependency surface is explicit (D3) and so the fire-and-forget
// followup handler can be wired up independently of the other push
// handlers.
//
// Issue-410 T-003 / design D3: the followup handler no longer calls a
// live `ClientSideConnection.prompt`. It resolves the target through
// the persisted binding (the same source the Workflow path already
// uses) and dispatches the prompt through `OpenCodeRuntime.followup`.
// The handler's `FollowupTarget` shape is a Mohist-owned value object
// `{ runtimeSessionId, workDir, projectId }` — no live RPC surface is
// held by the runner host.
//
// Behaviour is byte-identical to the inline implementation:
//   - drops silently on null / missing payload, missing/empty text, no
//     resolver, no server connection, no runtime, resolver returning
//     null, resolver throwing (logged), runtime returning
//     `unavailable-runtime` (the existing unavailable taxonomy)
//   - branches on `target.kind` to pick the runtime-events endpoint:
//     workflow → `workflowAgentSessionRuntimeEvents`;
//     generic  → `agentSessionRuntimeEvents`
//   - emits a `session.input` runtime event tagged with
//     `kind: "followup" / role: "user" / source: "followup"` on the
//     resolved `runtimeSessionId` — non-awaited, rejection logged but
//     does NOT block the prompt
//   - calls `runtime.followup(...)` exactly once, fire-and-forget:
//     the handler returns before the prompt resolves, and a prompt
//     rejection is logged rather than thrown
//   - records the terminal outcome (`completed` / `failed`) via the
//     followup failure outbox when an `operationId` is supplied
//   - legacy top-level `workflowRunId` / `sessionName` fallback via
//     `resolveSessionTarget` (T-004) — an empty `projectId` still
//     routes to the workflow followup path

import * as signalR from "@microsoft/signalr"
import type { ServerConnection } from "./connection.js"
import {
  resolveSessionTarget,
  type FollowupTarget,
  type FollowupTargetResolution,
  type FollowupTargetResolver,
  type ReceiveFollowupPayload,
  isFollowupTargetUnavailable,
} from "./session-target.js"
import type { SessionTarget } from "../runtime/acp-connection.js"
import type { FollowupFailureOutboxStore } from "./followup-failure-outbox.js"
import type { OpenCodeRuntime, RuntimeFollowupRequest } from "../runtime/opencode/index.js"

export interface FollowupHandlerDeps {
  serverConnection?: ServerConnection | null
  followupTargetResolver?: FollowupTargetResolver | null
  followupFailureOutbox?: FollowupFailureOutboxStore | null
  openCodeRuntime?: OpenCodeRuntime | null
}

export interface FollowupDeliveryResult {
  accepted: boolean
  error?: "missing" | "unavailable"
}

export function registerFollowupHandler(
  conn: signalR.HubConnection,
  deps: FollowupHandlerDeps,
): void {
  conn.on("ReceiveFollowup", async (payload: ReceiveFollowupPayload | null | undefined) =>
    await handleFollowup(payload, deps))
}

async function handleFollowup(
  payload: ReceiveFollowupPayload | null | undefined,
  deps: FollowupHandlerDeps,
): Promise<FollowupDeliveryResult> {
  if (!payload || typeof payload.text !== "string" || payload.text.length === 0) return unavailable()
  const serverConnection = deps.serverConnection ?? null
  const resolver = deps.followupTargetResolver ?? null
  const runtime = deps.openCodeRuntime ?? null
  if (!resolver || !serverConnection || !runtime) return unavailable()

  if (!runtime.ready()) {
    return unavailable()
  }

  // Issue-129 T-004: branch on the discriminated `target.kind` so a
  // single handler can deliver followups to either a workflow-shaped
  // session or a generic (non-workflow) AgentSession. The
  // server-side payload always carries the unified `target` shape
  // (T-004 / D3); when the target is absent we fall back to the
  // legacy top-level workflowRunId / sessionName fields so older
  // server builds (no `target` field) keep working against the
  // workflow followup route.
  const sessionTarget = resolveSessionTarget(payload)
  if (!sessionTarget) return unavailable()

  let target: FollowupTargetResolution
  try {
    const resolved = resolver(sessionTarget)
    target = isPromise(resolved) ? await resolved : resolved
  } catch (error) {
    console.error("followup target resolver threw:", error)
    return unavailable()
  }
  if (isFollowupTargetUnavailable(target)) return unavailable()
  if (!target) return { accepted: false, error: "missing" }

  emitFollowupEvent(serverConnection, sessionTarget, target, {
    type: "session.input",
    payload: {
      role: "user",
      text: payload.text,
      kind: "followup",
      sentAt: new Date().toISOString(),
      ...(payload.operationId ? { operationId: payload.operationId } : {}),
      runtimeSessionId: target.runtimeSessionId,
      source: "followup",
    },
  })

  const followupRequest: RuntimeFollowupRequest = {
    target: {
      runtime: "opencode",
      runtimeSessionId: target.runtimeSessionId,
      workDir: target.workDir,
    },
    prompt: payload.text,
  }
  try {
    void runtime.followup(followupRequest).then(
      (result) => {
        if (!result.ok && result.error.kind === "unavailable-runtime") {
          console.error("followup runtime unavailable:", result.error.message)
          recordFollowupTerminal(deps.followupFailureOutbox ?? null, serverConnection, sessionTarget, target, payload.operationId, "failed", result.error.message)
          return
        }
        if (!result.ok && result.error.kind === "missing-session") {
          recordFollowupTerminal(deps.followupFailureOutbox ?? null, serverConnection, sessionTarget, target, payload.operationId, "failed", result.error.message)
          return
        }
        if (!result.ok) {
          console.error("followup runtime rejected:", result.error.message)
          recordFollowupTerminal(deps.followupFailureOutbox ?? null, serverConnection, sessionTarget, target, payload.operationId, "failed", result.error.message)
          return
        }
        recordFollowupTerminal(deps.followupFailureOutbox ?? null, serverConnection, sessionTarget, target, payload.operationId, "completed", null)
      },
      (error) => {
        console.error("followup runtime.followup rejected:", error instanceof Error ? error.message : String(error))
        recordFollowupTerminal(deps.followupFailureOutbox ?? null, serverConnection, sessionTarget, target, payload.operationId, "failed", error)
      },
    )
  } catch (error) {
    console.error("followup runtime.followup threw:", error instanceof Error ? error.message : String(error))
    recordFollowupTerminal(deps.followupFailureOutbox ?? null, serverConnection, sessionTarget, target, payload.operationId, "failed", error)
    return unavailable()
  }
  return { accepted: true }
}

function recordFollowupTerminal(
  outbox: FollowupFailureOutboxStore | null,
  serverConnection: ServerConnection,
  sessionTarget: SessionTarget,
  target: FollowupTarget,
  operationId: string | undefined,
  status: "completed" | "failed",
  error: unknown,
): void {
  const message = error === null ? null : error instanceof Error ? error.message : String(error)
  const completedAt = new Date().toISOString()
  if (!operationId) return
  if (outbox) {
    void outbox.record({
      operationId,
      target: sessionTarget,
      runtimeSessionId: target.runtimeSessionId,
      status,
      error: message,
      completedAt,
    }, serverConnection).catch((outboxError) => {
      console.error("failed to persist followup failure:", outboxError)
    })
    return
  }

  emitFollowupEvent(serverConnection, sessionTarget, target, {
    type: status === "failed" ? "session.followup_failed" : "session.followup_completed",
    payload: {
      status,
      ...(message ? { failureReason: message } : {}),
      source: "followup",
      operationId,
      runtimeSessionId: target.runtimeSessionId,
      completedAt,
    },
  })
}

// Emits a runtime event for a follow-up through the workflow or generic
// runtime-events endpoint, depending on the session kind. Fire-and-forget:
// a rejection is logged but does not change the handler result.
function emitFollowupEvent(
  serverConnection: ServerConnection,
  sessionTarget: SessionTarget,
  target: FollowupTarget,
  event: { type: string; payload: Record<string, unknown> },
): void {
  const runtimeEvents = [event]
  const signal = new AbortController().signal
  const onError = (error: unknown) => {
    console.error(`failed to emit followup ${event.type} event:`, error)
  }
  if (sessionTarget.kind === "workflow") {
    void serverConnection.workflowAgentSessionRuntimeEvents(
      target.projectId,
      sessionTarget.workflowRunId!,
      sessionTarget.sessionName!,
      { workId: null, workType: null, stage: null, runtimeSessionId: target.runtimeSessionId, runtimeEvents },
      signal,
    ).catch(onError)
  } else {
    void serverConnection.agentSessionRuntimeEvents(
      target.projectId,
      sessionTarget.sessionId,
      { workId: null, workType: null, stage: null, runtimeSessionId: target.runtimeSessionId, runtimeEvents },
      signal,
    ).catch(onError)
  }
}

function isPromise<T>(value: T | Promise<T>): value is Promise<T> {
  return typeof (value as Promise<T> | null)?.then === "function"
}

function unavailable(): FollowupDeliveryResult {
  return { accepted: false, error: "unavailable" }
}