// The cancel handler does NOT consult outbox health — it is the one
// SignalR operation that must remain available while the durable
// snapshot is being recovered. It captures the runtime via the host-owned
// invocation-time accessor at command time (a runtime initialized or
// replaced after SignalR client construction is therefore visible) and
// resolves the binding through the binding-only `followupTargetResolver`.
//
// The cancel reply carries `interruptUnconfirmed` whenever the bound
// runtime reports a stop it could not confirm. The flag is surfaced end-to-end so the API/user is never told a
// still-running turn has been safely stopped. OpenCode replies never
// carry the flag because the OpenCode abort is authoritative (no
// `stopConfirmed` field on the result); Pi's `cancel` reports
// `stopConfirmed: false` exactly when the upper layers must surface
// `interruptUnconfirmed: true`.
//
// When a Cancel is confirmed, the handler
// enqueues a binding-guarded `session.activity` fact through the host
// runtime-event outbox so the grain's `ApplyRuntimeEventToDomain` →
// `ParseActivity` path settles activity: confirmed → `idle`, unconfirmed
// → `unknown` (an unconfirmed stop must never be reported as `idle`).
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
  ensureCommandRuntimeReady,
  readCancelFacts,
  resolveCommandRuntime,
  type CancelCallTarget,
  type CommandRuntimeAccessors,
} from "./command-runtime.js"
import type {
  AgentSessionRuntimeEventOutbox,
  RuntimeEventRecord,
} from "./runtime-event-outbox.js"
import type { CancelOperationJournalStore } from "../runtime/cancel-operation-journal.js"
import { runnerLogger } from "../system/logger.js"

const log = runnerLogger.child("session")

export interface CancelHandlerDeps {
  followupTargetResolver?: FollowupTargetResolver | null
  openCodeRuntime?: CommandRuntimeAccessors["openCode"]
  piRuntime?: CommandRuntimeAccessors["pi"]
  agentSessionRuntimeEventOutbox?: AgentSessionRuntimeEventOutbox | null
  cancelOperationJournal?: CancelOperationJournalStore | null
}

export function registerCancelHandler(
  conn: signalR.HubConnection,
  deps: CancelHandlerDeps,
): void {
  const inFlight = new Map<string, Promise<CancelAgentSessionReply>>()
  conn.on("CancelAgentSession", async (payload: CancelAgentSessionPayload | null | undefined) => {
    const key = operationKey(payload)
    if (!key) return await handleCancel(payload, deps)
    const existing = inFlight.get(key)
    if (existing) return await existing
    const operation = handleJournaledCancel(payload!, deps)
    inFlight.set(key, operation)
    try {
      return await operation
    } finally {
      if (inFlight.get(key) === operation) inFlight.delete(key)
    }
  })
}

async function handleJournaledCancel(
  payload: CancelAgentSessionPayload,
  deps: CancelHandlerDeps,
): Promise<CancelAgentSessionReply> {
  const journal = deps.cancelOperationJournal ?? null
  const sessionId = payload.sessionId ?? ""
  const operationId = payload.operationId ?? ""
  const outbox = deps.agentSessionRuntimeEventOutbox ?? null
  if (!journal || !sessionId || !operationId || !outbox?.ready()) return { state: "unavailable" }

  try {
    const existing = await journal.get(sessionId, operationId)
    if (existing) {
      if (!samePayload(existing.request, payload)) return { state: "unavailable" }
      if (existing.state === "completed") return existing.reply!
    } else {
      await journal.start(sessionId, payload)
    }
    const reply = await handleCancel(payload, deps)
    if (reply.state === "stop-requested" || reply.state === "unavailable") return reply
    await journal.complete(sessionId, payload, reply)
    return reply
  } catch (error) {
    log.error("cancel operation journal failed", { exception: error, session: "cancel" })
    return { state: "unavailable" }
  }
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
  if (!await ensureCommandRuntimeReady(handle)) {
    return { state: "unavailable" }
  }

  let resolved: FollowupTargetResolution
  try {
    resolved = await resolver(sessionTarget)
  } catch (error) {
    log.error("cancel target resolver threw", { exception: error })
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
      log.error("cancel runtime.cancel rejected", { reason: readErrorMessage(result), session: binding.runtimeSessionId })
      return { state: "stop-requested" }
    }
    const facts = readCancelFacts(result)
    if (!facts || !facts.cancelled) {
      return { state: "not-cancellable" }
    }
    const confirmed = handle.kind === "opencode" || facts.stopConfirmed === true
    try {
      await recordCancelActivity(
        deps.agentSessionRuntimeEventOutbox ?? null,
        sessionTarget,
        binding.runtimeSessionId,
        payload.turnId,
        payload.operationId,
        { ...facts, stopConfirmed: confirmed },
      )
    } catch (outboxError) {
      log.error("failed to persist cancel activity", { session: binding.runtimeSessionId, exception: outboxError })
      return { state: "stop-requested" }
    }
    return facts.stopConfirmed === false
      ? { state: "unknown", interruptUnconfirmed: true }
      : confirmed
        ? { state: "stopped" }
        : { state: "stop-requested" }
  } catch (error) {
    log.error("cancel runtime.cancel threw", { exception: error, session: binding.runtimeSessionId })
    return { state: "stop-requested" }
  }
}

async function recordCancelActivity(
  outbox: AgentSessionRuntimeEventOutbox | null,
  sessionTarget: SessionTarget,
  runtimeSessionId: string,
  turnId: string | undefined,
  operationId: string | undefined,
  facts: { readonly cancelled: boolean; readonly stopConfirmed: boolean },
): Promise<void> {
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
        ...(turnId ? { turnId } : {}),
        ...(operationId ? { stopOperationId: operationId } : {}),
        stopConfirmed: facts.stopConfirmed,
        runtimeSessionId,
        completedAt,
      },
    },
    acknowledgementPolicy: "successful-response",
  }
  await outbox.enqueueProducedFact(record)
}

function operationKey(payload: CancelAgentSessionPayload | null | undefined): string | null {
  return payload?.sessionId && payload.operationId ? `${payload.sessionId}:${payload.operationId}` : null
}

function samePayload(left: CancelAgentSessionPayload, right: CancelAgentSessionPayload): boolean {
  return left.sessionId === right.sessionId
    && left.operationId === right.operationId
    && left.turnId === right.turnId
    && JSON.stringify(left.target) === JSON.stringify(right.target)
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
