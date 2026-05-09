## Context

Mohist already centralizes opencode ACP execution in `AgentSession` and projects session metadata through observers into `coder_session`, session stream logs, workflow logs, SSE events, API responses, CLI output, and Web UI session components. The current runtime can detect explicit prompt timeout, process exit, cancellation, and thrown ACP errors, but it does not distinguish a long-running live session from a session whose ACP stream has stopped producing observable data.

The important boundary is domain ownership: issue stage/status are workflow facts and must not be used as a session health store. Session liveness belongs inside the session call; task/workflow callers should only receive a completed, failed, or cancelled session result and decide retry or user action outside the session layer.

## Goals / Non-Goals

**Goals:**

- Track session liveness from ACP/opencode observable data using `lastDataAt`.
- Probe the same opencode session after a quiet threshold before declaring failure.
- Return a distinct session failure result to task/workflow callers.
- Persist enough session call state to render current session status after API reloads.
- Show only simple user-facing session labels: `Running`, `Checking session`, `Session failed`, and `No active session`.
- Keep issue `stage` and `status` unchanged when a session enters `probing` or `failed`.

**Non-Goals:**

- No issue-level session summary domain object.
- No health taxonomy such as healthy, quiet, stale, suspected hung, or recoverable.
- No CPU, IO, process wait-state, or OS-level liveness detection.
- No full retry/recovery/WIP preservation redesign.
- No new task or issue comment for probe messages.

## Decisions

### D1: Keep Liveness Inside `AgentSession`

`AgentSession` should own the liveness state machine because it already owns the ACP connection, prompt lifecycle, process failure promises, cancellation, timeout handling, and result construction. Add lightweight runtime fields for `lastDataAt`, `probeSentAt`, `probeDeadlineAt`, and `failureReason`, and extend `SessionState` with `probing`.

State transitions:

- `running -> probing` when `Date.now() - lastDataAt >= quietThresholdMs` during an active prompt call.
- `probing -> running` when any valid ACP/opencode data arrives before the probe deadline.
- `probing -> failed` when probe send fails, probe deadline expires, ACP disconnects, or process exits.
- `running -> failed` remains valid for explicit protocol/process failures.
- `running/probing -> completed` only when the active prompt completes successfully.
- `running/probing -> cancelled` when user cancellation wins.

The state machine remains narrow and should remove `timeout` as a primary user-facing state for this flow by mapping timeout-like session call failures into `failed` with a specific `failureReason` where appropriate. If existing callers still depend on `timeout`, keep it internally compatible but surface session liveness failure as `failed`.

**Alternatives considered:** A separate `SessionLivenessMonitor` service would make the interface shallower by forcing AgentSession internals to leak connection, timer, and probe controls. A workflow-level monitor was rejected because workflow should not decide whether opencode is alive.

### D2: Treat ACP Notifications and Explicit Protocol Outcomes as Data

`lastDataAt` should update when ACP/opencode provides any valid observable signal for the session:

- `sessionUpdate` notifications, including assistant text, thoughts, tool calls, tool updates, plan/usage/user message chunks, and future ACP update types.
- successful protocol responses used by the active session call, such as initialize, newSession, prompt completion, setSessionConfigOption, and any future protocol-level ping/status response.
- process error/exit events only as terminal observable data paired with failure handling.

Implementation should update liveness before observer fan-out so observer failures cannot prevent liveness refresh. Raw `sessionUpdate` handling remains the broadest rule: if ACP produced a session update, the session is alive regardless of whether the UI currently renders that event type.

**Alternatives considered:** Maintaining a handpicked list of “meaningful” event types was rejected because ACP event shapes evolve and the issue explicitly considers any protocol/session data sufficient. Reusing `lastActivityAt` from workflow/session logs was rejected because logs are a derived persistence concern and miss protocol responses that are not stream events.

### D3: Implement Probe as a Race Inside `execute()`

During `AgentSession.execute()`, replace the single prompt-vs-timeout race with a small monitor loop around the same prompt promise. The loop waits for whichever happens first: prompt completion, abort signal, process exit, quiet-threshold timer, or probe deadline timer.

When the quiet threshold fires while still `running`, transition to `probing`, persist probe timestamps through observers, and send the probe to the same ACP session. Prefer a protocol-level ping/status request if the installed ACP SDK/opencode version exposes one. If no such method exists, use `connection.prompt()` with only:

```text
If this session is still alive, briefly report the current step and continue from existing context. Do not restart completed work.
```

The probe is not a new Mohist task and should not create a new issue comment. If probe text is sent through the model, record it as a `mohist_prompt` with kind `probe` or metadata that lets transcript rendering identify it as a system liveness probe rather than user work. Any subsequent valid data, including the probe response, returns state to `running` and the original task continues waiting for normal completion.

**Alternatives considered:** Killing on quiet threshold was rejected because quiet alone is not failure. Creating a new session for probing was rejected because it would lose the session context being tested. Using a normal workflow/user comment was rejected because probe is session control, not product collaboration.

### D4: Return a Structured Session Failure Without Forcing Workflow State

Extend `AcpSessionResult` with a small result classification, for example `status?: 'completed' | 'failed' | 'cancelled'` or `failureKind?: 'session_failed' | 'timeout' | 'cancelled'`, while preserving `success: boolean` for existing callers. Probe timeout, probe send failure, protocol disconnect, and process exit should return `success: false`, `status: 'failed'`, `failureKind: 'session_failed'`, `error`, `failureReason`, and `acpSessionId`.

Ralph/build task execution can categorize this as non-success and apply the existing retry/block policy. Plan/check/fix callers that directly use `AgentSession.create()` and `execute()` should receive the same result shape. No session failure handler should directly update issue `stage` or `status`; only existing task/workflow policy may decide later to retry, block, interrupt, or ask the user.

**Alternatives considered:** Throwing on session failure was rejected because most current callers already consume `AcpSessionResult` and use `success: false`. Directly setting issue status to interrupted was rejected because session failure is not necessarily the final workflow decision.

### D5: Persist Liveness on `coder_session`

Add nullable columns to `coder_session`:

- `last_data_at TEXT`
- `probe_sent_at TEXT`
- `probe_deadline_at TEXT`
- `failure_reason TEXT`

Use `status = 'running' | 'probing' | 'completed' | 'failed' | 'cancelled'` for the session call state. New sessions should initialize `last_data_at` to the session creation/start time. `WorkflowSessionObserver` should update the row when the runtime reports data, probe state, failure reason, or terminal state. Repository helpers should provide explicit operations such as `markDataReceived`, `markProbing`, and `markFailed` rather than pushing SQL details into runtime code.

Existing `findAllWithIssueInfo`, issue coder-session list, and detail endpoints should prefer `last_data_at` over derived workflow-log `lastActivityAt` for current session liveness display. Historical transcript assembly can keep using stream/log events for content.

**Alternatives considered:** A new session call table would be cleaner long term, but `coder_session` already represents the persisted opencode session call and is used by current API/UI paths. Adding `IssueSessionSummary` was explicitly rejected by scope.

### D6: Surface One Session Status Event

Add a focused SSE event such as `coder_session_status_changed` with `{ issueId, projectId, coderSessionId, acpSessionId, status, lastDataAt?, probeSentAt?, probeDeadlineAt?, failureReason? }`. Emit it on transitions to `probing`, back to `running`, and terminal `failed/cancelled/completed` updates. Keep `coder_session_started` and existing terminal events compatible, but new clients should use the status-changed event for liveness display.

Web UI hooks can update live session rows from this event and fall back to API reload for missed events. `SessionHeader`, issue detail current-task panels, and session detail metadata should map raw statuses to labels:

- `running` -> `Running`
- `probing` -> `Checking session`
- `failed` -> `Session failed`
- no active running/probing/failed current task session -> `No active session`

The existing API-derived `statusKind: 'stale'` should not be treated as authoritative after persisted `probing` exists. If kept for transcript display compatibility, it should not appear as a user-facing session health state.

**Alternatives considered:** Reusing `coder_recovery_status` was rejected because recovery is a different behavior and would blur liveness probing with retry/recovery. Emitting separate probe-start/probe-end events was rejected as unnecessary UI complexity.

### D7: Make Thresholds Configurable but Conservative

Introduce runtime options for `livenessQuietThresholdMs` and `probeTimeoutMs`, defaulting to conservative values derived from existing agent timeout config. The quiet threshold should be much shorter than the full task/session timeout but long enough to avoid probing during normal long tool execution. Tests should set these values very low through `AgentSessionOptions` to avoid slow test suites.

If configuration UI is not in scope, use internal defaults and optional config-file fields only. The design should not add a new settings surface unless a later spec requires it.

**Alternatives considered:** Hardcoded constants simplify implementation but make tests and different provider behavior harder. Full user-facing configuration was rejected as scope creep for the MVP.

## Risks / Trade-offs

- [Risk] A long-running tool may produce no ACP updates and get probed while it is legitimately working → Mitigation: use a conservative quiet threshold, send only a non-disruptive same-session probe, and treat any new data as recovery to `running`.
- [Risk] Text probe may interfere with the model’s task context if no protocol ping exists → Mitigation: prefer protocol-level capability when available, use the narrow mandated probe text, and mark probe prompts distinctly in logs/transcripts.
- [Risk] Concurrent original prompt and probe prompt may not be supported by ACP/opencode → Mitigation: verify SDK behavior before implementation; if concurrent prompt is not allowed, use the closest protocol-level status method or a serialized probe request that fails fast without starting a second task.
- [Risk] Observer or database failures could hide liveness state from UI while runtime continues correctly → Mitigation: keep runtime liveness authoritative in memory and treat persistence/event failures as non-fatal logged observer errors.
- [Risk] Adding `probing` may break code that assumes only running/completed/failed/timeout/cancelled → Mitigation: update type unions, status filters, terminal-status checks, UI label mapping, and tests together; treat `probing` as active/non-terminal wherever `running` is currently active.
- [Risk] Session failure could be confused with issue interruption → Mitigation: document and test that probing/failed session state does not update issue stage/status directly.

## Migration Plan

1. Add a SQLite migration for `coder_session` liveness columns and update `CoderSession` types, row mapping, insert defaults, and status update helpers.
2. Extend `SessionState`, transition rules, observer interface, and `WorkflowSessionObserver` to accept liveness data, persist liveness updates, and emit status-change events.
3. Implement `AgentSession` liveness bookkeeping, quiet-threshold monitoring, same-session probe dispatch, probe timeout failure, and structured failed results.
4. Update task/workflow callers to preserve existing `success: false` behavior while recognizing session failure metadata where categorization or user messaging needs it.
5. Update API responses for coder-session list/detail and current agent/session status to include `lastDataAt`, probe fields, and `failureReason`.
6. Update CLI/Web types and rendering to show the simplified labels and treat `probing` as an active session.
7. Add tests for new data avoiding probe, quiet threshold triggering probe, probe recovery, probe timeout/failure, process/protocol failure, returned session failure, and unchanged issue stage/status.
8. Rollback is schema-compatible by ignoring the new nullable columns and event fields; existing clients can continue using `status`, `createdAt`, and `completedAt`.

## Open Questions

- Does the installed `@agentclientprotocol/sdk` and current opencode ACP server expose a safe protocol-level ping/status method for an existing session, or must MVP use the narrow text probe?
- What conservative default values should be used for quiet threshold and probe timeout after observing normal long-running tool behavior in this codebase?
