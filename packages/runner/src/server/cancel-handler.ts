// Issue-461 T-001 / design D1 + D7: the cancel handler does NOT
// consult outbox health — it is the one SignalR operation that must
// remain available while the durable snapshot is being recovered.
// It captures the runtime via the host-owned invocation-time accessor
// at command time (a runtime initialized or replaced after SignalR
// client construction is therefore visible) and resolves the binding
// through the binding-only `followupTargetResolver`.

import * as signalR from "@microsoft/signalr"
import {
  sessionTargetFromWireTarget,
  type CancelAgentSessionPayload,
  type CancelAgentSessionReply,
  type FollowupTargetResolution,
  type FollowupTargetResolver,
} from "./session-target.js"
import type { OpenCodeRuntime } from "../runtime/opencode/index.js"

export interface CancelHandlerDeps {
  followupTargetResolver?: FollowupTargetResolver | null
  openCodeRuntime?: OpenCodeRuntime | (() => OpenCodeRuntime | null) | null
}

export function registerCancelHandler(
  conn: signalR.HubConnection,
  deps: CancelHandlerDeps,
): void {
  conn.on("CancelAgentSession", async (payload: CancelAgentSessionPayload | null | undefined) => {
    return await handleCancel(payload, deps)
  })
}

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
  const runtimeAccessor = deps.openCodeRuntime ?? null
  if (!resolver || !runtimeAccessor) {
    return { state: "not-cancellable" }
  }
  const runtime = resolveRuntime(runtimeAccessor)
  if (!runtime) return { state: "not-cancellable" }
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

  if (!resolved) {
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

function resolveRuntime(accessor: OpenCodeRuntime | (() => OpenCodeRuntime | null)): OpenCodeRuntime | null {
  if (typeof accessor === "function") return accessor()
  return accessor
}