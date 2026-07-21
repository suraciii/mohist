// Issue-461 T-001 / design D1-D7: the follow-up handler routes both
// input and operation-correlated terminal outcomes through the host-owned
// `AgentSessionRuntimeEventOutbox` instead of `FollowupFailureOutbox`.
//
// Behaviour:
//   - drops silently on null / missing payload, missing/empty text, no
//     resolver, resolver returning null, resolver throwing (logged)
//   - resolves the runtime accessor at invocation time (issue-461 D1:
//     host-owned late binding), so a runtime built or replaced after
//     SignalR client construction is visible to later commands
//   - admits a follow-up command only when (a) the binding resolves and
//     (b) the captured runtime is ready and (c) the outbox is healthy;
//     otherwise returns `{ accepted: false, error: "unavailable" }`
//     without enqueuing input or invoking the runtime
//   - enqueues a `session.input` record through `enqueueBeforeExecution`
//     before invoking `runtime.followup`; a local persistence failure
//     returns `unavailable` without invoking the runtime, so command
//     delivery can be retried
//   - invokes `runtime.followup` exactly once; the resolve/reject
//     handler enqueues the corresponding `session.followup_completed`
//     or `session.followup_failed` record (operation-correlated)
//   - server upload failure does NOT change the accepted result and
//     does NOT re-invoke the prompt — the durable record is now under
//     the outbox's retry/recovery policy

import * as signalR from "@microsoft/signalr"
import { errorMessage } from "../core/errors.js"
import {
  resolveSessionTarget,
  type FollowupTarget,
  type FollowupTargetResolution,
  type FollowupTargetResolver,
  type ReceiveFollowupPayload,
  type SessionTarget,
} from "./session-target.js"
import type { AgentSessionRuntimeEventOutbox, RuntimeEventRecord } from "./runtime-event-outbox.js"
import type { OpenCodeRuntime, RuntimeFollowupRequest } from "../runtime/opencode/index.js"

export interface FollowupHandlerDeps {
  followupTargetResolver?: FollowupTargetResolver | null
  agentSessionRuntimeEventOutbox?: AgentSessionRuntimeEventOutbox | null
  openCodeRuntime?: OpenCodeRuntime | (() => OpenCodeRuntime | null) | null
  randomId?: () => string
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

export function defaultFollowupRecordId(): string {
  return `fup_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 10)}`
}

async function handleFollowup(
  payload: ReceiveFollowupPayload | null | undefined,
  deps: FollowupHandlerDeps,
): Promise<FollowupDeliveryResult> {
  if (!payload || typeof payload.text !== "string" || payload.text.length === 0) return unavailable()
  const resolver = deps.followupTargetResolver ?? null
  const outbox = deps.agentSessionRuntimeEventOutbox ?? null
  const runtimeAccessor = deps.openCodeRuntime ?? null
  if (!resolver || !outbox || !runtimeAccessor) return unavailable()
  if (!outbox.ready()) return unavailable()

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
  if (!target) return { accepted: false, error: "missing" }

  const runtime = resolveRuntime(runtimeAccessor)
  if (!runtime || !runtime.ready()) return unavailable()

  try {
    await enqueueFollowupInput(outbox, sessionTarget, target, payload, deps.randomId ?? defaultFollowupRecordId)
  } catch (error) {
    console.error("followup durable input enqueue failed:", error instanceof Error ? error.message : String(error))
    return unavailable()
  }

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
        if (!result.ok) {
          recordFollowupTerminal(outbox, sessionTarget, target, payload.operationId, "failed", result.error.message)
          if (result.error.kind === "unavailable-runtime") {
            console.error("followup runtime unavailable:", result.error.message)
          }
          return
        }
        recordFollowupTerminal(outbox, sessionTarget, target, payload.operationId, "completed", null)
      },
      (error) => {
        console.error("followup runtime.followup rejected:", error instanceof Error ? error.message : String(error))
        recordFollowupTerminal(outbox, sessionTarget, target, payload.operationId, "failed", error)
      },
    )
  } catch (error) {
    console.error("followup runtime.followup threw:", error instanceof Error ? error.message : String(error))
    recordFollowupTerminal(outbox, sessionTarget, target, payload.operationId, "failed", error)
    return unavailable()
  }
  return { accepted: true }
}

async function enqueueFollowupInput(
  outbox: AgentSessionRuntimeEventOutbox,
  sessionTarget: SessionTarget,
  target: FollowupTarget,
  payload: ReceiveFollowupPayload,
  randomId: () => string,
): Promise<void> {
  const record: RuntimeEventRecord = {
    id: randomId(),
    producerFamily: sessionTarget.kind === "workflow" ? "workflow-session" : "generic-followup",
    target: sessionTargetToRuntimeTarget(sessionTarget),
    runtimeSessionId: target.runtimeSessionId,
    work: null,
    event: {
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
    },
    acknowledgementPolicy: "matching-receipt",
  }
  await outbox.enqueueBeforeExecution(record)
}

function recordFollowupTerminal(
  outbox: AgentSessionRuntimeEventOutbox,
  sessionTarget: SessionTarget,
  target: FollowupTarget,
  operationId: string | undefined,
  status: "completed" | "failed",
  error: unknown,
): void {
  if (!operationId) return
  const message = error === null ? null : error instanceof Error ? error.message : errorMessage(error)
  const completedAt = new Date().toISOString()
  const record: RuntimeEventRecord = {
    id: `followup-terminal:${operationId}:${status}:${completedAt}`,
    producerFamily: sessionTarget.kind === "workflow" ? "workflow-session" : "generic-followup",
    target: sessionTargetToRuntimeTarget(sessionTarget),
    runtimeSessionId: target.runtimeSessionId,
    work: null,
    event: {
      type: status === "failed" ? "session.followup_failed" : "session.followup_completed",
      payload: {
        status,
        ...(message ? { failureReason: message } : {}),
        source: "followup",
        operationId,
        runtimeSessionId: target.runtimeSessionId,
        completedAt,
      },
    },
    acknowledgementPolicy: "successful-response",
  }
  outbox.enqueueProducedFact(record).catch((outboxError) => {
    console.error("failed to persist followup terminal:", outboxError)
  })
}

function sessionTargetToRuntimeTarget(target: SessionTarget): RuntimeEventRecord["target"] {
  if (target.kind === "workflow") {
    return { kind: "workflow", projectId: target.projectId, workflowRunId: target.workflowRunId, sessionName: target.sessionName }
  }
  return { kind: "generic", projectId: target.projectId, sessionId: target.sessionId }
}

function resolveRuntime(accessor: OpenCodeRuntime | (() => OpenCodeRuntime | null)): OpenCodeRuntime | null {
  if (typeof accessor === "function") return accessor()
  return accessor
}

function isPromise<T>(value: T | Promise<T>): value is Promise<T> {
  return typeof (value as Promise<T> | null)?.then === "function"
}

function unavailable(): FollowupDeliveryResult {
  return { accepted: false, error: "unavailable" }
}