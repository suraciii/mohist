### Requirement: Session target resolution discriminates on target.kind with legacy fallback

`resolveSessionTarget` SHALL prefer the unified `target` field when present. A `target.kind === "workflow"` resolves only when `workflowRunId`, `sessionName`, and a non-empty `projectId` are all present, producing `{ kind: "workflow", projectId, workflowRunId, sessionName }`. A `target.kind === "generic"` resolves only when `sessionId` and a non-empty `projectId` are present, producing `{ kind: "generic", projectId, sessionId }`. Any other `kind`, or a target missing a required field, SHALL return `null`. When no `target` field is present, the resolver SHALL fall back to the legacy top-level `workflowRunId` / `sessionName` fields and produce a `{ kind: "workflow", projectId: "", workflowRunId, sessionName }`; if those legacy fields are absent it SHALL return `null`.

#### Scenario: Generic target resolves with sessionId and projectId

- **WHEN** the payload carries `{ target: { kind: "generic", projectId: "p", sessionId: "s" } }`
- **THEN** `resolveSessionTarget` returns `{ kind: "generic", projectId: "p", sessionId: "s" }`

#### Scenario: Generic target missing sessionId returns null

- **WHEN** the payload carries `{ target: { kind: "generic", projectId: "p" } }`
- **THEN** `resolveSessionTarget` returns `null`

#### Scenario: Workflow target resolves with workflowRunId, sessionName, and projectId

- **WHEN** the payload carries `{ target: { kind: "workflow", projectId: "p", workflowRunId: "wr", sessionName: "sn" } }`
- **THEN** `resolveSessionTarget` returns `{ kind: "workflow", projectId: "p", workflowRunId: "wr", sessionName: "sn" }`

#### Scenario: Workflow target missing sessionName returns null

- **WHEN** the payload carries `{ target: { kind: "workflow", projectId: "p", workflowRunId: "wr" } }`
- **THEN** `resolveSessionTarget` returns `null`

#### Scenario: Unknown target kind returns null

- **WHEN** the payload carries `{ target: { kind: <unrecognized> } }`
- **THEN** `resolveSessionTarget` returns `null`

#### Scenario: Legacy top-level fields fall back to a workflow target with empty projectId

- **WHEN** the payload carries no `target` but has top-level `workflowRunId` and `sessionName`
- **THEN** `resolveSessionTarget` returns `{ kind: "workflow", projectId: "", workflowRunId, sessionName }`

#### Scenario: Neither target nor legacy fields returns null

- **WHEN** the payload carries neither `target` nor top-level `workflowRunId`/`sessionName`
- **THEN** `resolveSessionTarget` returns `null`

### Requirement: ReceiveFollowup is fire-and-forget and never blocks the prompt

The `ReceiveFollowup` handler SHALL return immediately without awaiting the resolved session's `connection.prompt`. It SHALL emit a `session.input` runtime event (with `payload.kind === "followup"`, `role === "user"`, `source === "followup"`, the followup `text`, and the resolved `acpSessionId`) via the workflow endpoint (`workflowAgentSessionRuntimeEvents`) for a `workflow` target and via the generic endpoint (`agentSessionRuntimeEvents`) for a `generic` target, each as a non-awaited promise whose own rejection is logged but does not block the prompt. A failure in the runtime-event emit MUST NOT prevent the prompt from being issued.

#### Scenario: Prompt is issued without being awaited

- **WHEN** a workflow followup is delivered with a resolvable target
- **THEN** the handler returns before `connection.prompt` resolves, and `prompt` is invoked exactly once with `{ sessionId, prompt: [{ type: "text", text }] }`

#### Scenario: Runtime-event emit failure does not block the prompt

- **WHEN** the runtime-event endpoint rejects
- **THEN** the handler still invokes `connection.prompt` exactly once, and the rejection is logged rather than thrown

#### Scenario: Workflow target uses the workflow runtime-events endpoint

- **WHEN** a followup with `target.kind === "workflow"` is delivered
- **THEN** `workflowAgentSessionRuntimeEvents` is invoked (with projectId, workflowRunId, sessionName) and `agentSessionRuntimeEvents` is not

#### Scenario: Generic target uses the agent-session runtime-events endpoint

- **WHEN** a followup with `target.kind === "generic"` is delivered
- **THEN** `agentSessionRuntimeEvents` is invoked (with projectId, sessionId) and `workflowAgentSessionRuntimeEvents` is not

#### Scenario: session.input event is tagged with kind followup

- **WHEN** any followup is delivered and emitted
- **THEN** the runtime event's payload contains `{ type: "session.input", payload: { kind: "followup", role: "user", source: "followup", text, acpSessionId } }`

### Requirement: ReceiveFollowup drops unusable payloads and resolver outcomes silently

The `ReceiveFollowup` handler SHALL drop a payload without prompting when the payload or its `text` is missing/empty, when no `followupTargetResolver` is registered, when no `serverConnection` is registered, when `resolveSessionTarget` returns `null`, when the resolver throws (logging the error), or when the resolver returns `null`. In every drop case the handler MUST NOT throw to the transport and MUST NOT issue a prompt or runtime event.

#### Scenario: Missing or empty text drops

- **WHEN** the payload's `text` is missing, empty, or whitespace
- **THEN** no prompt is issued and no runtime event is emitted

#### Scenario: Null/undefined payload drops

- **WHEN** the payload is `null` or `undefined`
- **THEN** no prompt is issued and no runtime event is emitted

#### Scenario: Missing resolver or server connection drops

- **WHEN** no `followupTargetResolver` or no `serverConnection` is registered
- **THEN** no prompt is issued

#### Scenario: Resolver returning null drops

- **WHEN** the resolver returns `null` for the resolved target
- **THEN** no prompt is issued and no runtime event is emitted, and the handler does not throw

#### Scenario: Resolver throwing drops and logs

- **WHEN** the resolver throws
- **THEN** no prompt is issued, no runtime event is emitted, the error is logged, and the handler does not throw

#### Scenario: Prompt rejection is caught and logged

- **WHEN** `connection.prompt` rejects
- **THEN** the rejection is logged and the handler does not throw

### Requirement: CancelAgentSession replies with the observed session state

The `CancelAgentSession` handler SHALL return a `{ state }` reply that the server mirrors verbatim into the HTTP response, so the runner MUST NOT fabricate a successful state. Only a `target.kind === "generic"` with a `sessionId` is cancellable. The handler SHALL report `cancelled` only when the resolver hits AND the resolved connection exposes a `cancel` function AND that `cancel({ sessionId })` resolves; otherwise it SHALL report `not-cancellable`. A resolver throw, a resolver `null`, a missing `cancel` method on the connection, a `cancel` rejection, a non-generic or session-id-less target, a missing resolver, and a null/missing payload MUST all return `not-cancellable` without throwing.

#### Scenario: Live generic session is cancelled

- **WHEN** the target is `{ kind: "generic", sessionId }`, the resolver hits, the connection exposes `cancel`, and `cancel({ sessionId })` resolves
- **THEN** the reply is `{ state: "cancelled" }` and `cancel` was called with the resolved ACP session id

#### Scenario: Unknown session returns not-cancellable

- **WHEN** the resolver returns `null`
- **THEN** the reply is `{ state: "not-cancellable" }` and `cancel` is not called

#### Scenario: No registered resolver returns not-cancellable

- **WHEN** no `followupTargetResolver` is registered
- **THEN** the reply is `{ state: "not-cancellable" }` and `cancel` is not called

#### Scenario: Connection without a cancel method returns not-cancellable

- **WHEN** the resolved connection has no `cancel` function
- **THEN** the reply is `{ state: "not-cancellable" }`

#### Scenario: Cancel send rejection returns not-cancellable and logs

- **WHEN** `cancel({ sessionId })` rejects
- **THEN** the reply is `{ state: "not-cancellable" }`, the error is logged, and the handler does not throw

#### Scenario: Resolver throw returns not-cancellable and logs

- **WHEN** the resolver throws
- **THEN** the reply is `{ state: "not-cancellable" }`, the error is logged, `cancel` is not called, and the handler does not throw

#### Scenario: Null or missing payload returns not-cancellable

- **WHEN** the payload is `null`, `undefined`, or has no `target`
- **THEN** the reply is `{ state: "not-cancellable" }` and `cancel` is not called

#### Scenario: Workflow-shaped target returns not-cancellable

- **WHEN** the target has `kind === "workflow"`
- **THEN** the reply is `{ state: "not-cancellable" }` and `cancel` is not called (the cancel surface is generic-only)

#### Scenario: Generic target without sessionId returns not-cancellable

- **WHEN** the target has `kind === "generic"` but no `sessionId`
- **THEN** the reply is `{ state: "not-cancellable" }`

### Requirement: ReceiveWorkflowRunStatus transitions only terminal runs idempotently

The `ReceiveWorkflowRunStatus` handler SHALL transition a registry entry from `active` to `eligible` (stamping `terminalAt`) only for terminal statuses (`Completed` and `Stopped`). `Failed` and every non-terminal status SHALL leave an `active` entry unchanged with `terminalAt` remaining `null`. The transition SHALL be idempotent: re-delivering a terminal push for an already-eligible entry MUST NOT re-stamp `terminalAt` and MUST NOT rewrite the on-disk file with a new value. The handler SHALL tolerate unknown run ids, null/undefined payloads, payloads missing `workflowRunId`, and registry failures without throwing to the transport.

#### Scenario: Completed push transitions active to eligible

- **WHEN** a `Completed` push arrives for a run with an `active` entry
- **THEN** the entry's `phase` becomes `eligible` and `terminalAt` is set

#### Scenario: Stopped push transitions active to eligible

- **WHEN** a `Stopped` push arrives for a run with an `active` entry
- **THEN** the entry's `phase` becomes `eligible`

#### Scenario: Failed push leaves the entry active

- **WHEN** a `Failed` push arrives for a run with an `active` entry
- **THEN** the entry remains `active` and `terminalAt` stays `null`

#### Scenario: Non-terminal push leaves the entry active

- **WHEN** any non-terminal status (`Created`, `Pending`, `Ready`, `Running`, `Paused`, `AwaitingApproval`, or an unknown value) arrives for an `active` entry
- **THEN** the entry remains `active` and `terminalAt` stays `null`

#### Scenario: Re-delivered terminal push does not re-stamp terminalAt

- **WHEN** a terminal push arrives for an entry that is already `eligible` with a stamped `terminalAt`
- **THEN** the entry stays `eligible` and `terminalAt` is unchanged (no re-stamp)

#### Scenario: Unknown run id is a no-op that does not throw

- **WHEN** a terminal push arrives for a run id the runner never materialized
- **THEN** the handler resolves without throwing and the registry is unchanged

#### Scenario: Null payload or missing workflowRunId does not throw

- **WHEN** the payload is `null`, `undefined`, or lacks a non-empty string `workflowRunId`
- **THEN** the handler resolves without throwing and the registry is unchanged

#### Scenario: Registry failure is logged without throwing to the transport

- **WHEN** the registry operation throws
- **THEN** the error is logged and the handler resolves without throwing

#### Scenario: Transition persists to the on-disk registry

- **WHEN** a terminal push transitions an entry to `eligible`
- **THEN** the on-disk registry file reflects `phase: "eligible"` and a non-null `terminalAt`

### Requirement: RemoveWorkspace is runner-root contained and keeps the registry consistent

The `RemoveWorkspace` handler SHALL refuse to delete any path that resolves outside the configured runner root, returning `{ removed: false, status: "failed", reason: "workspace_cleanup_refused" }` and leaving the registry untouched for that path. The handler SHALL drop the registry entry matching the resolved workspace path regardless of whether the directory still exists on disk (so the registry tracks disk reality); `null`/missing `workspacePath` drops nothing from the registry. A missing directory SHALL return `{ removed: false, status: "missing", reason: "workspace_missing" }`. A successful delete SHALL return `{ removed: true, status: "removed", reason: null }`. A delete failure SHALL return `{ removed: false, status: "failed", reason: "workspace_cleanup_failed" }` carrying the error message.

#### Scenario: Path under the runner root is removed and the entry is dropped

- **WHEN** `RemoveWorkspace` is invoked with a `workspacePath` that resolves under the runner root and the directory exists
- **THEN** the directory is deleted, the matching registry entry is removed, and the reply is `{ removed: true, status: "removed", ... }`

#### Scenario: Already-missing directory still drops the registry entry

- **WHEN** `RemoveWorkspace` is invoked with a `workspacePath` whose directory no longer exists but whose registry entry remains
- **THEN** the registry entry is dropped and the reply is `{ removed: false, status: "missing", reason: "workspace_missing" }`

#### Scenario: Path outside the runner root is refused

- **WHEN** `RemoveWorkspace` is invoked with a `workspacePath` that resolves outside the runner root
- **THEN** the reply is `{ removed: false, status: "failed", reason: "workspace_cleanup_refused" }`, the directory is not deleted, and the registry is not modified for that path

#### Scenario: Missing workspacePath reports workspace_missing

- **WHEN** `RemoveWorkspace` is invoked with no `workspacePath`
- **THEN** the reply is `{ removed: false, status: "missing", reason: "workspace_missing" }` with the path reported as null, and no registry entry is dropped

#### Scenario: Delete failure reports workspace_cleanup_failed

- **WHEN** the directory delete throws
- **THEN** the reply is `{ removed: false, status: "failed", reason: "workspace_cleanup_failed" }` carrying the error message
