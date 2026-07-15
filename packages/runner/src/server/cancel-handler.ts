// Issue-313 T-008 / design P7 / D2 / D3 / D5: the server-invoked
// `CancelAgentSession` SignalR method is extracted from `runner-signalr.ts`
// into a free-function `registerCancelHandler(conn, deps)` so the
// cluster's dependency surface is explicit (D3) and so the cancel reply
// path can be exercised independently of the other push handlers.
//
// Behaviour:
//   - returns `{ state: "not-cancellable" }` for: null/missing payload,
//     missing or malformed `target`, no registered resolver, resolver returning null,
//     resolver throwing (logged), connection without `cancel`, cancel
//     send rejection (logged)
//   - returns `{ state: "cancelled" }` only when:
//       (1) the resolver hits (returns a non-null `FollowupTarget`),
//       (2) the resolved connection exposes a `cancel` method, AND
//       (3) `cancel({ sessionId })` resolves without throwing
//   - the same `followupTargetResolver` is shared with
//     `RegisterFollowupHandler` (the cancel surface looks up the same
//     live ACP session the followup surface delivers to). The deps
//     therefore only need the resolver — no `serverConnection`
//     dependency, since the cancel reply path is server-less.

import * as signalR from "@microsoft/signalr"
import type { SessionTarget } from "../runtime/acp-connection.js"
import type { CancelAgentSessionPayload, CancelAgentSessionReply, FollowupTarget, FollowupTargetResolver } from "./session-target.js"

export interface CancelHandlerDeps {
  followupTargetResolver?: FollowupTargetResolver | null
}

export function registerCancelHandler(
  conn: signalR.HubConnection,
  deps: CancelHandlerDeps,
): void {
  conn.on("CancelAgentSession", async (payload: CancelAgentSessionPayload | null | undefined) => {
    return await handleCancel(payload, deps)
  })
}

// Server-invoked cancel (issue-129 T-005 / design D6). The server
// pushes a `CancelAgentSession` SignalR invocation carrying a
// `SessionTarget` and expects a `{ state: ... }` reply that the HTTP
// endpoint mirrors verbatim. The handler branches on the same
// `target.kind` discriminator introduced in T-004 (workflow vs generic).
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
async function handleCancel(
  payload: CancelAgentSessionPayload | null | undefined,
  deps: CancelHandlerDeps,
): Promise<CancelAgentSessionReply> {
  if (!payload || !payload.target) {
    return { state: "not-cancellable" }
  }

  const target = payload.target
  const sessionTarget: SessionTarget | null = target.kind === "workflow"
    && target.workflowRunId
    && target.sessionName
    ? {
        kind: "workflow",
        projectId: target.projectId ?? "",
        workflowRunId: target.workflowRunId,
        sessionName: target.sessionName,
      }
    : target.kind === "generic" && target.sessionId
      ? {
          kind: "generic",
          projectId: target.projectId ?? "",
          sessionId: target.sessionId,
        }
      : null
  if (!sessionTarget || !sessionTarget.projectId) return { state: "not-cancellable" }

  const resolver = deps.followupTargetResolver ?? null
  if (!resolver) {
    return { state: "not-cancellable" }
  }

  let resolved: FollowupTarget | null
  try {
    resolved = resolver(sessionTarget)
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
