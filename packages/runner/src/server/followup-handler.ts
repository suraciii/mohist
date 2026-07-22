// Issue-461 T-001 / design D1-D7 + issue-451 T-004 / design D2-D3:
// the follow-up handler routes both input and operation-correlated
// terminal outcomes through the host-owned
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
//   - dispatches to the binding's runtime (issue-451 T-004 / design D2):
//     the wire binding's `runtime` field selects between the OpenCode
//     and Pi backends; an unknown or not-ready runtime reports
//     `unavailable` and the command is not silently dropped
//   - enqueues a `session.input` record through `enqueueBeforeExecution`
//     before invoking `runtime.followup`; a local persistence failure
//     returns `unavailable` without invoking the runtime, so command
//     delivery can be retried
//   - invokes `runtime.followup` exactly once; the resolve/reject
//     handler enqueues the corresponding session.activity record
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
import {
  callFollowup,
  resolveCommandRuntime,
  type CommandRuntimeAccessors,
} from "./command-runtime.js"
import type { PiTurnObserver } from "../runtime/pi/index.js"

export interface FollowupHandlerDeps {
  followupTargetResolver?: FollowupTargetResolver | null
  agentSessionRuntimeEventOutbox?: AgentSessionRuntimeEventOutbox | null
  openCodeRuntime?: CommandRuntimeAccessors["openCode"]
  piRuntime?: CommandRuntimeAccessors["pi"]
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
  if (!resolver || !outbox) return unavailable()
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

  const binding = sessionTarget.binding
  if (!binding) return { accepted: false, error: "missing" }

  const handle = resolveCommandRuntime(binding, {
    openCode: deps.openCodeRuntime,
    pi: deps.piRuntime,
  })
  if (!handle) return unavailable()
  if (!handle.runtime.ready()) return unavailable()

  try {
    await enqueueFollowupInput(outbox, sessionTarget, target, payload, deps.randomId ?? defaultFollowupRecordId)
  } catch (error) {
    console.error("followup durable input enqueue failed:", error instanceof Error ? error.message : String(error))
    return unavailable()
  }

  const followupRequest = {
    target: { runtime: binding.runtime, runtimeSessionId: target.runtimeSessionId, workDir: target.workDir },
    prompt: payload.text,
  }
  const observer = buildFollowupObserver(outbox, sessionTarget, target, payload.operationId)
  try {
    void callFollowup(handle, followupRequest, observer).then(
      (result) => {
        if (!result.ok) {
          const message = readErrorMessage(result)
          recordFollowupActivity(outbox, sessionTarget, target, payload.operationId, "unknown", message)
          if (readErrorKind(result) === "unavailable-runtime") {
            console.error("followup runtime unavailable:", message)
          }
          return
        }
        recordFollowupActivity(outbox, sessionTarget, target, payload.operationId, "idle")
      },
      (error) => {
        console.error("followup runtime.followup rejected:", error instanceof Error ? error.message : String(error))
        recordFollowupActivity(outbox, sessionTarget, target, payload.operationId, "unknown", error)
      },
    )
  } catch (error) {
    console.error("followup runtime.followup threw:", error instanceof Error ? error.message : String(error))
    recordFollowupActivity(outbox, sessionTarget, target, payload.operationId, "unknown", error)
    return unavailable()
  }
  return { accepted: true }
}

function buildFollowupObserver(
  outbox: AgentSessionRuntimeEventOutbox,
  sessionTarget: SessionTarget,
  target: FollowupTarget,
  operationId: string | undefined,
): PiTurnObserver | null {
  if (!operationId) return null
  const completedAt = new Date().toISOString()
  return {
    onEvent: (event) => {
      const record: RuntimeEventRecord = {
        id: `followup-event:${operationId}:${event.id}`,
        producerFamily: sessionTarget.kind === "workflow" ? "workflow-session" : "generic-followup",
        target: sessionTargetToRuntimeTarget(sessionTarget),
        runtimeSessionId: target.runtimeSessionId,
        work: null,
        event: {
          type: event.type,
          payload: { ...event.payload, source: "followup", operationId, runtimeSessionId: target.runtimeSessionId, completedAt },
        },
        acknowledgementPolicy: "successful-response",
      }
      outbox.enqueueProducedFact(record).catch((outboxError) => {
        console.error("failed to persist followup runtime event:", outboxError)
      })
    },
  }
}

function readErrorMessage(result: { readonly error?: { readonly message?: string } }): string {
  return result.error?.message ?? "followup runtime error"
}

function readErrorKind(result: { readonly error?: { readonly kind?: string } }): string {
  return result.error?.kind ?? ""
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

function recordFollowupActivity(
  outbox: AgentSessionRuntimeEventOutbox,
  sessionTarget: SessionTarget,
  target: FollowupTarget,
  operationId: string | undefined,
  activity: "idle" | "unknown",
  error?: unknown,
): void {
  if (!operationId) return
  const completedAt = new Date().toISOString()
  const record: RuntimeEventRecord = {
    id: `followup-activity:${operationId}:${activity}:${completedAt}`,
    producerFamily: sessionTarget.kind === "workflow" ? "workflow-session" : "generic-followup",
    target: sessionTargetToRuntimeTarget(sessionTarget),
    runtimeSessionId: target.runtimeSessionId,
    work: null,
    event: {
      type: "session.activity",
      payload: {
        activity,
        status: activity === "idle" ? "completed" : "failed",
        ...(error ? { failureReason: error instanceof Error ? error.message : errorMessage(error) } : {}),
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

function isPromise<T>(value: T | Promise<T>): value is Promise<T> {
  return typeof (value as Promise<T> | null)?.then === "function"
}

function unavailable(): FollowupDeliveryResult {
  return { accepted: false, error: "unavailable" }
}
