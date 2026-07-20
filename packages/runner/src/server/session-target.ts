// Session-target resolution and the wire/target types shared by the
// server→runner push handlers (`ReceiveFollowup`, `CancelAgentSession`,
// `ReceiveWorkflowRunStatus`).
//
// Extracted from `runner-signalr.ts` as part of issue-313 / design P3 so the
// pure resolver can be unit-tested directly (it was previously defined next
// to the transport plumbing and exercised only indirectly). Behaviour is
// byte-identical to the inline implementation — see acceptance criteria
// for T-004 in `openspec/changes/issue-313/tasks.json` and the spec
// scenarios in `specs/runner-signalr-push-handlers/spec.md` ("Session
// target resolution discriminates on target.kind with legacy fallback").
//
// Issue-410 T-003 / design D3: the `FollowupTarget` carried by the
// handlers is no longer a live `ClientSideConnection` — it is a Mohist-owned
// value object `{ runtimeSessionId, workDir, projectId }` resolved from
// the persisted binding (the same source the Workflow path already uses).
// The handlers pass the value object to `OpenCodeRuntime.followup` /
// `OpenCodeRuntime.cancel`. The module has no live connection lifecycle or
// per-session reconnect path.
//
// Issue-410 T-004: the `RuntimeSessionBinding` and `SessionTarget` types
// are now defined here. The wire shape is unchanged; only the connection
// lifecycle went away.

/**
 * Persisted runtime-session binding carried on the wire target by
 * `ReceiveFollowup` / `CancelAgentSession`. Mirrors the server-side
 * `RuntimeSessionBinding` record (issue-407).
 */
export interface RuntimeSessionBinding {
  runtime: string
  runtimeSessionId: string
  runnerId: string
  workDir: string | null
}

export type SessionTarget =
  | { kind: "workflow"; projectId: string; workflowRunId: string; sessionName: string; binding?: RuntimeSessionBinding }
  | { kind: "generic"; projectId: string; sessionId: string; binding?: RuntimeSessionBinding }

/**
 * The resolver's return value. A pure Mohist-owned value object:
 * `runtimeSessionId` + `workDir` come from the persisted AgentSession
 * binding the server already carries on the wire target; `projectId`
 * is the AgentSession's owning project. No live RPC surface is held
 * here — the handler consumes the value object and forwards it to
 * `OpenCodeRuntime.followup` / `OpenCodeRuntime.cancel`.
 */
export interface FollowupTarget {
  readonly runtimeSessionId: string
  readonly workDir: string
  readonly projectId: string
}

export interface FollowupTargetUnavailable {
  readonly unavailable: true
}

export const FOLLOWUP_TARGET_UNAVAILABLE: FollowupTargetUnavailable = { unavailable: true }

/**
 * The runner-side resolver turns a discriminated `SessionTarget`
 * (issue-129 T-004) into a `FollowupTarget` constructed from the
 * persisted binding, or `null` when no usable binding is registered.
 * `FOLLOWUP_TARGET_UNAVAILABLE` is returned when the runtime is
 * initializing (the OpenCode runtime is not yet ready / catalog not
 * loaded) so the handler can return the existing `unavailable`
 * taxonomy without consulting a live connection.
 *
 * Both `ReceiveFollowup` and `CancelAgentSession` call into this
 * resolver; a single registration keeps the wire-decoding logic in
 * one place.
 */
export type FollowupTargetResolution = FollowupTarget | FollowupTargetUnavailable | null

export type FollowupTargetResolver = (target: SessionTarget) => FollowupTargetResolution | Promise<FollowupTargetResolution>

export function isFollowupTargetUnavailable(value: FollowupTargetResolution): value is FollowupTargetUnavailable {
  return value !== null && "unavailable" in value
}

/**
 * Discriminated session target carried in the unified
 * `ReceiveFollowup` SignalR payload (issue-129 T-004). The runner
 * branches on `kind` to pick the right runtime-events endpoint
 * (`workflow:` / `generic:`, T-002) and the right server-side
 * runtime endpoint. Older runners that only know workflow followups
 * can keep reading the top-level `workflowRunId` / `sessionName`
 * fields the server still populates for the issue-scoped route.
 *
 * The `binding` field carries the persisted AgentSession binding
 * (the same source the Workflow path already uses). Issue-410 T-003
 * promotes `binding` from a resume path input to the resolver's
 * authoritative source: the resolver reads `binding.runtimeSessionId`
 * + `binding.workDir` and projects them into a `FollowupTarget`.
 * A legacy binding whose runtime is not `opencode` is treated as
 * missing — the resolver returns `null` and the handler fails with
 * the existing missing taxonomy + Reset hint.
 */
export interface ReceiveFollowupSessionTarget {
  kind: "workflow" | "generic"
  projectId: string
  workflowRunId?: string
  sessionName?: string
  sessionId?: string
  binding?: Partial<RuntimeSessionBinding>
}

/**
 * Unified payload delivered by the server-side `ReceiveFollowup` SignalR
 * method. Workflow followups continue to populate the top-level
 * `workflowRunId` / `sessionName` fields so older runners keep working;
 * generic followups carry `target.kind === "generic"` and a `sessionId`
 * instead. The `text` field is always present and non-empty (the server
 * rejects empty / whitespace text with 400 before pushing).
 */
export interface ReceiveFollowupPayload {
  workflowRunId?: string
  sessionName?: string
  target?: ReceiveFollowupSessionTarget
  text: string
  operationId?: string
}

// Payload delivered by the server-side `ReceiveWorkflowRunStatus` SignalR
// method when a workflow run reaches a terminal state. The status string
// is the canonical WorkflowRunStatus enum name (`Completed`, `Stopped`,
// `Failed` for terminal; non-terminal statuses are not delivered by the
// router — see RunnerWorkflowStatusRouter).
export interface ReceiveWorkflowRunStatusPayload {
  workflowRunId: string
  status: string
}

/**
 * Payload delivered by the server-side `CancelAgentSession` SignalR
 * invocation (issue-129 T-005 / design D6). Distinct from
 * `ReceiveFollowup` because cancel needs a reply path (the runner
 * returns `{ state: "cancelled" | "not-cancellable" | <terminal-state> }`)
 * while followup is strictly fire-and-forget. The `target` shape is the
 * same `SessionTarget` discriminator introduced in T-004; today only
 * generic (non-workflow) sessions are reachable through this method
 * because the cancel endpoint is product-level and issue-anchored
 * sessions have no cancel surface.
 */
export interface CancelAgentSessionPayload {
  target: ReceiveFollowupSessionTarget
}

/**
 * Reply shape returned by the runner for the `CancelAgentSession`
 * invocation. The server mirrors this value into the HTTP response so
 * the API can never fake success (design D6). Recognised values:
 * `cancelled`, `not-cancellable`, and the terminal-state names
 * (`completed` / `failed` / `stopped`).
 */
export interface CancelAgentSessionReply {
  state: string
}

// Issue-129 T-004: derives a discriminated `SessionTarget` from the
// unified `ReceiveFollowup` SignalR payload. Prefers the `target` field
// when present; falls back to the legacy top-level `workflowRunId` /
// `sessionName` fields so older server builds (which only populate the
// top-level fields on the issue-scoped route) keep working against the
// workflow followup path. Returns `null` when neither carries a usable
// target — the caller drops the message silently, matching the existing
// "unknown session" contract.
export function resolveSessionTarget(payload: ReceiveFollowupPayload): SessionTarget | null {
  const target = payload.target
  if (target) {
    return sessionTargetFromWireTarget(target)
  }

  // Legacy fallback for older server builds (no `target` field).
  if (payload.workflowRunId && payload.sessionName) {
    return { kind: "workflow", projectId: "", workflowRunId: payload.workflowRunId, sessionName: payload.sessionName }
  }
  return null
}

export function sessionTargetFromWireTarget(target: ReceiveFollowupSessionTarget | null | undefined): SessionTarget | null {
  if (!target) return null
  const projectId = target.projectId ?? ""
  if (!projectId) return null
  const binding = runtimeBindingFromWireTarget(target.binding)
  if (target.binding !== undefined && !binding) return null

  if (target.kind === "workflow" && target.workflowRunId && target.sessionName) {
    return {
      kind: "workflow",
      projectId,
      workflowRunId: target.workflowRunId,
      sessionName: target.sessionName,
      ...(binding ? { binding } : {}),
    }
  }
  if (target.kind === "generic" && target.sessionId) {
    return {
      kind: "generic",
      projectId,
      sessionId: target.sessionId,
      ...(binding ? { binding } : {}),
    }
  }
  return null
}

function runtimeBindingFromWireTarget(value: unknown): RuntimeSessionBinding | null {
  if (!value || typeof value !== "object") return null
  const binding = value as Partial<RuntimeSessionBinding>
  return typeof binding.runtime === "string" && binding.runtime.length > 0
    && typeof binding.runtimeSessionId === "string" && binding.runtimeSessionId.length > 0
    && typeof binding.runnerId === "string" && binding.runnerId.length > 0
    && (binding.workDir === undefined || binding.workDir === null || (typeof binding.workDir === "string" && binding.workDir.length > 0))
    ? {
        runtime: binding.runtime,
        runtimeSessionId: binding.runtimeSessionId,
        runnerId: binding.runnerId,
        workDir: binding.workDir ?? null,
      }
    : null
}
