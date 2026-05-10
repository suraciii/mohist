## Why

Mohist can currently believe an issue workflow task is still running while the underlying opencode ACP session has silently stopped producing responses, events, or other observable data. This change makes session liveness an explicit session-level responsibility so silent failures are detected promptly and returned to task/workflow callers without mutating issue stage or status directly.

## What Changes

- Track `lastDataAt` for each running opencode session whenever ACP/opencode emits valid new data such as protocol responses, session updates, assistant text, tool calls, tool updates, message growth, process errors, or process exits.
- Add a narrow session liveness state flow: `running` enters `probing` after the configured quiet threshold, returns to `running` when any valid new data arrives, and becomes `failed` when the probe times out, cannot be sent, the protocol disconnects, or the process exits unexpectedly.
- Probe the same opencode session rather than creating a new task or issue comment; prefer ACP/protocol-level ping or status if available, otherwise send the minimal model probe message.
- Return session failure as the opencode session call result so task/workflow code can apply retry, block, interruption, or user-action policy above the session layer.
- Persist session liveness fields on session call records: `lastDataAt`, `probeSentAt`, `probeDeadlineAt`, and `failureReason`, alongside the existing session identity and status fields.
- Keep issue `stage` and `status` unchanged by session probing or session failure; workflow decisions after receiving `SessionFailed` remain separate.
- Expose only simple user-facing current-session states in CLI/Web surfaces: `Running`, `Checking session`, `Session failed`, and `No active session`.
- Do not introduce `IssueSessionSummary`, runtime health taxonomies, CPU/IO/process wait-state checks, or complete retry/recovery/WIP-preservation behavior in this change.

## Capabilities

### New Capabilities

- None

### Modified Capabilities

- `agent-runtime` — ACP session execution must maintain liveness timestamps, probe quiet sessions, transition through `probing`, and return failed session results for probe timeout/failure, disconnect, or process exit.
- `coder-session-tracking` — Persisted coder session records must include liveness fields and failure reason in addition to running/completed/failed/cancelled state.
- `ralph-task-execution` — Build task attempts must treat session failure as a task execution result distinct from successful completion and allow existing task retry/block policy to decide the next action.
- `workflow-agent` — Workflow orchestration must consume session failed results from task/session calls without directly judging opencode liveness or mutating issue state from session health.
- `pipeline-session-events` — Session lifecycle/event streams must surface probing and failed session status changes so live clients can display the current session call state.
- `http-api` — Issue/session detail responses must expose the current session call status, last response/data time, probe timing, and failure reason needed by CLI/Web.
- `web-ui` — Issue detail and session views must show the simplified current session state labels without adding a complex health taxonomy.
- `cli-interface` — CLI issue/status output must show the same simplified current session state labels when an issue has an active or failed session call.

## Impact

- **Agent runtime**: `packages/cli/src/agent-runtime/agent-session.ts`, `session-state.ts`, and `session-observer.ts` need liveness tracking, a `probing` state, probe dispatch, probe timeout handling, and a failure result that preserves the existing session call boundary.
- **ACP/opencode integration**: `ClientSideConnection` usage must determine whether ACP exposes a protocol-level ping/status/resume capability; if not, `connection.prompt()` must send only the narrow liveness probe to the existing session.
- **Persistence**: `packages/cli/src/db/coder-session-repo.ts` and database migrations for `coder_session` need liveness columns for `last_data_at`, `probe_sent_at`, `probe_deadline_at`, and `failure_reason`; existing issue stage/status storage should not change.
- **Observers and logs**: `packages/cli/src/services/session-observers.ts`, `session_stream_log`, `workflow_log`, and EventBus emission should update `lastDataAt` from valid ACP/session data and publish/provide state changes for probing and failed sessions.
- **Task/workflow callers**: Build task execution paths, including Ralph task execution and stage runners that call `withSession()` or `AgentSession.execute()`, must receive and handle `SessionFailed` style results rather than waiting for the global session timeout or inferring issue failure.
- **API/CLI/Web**: Issue detail, coder session detail, agent/session status endpoints, `mo issue show` or status output, and Web UI issue/session panels need current-session display fields for running, checking session, failed, or no active session.
- **Tests**: Add or update runtime/session tests for data-refresh avoiding probes, quiet-threshold probe, probe recovery on new data, probe timeout/failure, process/protocol failure, returned session failure, and issue stage/status remaining unchanged by probing or failure.
