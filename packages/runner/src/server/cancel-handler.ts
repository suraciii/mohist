// Issue-461 T-001 / design D1 + D7 + issue-451 T-004 / design D2 + D6:
// the cancel handler does NOT consult outbox health — it is the one
// SignalR operation that must remain available while the durable
// snapshot is being recovered. It captures the runtime via the host-owned
// invocation-time accessor at command time (a runtime initialized or
// replaced after SignalR client construction is therefore visible) and
// resolves the binding through the binding-only `followupTargetResolver`.
//
// The cancel reply carries `interruptUnconfirmed` whenever the bound
// runtime reports a stop it could not confirm (issue-451 T-004 / design
// D6). The flag is surfaced end-to-end so the API/user is never told a
// still-running turn has been safely stopped. OpenCode replies never
// carry the flag because the OpenCode abort is authoritative (no
// `stopConfirmed` field on the result); Pi's `cancel` reports
// `stopConfirmed: false` exactly when the upper layers must surface
// `interruptUnconfirmed: true`.
//
// Issue-492 T-002 / design D5: when a Cancel is confirmed, the handler
// enqueues a binding-guarded `session.activity` fact through the host
// runtime-event outbox so the grain's `ApplyRuntimeEventToDomain` →
// `ParseActivity` path settles activity: confirmed → `idle`, unconfirmed
// → `unknown` (the spec forbids reporting an unconfirmed stop as `idle`).
// The grain's `AppendEventsAsync(..., requireCurrentRuntimeBinding: true)`
// discards the fact if the binding has been superseded by a concurrent
// Reset / recovery. The outbox is best-effort: if it is null or unhealthy
// the cancel reply still flows to the caller, because cancel must remain
// available while the durable snapshot is being recovered.

import * as signalR from "@microsoft/signalr"
import {
  sessionTargetFromWireTarget,
  type CancelAgentSessionPayload,
  type CancelAgentSessionReply,
  type FollowupTargetResolution,
  type FollowupTargetResolver,
  type SessionTarget,
} from "./session-target.js"
import {
  callCancel,
  readCancelFacts,
  resolveCommandRuntime,
  type CancelCallTarget,
  type CommandRuntimeAccessors,
} from "./command-runtime.js"
import type {
  AgentSessionRuntimeEventOutbox,
  RuntimeEventRecord,
} from "./runtime-event-outbox.js"

export interface CancelHandlerDeps {
  followupTargetResolver?: FollowupTargetResolver | null
  openCodeRuntime?: CommandRuntimeAccessors["openCode"]
  piRuntime?: CommandRuntimeAccessors["pi"]
  agentSessionRuntimeEventOutbox?: AgentSessionRuntimeEventOutbox | null
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
  const binding = sessionTarget.binding
  if (!binding) return { state: "not-cancellable" }

  const resolver = deps.followupTargetResolver ?? null
  if (!resolver) {
    return { state: "not-cancellable" }
  }
  const handle = resolveCommandRuntime(binding, {
    openCode: deps.openCodeRuntime,
    pi: deps.piRuntime,
  })
  if (!handle) return { state: "not-cancellable" }
  if (!handle.runtime.ready()) {
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
    const workDir = binding.workDir
    if (!workDir) return { state: "not-cancellable" }
    const cancelTarget: CancelCallTarget = {
      runtime: binding.runtime,
      runtimeSessionId: binding.runtimeSessionId,
      workDir,
    }
    const result = await callCancel(handle, cancelTarget)
    if (!result.ok) {
      const kind = readErrorKind(result)
      if (kind === "unavailable-runtime") {
        return { state: "unavailable" }
      }
      if (kind === "missing-session") {
        return { state: "not-cancellable" }
      }
      console.error("cancel runtime.cancel rejected:", readErrorMessage(result))
      return { state: "not-cancellable" }
    }
    const facts = readCancelFacts(result)
    if (!facts || !facts.cancelled) {
      return { state: "not-cancellable" }
    }
    recordCancelActivity(
      deps.agentSessionRuntimeEventOutbox ?? null,
      sessionTarget,
      binding.runtimeSessionId,
      facts,
    )
    return facts.stopConfirmed === false
      ? { state: "cancelled", interruptUnconfirmed: true }
      : { state: "cancelled" }
  } catch (error) {
    console.error("cancel runtime.cancel threw:", error instanceof Error ? error.message : String(error))
    return { state: "not-cancellable" }
  }
}

function recordCancelActivity(
  outbox: AgentSessionRuntimeEventOutbox | null,
  sessionTarget: SessionTarget,
  runtimeSessionId: string,
  facts: { readonly cancelled: boolean; readonly stopConfirmed: boolean },
): void {
  if (!outbox) return
  const activity = facts.stopConfirmed ? "idle" : "unknown"
  const completedAt = new Date().toISOString()
  const record: RuntimeEventRecord = {
    id: `cancel-activity:${runtimeSessionId}:${activity}:${completedAt}:${Math.random().toString(36).slice(2, 10)}`,
    producerFamily: sessionTarget.kind === "workflow" ? "workflow-session" : "generic-followup",
    target: sessionTargetToRuntimeTarget(sessionTarget),
    runtimeSessionId,
    work: null,
    event: {
      type: "session.activity",
      payload: {
        activity,
        status: facts.stopConfirmed ? "completed" : "failed",
        source: "cancel",
        stopConfirmed: facts.stopConfirmed,
        runtimeSessionId,
        completedAt,
      },
    },
    acknowledgementPolicy: "successful-response",
  }
  outbox.enqueueProducedFact(record).catch((outboxError) => {
    console.error("failed to persist cancel activity:", outboxError)
  })
}

function sessionTargetToRuntimeTarget(target: SessionTarget): RuntimeEventRecord["target"] {
  if (target.kind === "workflow") {
    return { kind: "workflow", projectId: target.projectId, workflowRunId: target.workflowRunId, sessionName: target.sessionName }
  }
  return { kind: "generic", projectId: target.projectId, sessionId: target.sessionId }
}

function readErrorKind(result: { readonly error?: { readonly kind?: string } }): string {
  return result.error?.kind ?? ""
}

function readErrorMessage(result: { readonly error?: { readonly message?: string } }): string {
  return result.error?.message ?? "runtime error"
}
