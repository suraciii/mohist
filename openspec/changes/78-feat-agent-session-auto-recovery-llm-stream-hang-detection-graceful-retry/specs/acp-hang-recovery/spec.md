## ADDED Requirements

### Requirement: Idle event detection via last-event timestamp

`runAcpSession` and `createAcpConnection` SHALL track the timestamp of the last received ACP `sessionUpdate` event. On every `sessionUpdate` callback invocation, the timestamp SHALL be updated to `Date.now()`. The timestamp SHALL be initialized to `Date.now()` at the start of each `prompt()` call (i.e., when the agent begins processing).

#### Scenario: Timestamp updated on every ACP event
- **WHEN** an ACP `sessionUpdate` event arrives (any type: `agent_message_chunk`, `tool_call`, `agent_thought_chunk`, etc.)
- **THEN** the last-event timestamp SHALL be set to `Date.now()`

#### Scenario: Timestamp initialized at prompt start
- **WHEN** `connection.prompt()` is called to start a session round
- **THEN** the last-event timestamp SHALL be set to `Date.now()` before waiting for the prompt result

#### Scenario: No events within idle threshold
- **WHEN** no `sessionUpdate` event arrives within the configured idle threshold (default 3 minutes = 180000ms)
- **THEN** the session SHALL be declared as "hung" and the hang recovery flow SHALL begin

### Requirement: Idle threshold configurable via AcpSessionOptions

`AcpSessionOptions` and `AcpConnectionOptions` SHALL accept an optional `hangIdleMs?: number` field. When provided, it overrides the default idle threshold. When omitted, the default idle threshold SHALL be 180000ms (3 minutes).

#### Scenario: Custom idle threshold
- **WHEN** `hangIdleMs: 60000` is passed in options
- **THEN** hang detection triggers after 60 seconds of event silence

#### Scenario: Default idle threshold
- **WHEN** `hangIdleMs` is not provided in options
- **THEN** hang detection triggers after 180000ms (3 minutes) of event silence

#### Scenario: Idle threshold disabled
- **WHEN** `hangIdleMs: 0` is passed in options
- **THEN** hang detection SHALL be disabled entirely (no idle monitoring)

### Requirement: Hang recovery flow

When a hang is detected, the system SHALL execute the following recovery sequence:
1. Emit `coder_recovery_status` SSE event with `status: 'detected'`
2. Write `acp_session_hang_detected` to workflow_log
3. Execute WIP commit via `onBeforeKill(cwd)` with a 5-second timeout
4. Call `connection.cancel({ sessionId })` with a 5-second timeout
5. If cancel succeeds: wait 1 second cooldown, then call `connection.prompt()` with a recovery hint message
6. If cancel times out: fall back to kill process (existing behavior)

The recovery hint message SHALL include context that the session was interrupted due to LLM stream hang and that the agent should continue from where it left off.

#### Scenario: Successful recovery after first hang
- **WHEN** a hang is detected (3 min idle) on attempt 1
- **AND** `cancel()` succeeds within 5 seconds
- **THEN** the system SHALL wait 1 second cooldown
- **AND** call `connection.prompt()` with recovery hint
- **AND** reset the last-event timestamp to `Date.now()`
- **AND** emit `coder_recovery_status` SSE event with `status: 'recovering'`
- **AND** write `acp_session_recovery_started` to workflow_log
- **AND** continue monitoring for the prompt result

#### Scenario: Recovery succeeds — agent resumes producing events
- **WHEN** a recovery prompt was issued after hang detection
- **AND** the agent begins producing new `sessionUpdate` events
- **THEN** the system SHALL emit `coder_recovery_status` SSE event with `status: 'recovered'`
- **AND** write `acp_session_recovery_succeeded` to workflow_log
- **AND** continue normal session monitoring

#### Scenario: Cancel times out during recovery
- **WHEN** a hang is detected
- **AND** `connection.cancel()` does not resolve within 5 seconds
- **THEN** the system SHALL abort the cancel attempt
- **AND** fall back to killing the process (existing `ensureKill` behavior)
- **AND** write `acp_session_recovery_failed` to workflow_log with reason `'cancel_timeout'`
- **AND** return an `AcpSessionResult` with `success: false` and error containing `'[HANG_UNRECOVERABLE] cancel timed out'`

#### Scenario: WIP commit during recovery
- **WHEN** a hang is detected and `onBeforeKill` callback is provided
- **THEN** the callback SHALL be invoked with a 5-second timeout before attempting `cancel()`
- **AND** if the callback succeeds, `wipCommitted` SHALL be `true` in the result if recovery ultimately fails

#### Scenario: WIP commit times out during recovery
- **WHEN** a hang is detected
- **AND** `onBeforeKill` callback does not resolve within 5 seconds
- **THEN** the WIP commit SHALL be skipped
- **AND** recovery SHALL proceed to `cancel()` step regardless

### Requirement: Maximum recovery attempts

The system SHALL limit recovery attempts to 2 per session round. A recovery attempt is counted when the recovery prompt is issued (i.e., cancel succeeded and prompt was sent). If the agent hangs again after a recovery prompt and the maximum attempts have been exhausted, the system SHALL return failure.

#### Scenario: Second hang triggers final recovery
- **WHEN** the agent hangs for the second time in the same session round
- **AND** only 1 recovery attempt has been used
- **THEN** the system SHALL perform another recovery (cancel + prompt)

#### Scenario: Third hang exceeds max attempts
- **WHEN** the agent hangs for the third time in the same session round
- **AND** 2 recovery attempts have already been used
- **THEN** the system SHALL NOT attempt another recovery
- **AND** SHALL kill the process
- **AND** write `acp_session_recovery_failed` to workflow_log with reason `'max_attempts_exceeded'`
- **AND** return `AcpSessionResult` with `success: false` and error containing `'[HANG_UNRECOVERABLE] max recovery attempts exceeded'`

#### Scenario: Recovery attempt counter resets per prompt round
- **WHEN** `createAcpConnection.prompt()` is called for a new round
- **THEN** the recovery attempt counter SHALL be reset to 0

### Requirement: Recovery applies to both runAcpSession and createAcpConnection

The hang detection and recovery logic SHALL apply to both `runAcpSession` (single-round) and `createAcpConnection.prompt()` (multi-round). The implementation SHALL use a shared internal function to avoid duplication.

#### Scenario: runAcpSession with hang recovery
- **WHEN** `runAcpSession` is running and no ACP events arrive for 3 minutes
- **THEN** hang recovery SHALL be attempted with cancel + prompt
- **AND** if recovery succeeds, the session continues and eventually returns normally
- **AND** if recovery fails, returns `AcpSessionResult` with `success: false`

#### Scenario: createAcpConnection.prompt() with hang recovery
- **WHEN** `createAcpConnection.prompt()` is running and no ACP events arrive for 3 minutes
- **THEN** hang recovery SHALL be attempted with cancel + prompt on the same connection
- **AND** if recovery succeeds, the prompt round continues and eventually returns normally
- **AND** if recovery fails, returns `AcpSessionResult` with `success: false`

### Requirement: Recovery does not interfere with normal session flow

When the agent is producing events normally (within the idle threshold), the hang detection mechanism SHALL NOT introduce any overhead or side effects. The idle timer check SHALL only trigger when the prompt result has not yet resolved.

#### Scenario: Agent completes normally without hang
- **WHEN** an agent session runs to completion without any idle period exceeding the threshold
- **THEN** no recovery events SHALL be written to workflow_log
- **AND** no `coder_recovery_status` SSE events SHALL be emitted
- **AND** the session result SHALL be identical to the current behavior

#### Scenario: Agent completes quickly before idle threshold
- **WHEN** an agent session completes in 30 seconds (well under 3-minute threshold)
- **THEN** the idle timer SHALL be cancelled
- **AND** no recovery logic SHALL execute

### Requirement: Hang recovery workflow_log events

The system SHALL write the following event types to `workflow_log` during the recovery lifecycle:

| Event Type | When | Data Fields |
|---|---|---|
| `acp_session_hang_detected` | Idle threshold exceeded | `{ sessionId, idleMs, attempt }` |
| `acp_session_recovery_started` | Recovery prompt issued | `{ sessionId, attempt }` |
| `acp_session_recovery_succeeded` | Agent produces events after recovery | `{ sessionId, attempt }` |
| `acp_session_recovery_failed` | Recovery failed (cancel timeout or max attempts) | `{ sessionId, attempt, reason }` |

#### Scenario: Full recovery lifecycle logged
- **WHEN** a hang is detected, recovery is attempted, and the agent resumes
- **THEN** workflow_log SHALL contain (in order): `acp_session_hang_detected`, `acp_session_recovery_started`, `acp_session_recovery_succeeded`

#### Scenario: Failed recovery lifecycle logged
- **WHEN** a hang is detected and cancel times out
- **THEN** workflow_log SHALL contain (in order): `acp_session_hang_detected`, `acp_session_recovery_failed` with `reason: 'cancel_timeout'`
