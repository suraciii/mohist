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

import type { SessionTarget } from "../runtime/acp-connection.js"

/**
 * Discriminated session target carried in the unified
 * `ReceiveFollowup` SignalR payload (issue-129 T-004). The runner
 * branches on `kind` to pick the right `AcpSessionManager` key prefix
 * (`workflow:` / `generic:`, T-002) and the right server-side runtime
 * endpoint. Older runners that only know workflow followups can keep
 * reading the top-level `workflowRunId` / `sessionName` fields the
 * server still populates for the issue-scoped route.
 */
export interface ReceiveFollowupSessionTarget {
  kind: "workflow" | "generic"
  projectId: string
  workflowRunId?: string
  sessionName?: string
  sessionId?: string
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
    if (target.kind === "workflow") {
      if (!target.workflowRunId || !target.sessionName) return null
      const projectId = target.projectId ?? ""
      if (!projectId) return null
      return { kind: "workflow", projectId, workflowRunId: target.workflowRunId, sessionName: target.sessionName }
    }
    if (target.kind === "generic") {
      if (!target.sessionId) return null
      const projectId = target.projectId ?? ""
      if (!projectId) return null
      return { kind: "generic", projectId, sessionId: target.sessionId }
    }
    return null
  }

  // Legacy fallback for older server builds (no `target` field).
  if (payload.workflowRunId && payload.sessionName) {
    return { kind: "workflow", projectId: "", workflowRunId: payload.workflowRunId, sessionName: payload.sessionName }
  }
  return null
}