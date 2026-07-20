// Issue-313 T-008 / design P7 / D2 / D3 / D5: the server-invoked
// `CancelAgentSession` SignalR method is extracted from `runner-signalr.ts`
// into a free-function `registerCancelHandler(conn, deps)` so the
// cluster's dependency surface is explicit (D3) and so the cancel reply
// path can be exercised independently of the other push handlers.
//
// Issue-410 T-003 / design D3: the cancel handler no longer calls
// `ClientSideConnection.cancel?.bind(connection)`. It resolves the
// target through the persisted binding (the same source the Workflow
// path already uses) and forwards the cancel to
// `OpenCodeRuntime.cancel`. The handler's `FollowupTarget` shape is a
// Mohist-owned value object `{ runtimeSessionId, workDir, projectId }`
// — no live RPC surface is held by the runner host.
//
// Behaviour:
//   - returns `{ state: "not-cancellable" }` for: null/missing payload,
//     missing or malformed `target`, no registered resolver, no runtime,
//     runtime not ready, resolver returning null, resolver throwing
//     (logged), runtime returning `not-cancellable`
//   - returns `{ state: "cancelled" }` only when the runtime's
//     `OpenCodeRuntime.cancel` resolves with a `cancelled: true` fact
//   - returns `{ state: "unavailable" }` when the runtime is
//     initializing (`FOLLOWUP_TARGET_UNAVAILABLE`) or the runtime
//     itself reports `unavailable-runtime`
//   - the same `followupTargetResolver` is shared with
//     `RegisterFollowupHandler` (both surface resolve through the
//     persisted binding)

import * as signalR from "@microsoft/signalr"
import {
  sessionTargetFromWireTarget,
  type CancelAgentSessionPayload,
  type CancelAgentSessionReply,
  type FollowupTargetResolution,
  type FollowupTargetResolver,
  isFollowupTargetUnavailable,
} from "./session-target.js"
import type { OpenCodeRuntime } from "../runtime/opencode/index.js"

export interface CancelHandlerDeps {
  followupTargetResolver?: FollowupTargetResolver | null
  openCodeRuntime?: OpenCodeRuntime | null
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
//   - `cancelled` — a persisted Runtime Session binding exists for
//     the target AND the OpenCode runtime reports `cancelled: true`.
//     The runtime's `client.session.abort` is fire-and-forget at the
//     SDK layer (resolves once the abort is on the wire, not when
//     the agent honours it). Whether the agent honours the cancel is
//     the agent's decision; the runner is honest about the attempt.
//   - `not-cancellable` — there is no persisted binding for this
//     target, the binding is for a non-OpenCode runtime, OR the
//     runtime reports a `missing-session` / `turn-failed` outcome.
//     There is nothing to cancel.
//   - `unavailable` — the OpenCode runtime is initializing (`ready()`
//     false) or `OpenCodeRuntime.cancel` returns `unavailable-runtime`.
//
// The server already short-circuits terminal sessions before invoking
// the runner (T-005 / design D6), so a `terminal-state` reply from
// the runner is rare but reserved (e.g. for a race window where the
// agent reports the session as terminal in the same instant we sent
// the cancel). The handler does not invent terminal states — the
// server is the source of truth.
async function handleCancel(
  payload: CancelAgentSessionPayload | null | undefined,
  deps: CancelHandlerDeps,
): Promise<CancelAgentSessionReply> {
  if (!payload || !payload.target) {
    return { state: "not-cancellable" }
  }

  const sessionTarget = sessionTargetFromWireTarget(payload.target)
  if (!sessionTarget) return { state: "not-cancellable" }

  const resolver = deps.followupTargetResolver ?? null
  const runtime = deps.openCodeRuntime ?? null
  if (!resolver || !runtime) {
    return { state: "not-cancellable" }
  }
  if (!runtime.ready()) {
    return { state: "unavailable" }
  }

  let resolved: FollowupTargetResolution
  try {
    resolved = await resolver(sessionTarget)
  } catch (error) {
    console.error("cancel target resolver threw:", error)
    return { state: "not-cancellable" }
  }

  if (isFollowupTargetUnavailable(resolved)) return { state: "unavailable" }

  if (!resolved) {
    // No persisted binding for this target. There is nothing to
    // cancel — the API must report that honestly.
    return { state: "not-cancellable" }
  }

  try {
    const result = await runtime.cancel({
      target: {
        runtime: "opencode",
        runtimeSessionId: resolved.runtimeSessionId,
        workDir: resolved.workDir,
      },
    })
    if (!result.ok) {
      if (result.error.kind === "unavailable-runtime") {
        return { state: "unavailable" }
      }
      if (result.error.kind === "missing-session") {
        return { state: "not-cancellable" }
      }
      console.error("cancel runtime.cancel rejected:", result.error.message)
      return { state: "not-cancellable" }
    }
    if (!result.value.facts.cancelled) {
      return { state: "not-cancellable" }
    }
    return { state: "cancelled" }
  } catch (error) {
    console.error("cancel runtime.cancel threw:", error instanceof Error ? error.message : String(error))
    return { state: "not-cancellable" }
  }
}