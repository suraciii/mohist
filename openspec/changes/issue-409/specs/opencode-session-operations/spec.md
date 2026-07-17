### Requirement: Follow-up runs via `promptAsync` and returns immediately without auto-retry

Follow-up SHALL call `client.session.promptAsync()` on the current physical Session, passing the user's prompt and the optional current model/variant selection, and SHALL return to the caller immediately; the completion process SHALL continue to surface through Session events. When the Session is active, the Follow-up SHALL join the current OpenCode execution; when the Session is idle, the Follow-up SHALL start a user-initiated turn. A routing or admission failure SHALL be returned to the user and MUST NOT be automatically retried or replayed.

#### Scenario: Follow-up returns immediately and streams completion

- **WHEN** a Follow-up is delivered to the current physical Session
- **THEN** the runtime SHALL call `client.session.promptAsync()` and return immediately
- **AND** the completion SHALL continue to appear through Session events

#### Scenario: Follow-up joins an active execution

- **WHEN** a Follow-up is delivered while the Session is active
- **THEN** the Follow-up SHALL join the current OpenCode execution

#### Scenario: A Follow-up routing failure is not retried

- **WHEN** Follow-up routing or admission fails
- **THEN** the failure SHALL be returned to the user
- **AND** the runtime MUST NOT automatically retry or replay the Follow-up

### Requirement: Compact runs via `summarize` in place and does not rotate the session

Compact SHALL be allowed only when the logical AgentSession is idle; when a work turn is active, Compact SHALL return a conflict (sharing the same idle-only boundary as Reset) and SHALL NOT mutate the session. The runtime SHALL first read the current model from the OpenCode Session, then call `client.session.summarize({ sessionID, providerID, modelID })`. Compact MUST NOT create a new physical Session and MUST NOT mint a new session id; the current `runtimeSessionId`, `runtime`, and stable `sessionId` SHALL be unchanged. The runtime MUST NOT provide a synthetic summary fallback. When the Session has no current model, the runtime SHALL return an actionable error and MUST NOT guess. Session and message events produced by Compact SHALL be reconciled into the transcript.

#### Scenario: Compact summarizes the idle session in place

- **WHEN** Compact is applied to an idle session currently bound to `runtimeSessionId` `S`
- **THEN** the runtime SHALL read the current model and call `client.session.summarize()` for `S`
- **AND** the current `runtimeSessionId`, `runtime`, and `sessionId` SHALL remain unchanged
- **AND** the runtime MUST NOT create a new physical Session

#### Scenario: Compact conflicts with an active turn

- **WHEN** Compact is requested on a session whose work turn is active
- **THEN** the runtime SHALL return a conflict referencing the active session
- **AND** SHALL NOT call `summarize` or change the runtime binding

#### Scenario: Compact without a current model errors actionably

- **WHEN** Compact is requested on a session that has no current model
- **THEN** the runtime SHALL return an actionable error
- **AND** SHALL NOT guess a model or fall back to a synthetic summary

### Requirement: Reset creates a new empty physical Session under an expected-binding guard

Reset SHALL be allowed only when the logical AgentSession is idle. The runtime SHALL read the current model/variant if present, then create a new empty OpenCode Session in the same working directory via `client.session.create()`. Only after the new Session is created successfully SHALL the logical Session binding be replaced and a new lineage entry appended; the stable `sessionId` SHALL be preserved. The replacement SHALL be applied only when the command's expected current binding still matches the persisted current binding, so a stale Reset result cannot overwrite a newer binding. When the OpenCode Session is missing, the runtime SHALL report an explicit error with a Reset hint and MUST NOT implicitly create a replacement.

#### Scenario: Reset establishes a replacement when idle and the binding is current

- **WHEN** Reset is issued with an expected current binding that matches the persisted current binding and the session is idle
- **THEN** the runtime SHALL create a new empty OpenCode Session in the same working directory
- **AND** SHALL replace the current binding and append a lineage entry while preserving the `sessionId`

#### Scenario: Reset is rejected when the expected binding is stale

- **WHEN** Reset is issued with an expected current binding that no longer matches the persisted current binding
- **THEN** the runtime SHALL reject the Reset
- **AND** SHALL NOT replace the binding or append lineage

#### Scenario: A missing runtime session reports a Reset hint

- **WHEN** Reset targets an AgentSession whose current Runtime Session binding does not exist
- **THEN** the runtime SHALL report an explicit error indicating the runtime session is missing and prompting a Reset
- **AND** MUST NOT implicitly create a replacement to mask the missing backend

### Requirement: Cancel interrupts the turn via `abort` and preserves the AgentSession

Cancel SHALL interrupt only the currently running turn by calling `client.session.abort()` and SHALL return an `interrupted` result. Cancel MUST NOT delete the AgentSession, its transcript, its lineage, or any persisted state. After Cancel the AgentSession SHALL remain queryable and auditable under the same stable `sessionId`.

#### Scenario: Cancel aborts the turn without deleting the session

- **WHEN** Cancel is requested on a session with an active turn
- **THEN** the runtime SHALL call `client.session.abort()`
- **AND** SHALL return an `interrupted` result
- **AND** the AgentSession, transcript, and lineage SHALL remain present under the same `sessionId`

### Requirement: Command results distinguish not-started from unavailable

Command results SHALL distinguish "definitely not started" from "possibly started but outcome unknown". The runtime SHALL return `notStarted` when the Server cannot find the target Runner connection, the Runner has not yet obtained a runtime connection, or the command is rejected before any runtime call; in that case the Server MAY end the reservation so a later request creates a new operation. Once a runtime call may have begun, a timeout, connection loss, or unconfirmable runtime reply SHALL return `unavailable`, the Server MUST preserve the original operation, and subsequent delivery SHALL reuse the same operation id; the runtime MUST NOT assume a side-effect did not occur by abandoning the reservation.

#### Scenario: Not-started lets the reservation end

- **WHEN** a command is rejected before any runtime call begins (for example the Runner has no runtime connection)
- **THEN** the result SHALL be `notStarted`
- **AND** the Server MAY end the reservation so a later request creates a new operation

#### Scenario: Unavailable preserves the operation id

- **WHEN** a command times out or its runtime reply is unconfirmable after the runtime call may have begun
- **THEN** the result SHALL be `unavailable`
- **AND** the Server SHALL preserve the original operation
- **AND** subsequent delivery SHALL reuse the same operation id

### Requirement: The runtime handler deduplicates repeated delivery of one operation

The runtime handler SHALL deduplicate repeated delivery of the same operation for Compact and Reset. After restart it MAY reconcile a previously started operation, but it MUST NOT blindly re-execute it. For one operation, Compact SHALL record at most one compaction fact and transcript record, and Reset SHALL append at most one replacement lineage entry.

#### Scenario: Repeated Compact delivery records one compaction

- **WHEN** the same Compact operation is delivered more than once
- **THEN** the runtime handler SHALL deduplicate it
- **AND** at most one compaction fact and transcript record SHALL be recorded

#### Scenario: A timed-out Reset does not create a second replacement

- **WHEN** Reset delivery times out after the runtime may have started creating a replacement and is later retried
- **THEN** the runtime SHALL reuse the same operation
- **AND** SHALL append at most one replacement lineage entry for that operation

### Requirement: Workflow-source Session state and diagnostics expose no ACP identity

Workflow-source Session state, command requests, command results, and user-visible diagnostics SHALL NOT surface an ACP Action or an ACP Session identity (no `acpSessionId`). Session commands SHALL route through the Mohist-owned request/result shape and the runtime handler SHALL fulfil them without reading Workflow Action Input or Agent definitions. `mohist/acp-agent` SHALL remain registered solely for the AgentJob path until issue #410; this change MUST NOT add a feature flag, compatibility alias, or ACP fallback to the Workflow source.

#### Scenario: A Workflow-source command carries no ACP identity

- **WHEN** a Session command is issued against a Workflow-source AgentSession
- **THEN** the request, result, and diagnostics SHALL expose no `acpSessionId` or ACP Action identity
- **AND** the runtime handler SHALL fulfil the command from the Mohist-owned request alone

#### Scenario: The Workflow source has no ACP fallback

- **WHEN** the Workflow Inline Agent path executes a turn or handles a Session command
- **THEN** it SHALL use the native OpenCode runtime exclusively
- **AND** `mohist/acp-agent` SHALL remain available only for the AgentJob path
