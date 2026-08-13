// Session-target resolution and the wire/target types shared by the
// server→runner push handlers (`ReceiveFollowup`, `CancelAgentSession`,
// `ReceiveWorkflowRunStatus`).

/**
 * Persisted runtime-session binding carried on the wire target by
 * `ReceiveFollowup` / `CancelAgentSession`. Mirrors the server-side
 * `RuntimeSessionBinding` record.
 */
export interface RuntimeSessionBinding {
  runtime: string
  runtimeSessionId: string
  runnerId: string
  workDir: string | null
}

export interface AgentExecutionDefinition {
  readonly instructions?: string | null
  readonly runtime?: string | null
  readonly model?: string | null
  readonly variant?: string | null
  readonly skills?: readonly string[] | null
}

export type SessionTarget =
  | { kind: "workflow"; projectId: string; workflowRunId: string; sessionName: string; agentSessionId?: string; binding?: RuntimeSessionBinding }
  | { kind: "generic"; projectId: string; sessionId: string; definition?: AgentExecutionDefinition; binding?: RuntimeSessionBinding }

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
  readonly definition?: AgentExecutionDefinition
}

/**
 * The runner-side resolver turns a discriminated `SessionTarget`
 * into a `FollowupTarget` constructed from the
 * persisted binding, or `null` when no usable binding is registered.
 *
 * The resolver is BINDING-ONLY. It never reads runtime
 * or outbox readiness. Admission is owned by each caller (claim,
 * follow-up, cancel) so the resolver observes neither a stale runtime
 * nor a second runtime during replacement.
 *
 * Both `ReceiveFollowup` and `CancelAgentSession` call into this
 * resolver; a single registration keeps the wire-decoding logic in
 * one place.
 */
export type FollowupTargetResolution = FollowupTarget | null

export type FollowupTargetResolver = (target: SessionTarget) => FollowupTargetResolution | Promise<FollowupTargetResolution>

/**
 * Discriminated session target carried in the unified
 * `ReceiveFollowup` SignalR payload. The runner
 * branches on `kind` to pick the right runtime-events endpoint
 * (`workflow:` / `generic:`) and the right server-side
 * runtime endpoint.
 *
 * The `binding` field carries the persisted AgentSession binding
 * (the same source the Workflow path already uses): the resolver
 * reads `binding.runtimeSessionId`
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
  definition?: AgentExecutionDefinition
}

/**
 * Unified payload delivered by the server-side `ReceiveFollowup` SignalR
 * method. The `text` field is always present. It may be empty when accepted
 * attachment descriptors are present; the server rejects an empty input
 * only when there are no accepted attachments to deliver.
 */
export interface ReceiveFollowupPayload {
  target?: ReceiveFollowupSessionTarget
  text: string
  operationId?: string
  /**
   * Issue-522 T-001: stable SessionInput id minted by the Server
   * and recorded on the AgentSession grain before the Runner is
   * invoked. When present the Runner uses it as the canonical id on
   * the durable `session.input` record so the Server does not have
   * to mint a duplicate. Absent on legacy callers that did not yet
   * support the durable Turn identity; the Runner falls back to its
   * own random id and the Server's existing acceptance path.
   */
  inputId?: string
  /**
   * Issue-522 T-001: stable AgentTurn id minted by the Server and
   * recorded on the AgentSession grain before the Runner is
   * invoked. The Runner does not currently use this id (it has no
   * Turn-id-keyed state); it is carried on the wire so later stop /
   * cancel plumbing can target the same Turn. Absent on legacy
   * callers.
   */
  turnId?: string
  /**
   * Issue-513 T-003: accepted attachment descriptors for this
   * follow-up turn. The Runner uses these to materialize the
   * workspace, build the system-attributed manifest block, and pass
   * native image parts on OpenCode. Bytes are NEVER carried on the
   * wire; the Runner fetches content through the owning-input
   * scoped content route.
   */
  attachments?: ReadonlyArray<{
    readonly id: string
    readonly name: string
    readonly contentType: string | null
    readonly size: number
  }>
  slackExecutionContext?: unknown
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
 * invocation. Distinct from
 * `ReceiveFollowup` because cancel needs a reply path (the runner
 * returns `{ state: "stopped" | "unknown" | "stop-requested" | <terminal-state> }`)
 * while followup is strictly fire-and-forget. The `target` shape is the
 * same `SessionTarget` discriminator; today only
 * generic (non-workflow) sessions are reachable through this method
 * because the cancel endpoint is product-level and issue-anchored
 * sessions have no cancel surface.
 */
export interface CancelAgentSessionPayload {
  target: ReceiveFollowupSessionTarget
  turnId?: string
  sessionId?: string
  operationId?: string
}

/**
 * Reply shape returned by the runner for the `CancelAgentSession`
 * invocation. The server mirrors this value into the HTTP response so
 * the API can never fake success. Recognised values:
 * `stopped`, `unknown`, `stop-requested`, `not-cancellable`, and terminal
 * state names.
 *
 * `interruptUnconfirmed` is the honest
 * stop-confirmation flag the API needs to surface when a runtime
 * (currently Pi) could not confirm the turn actually stopped. The
 * internal runtime confirmation fact is reduced to the existing reply
 * states; confirmed-stop replies report `stopped`.
 */
export interface CancelAgentSessionReply {
  state: string
  interruptUnconfirmed?: boolean
}

// Derives a discriminated `SessionTarget` from the
// unified `ReceiveFollowup` SignalR payload. Returns `null` when the
// payload carries no usable target — the caller drops the message
// silently, matching the existing "unknown session" contract.
export function resolveSessionTarget(payload: ReceiveFollowupPayload): SessionTarget | null {
  return sessionTargetFromWireTarget(payload.target)
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
      ...(target.sessionId ? { agentSessionId: target.sessionId } : {}),
      ...(binding ? { binding } : {}),
    }
  }
  if (target.kind === "generic" && target.sessionId) {
    return {
      kind: "generic",
      projectId,
      sessionId: target.sessionId,
      ...(target.definition ? { definition: target.definition } : {}),
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
