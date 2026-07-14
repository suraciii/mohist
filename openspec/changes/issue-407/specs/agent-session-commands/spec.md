### Requirement: Compact keeps the current Runtime Session binding and only records the compaction

Compact SHALL execute as an in-place context compaction: it MUST NOT rotate the Runtime Session binding or the `sessionId`. After Compact, the current `runtimeSessionId` and `runtime` SHALL be unchanged, the AgentSession `sessionId` SHALL be unchanged, and only the compaction fact (summary, strategy, context-window counters) SHALL be recorded. Compact SHALL NOT mint a new session id on the command, the grain, or the response.

#### Scenario: Compact preserves the current runtime binding

- **WHEN** Compact is applied to an idle session currently bound to runtime `R` with `runtimeSessionId` `S`
- **THEN** after Compact the session's current `runtimeSessionId` SHALL still be `S` and `runtime` SHALL still be `R`
- **AND** the `sessionId` SHALL be unchanged
- **AND** a compaction record (strategy, summary, context-window before/after) SHALL be persisted

#### Scenario: Compact appends no new lineage entry

- **WHEN** Compact is applied to a session whose lineage currently has `N` entries
- **THEN** after Compact the lineage SHALL still have exactly `N` entries in the same order
- **AND** no `AgentSessionRuntimeBound` rebind event SHALL be emitted by Compact

### Requirement: Reset requests a replacement Runtime Session under an expected-binding guard

Reset SHALL request a replacement Runtime Session and update the current binding only when the command's expected current binding still matches the persisted current binding. The expected-binding guard MUST reject a stale Reset result so an out-of-date replacement cannot overwrite a newer binding. Reset SHALL append a new lineage entry and preserve the stable `sessionId`. Reset SHALL NOT mint or return a rotated `sessionId`.

#### Scenario: Reset applies a replacement when the expected binding is current

- **WHEN** Reset is issued with an expected current binding that matches the persisted current binding
- **AND** the session is idle
- **THEN** the system SHALL establish a replacement Runtime Session
- **AND** SHALL update the current binding (`runtime`, `runtimeSessionId`) to the replacement
- **AND** SHALL append a new lineage entry while preserving the stable `sessionId`

#### Scenario: Reset is rejected when the expected binding is stale

- **WHEN** Reset is issued with an expected current binding that no longer matches the persisted current binding (because a later binding already took effect)
- **THEN** the Reset SHALL be rejected
- **AND** the current binding SHALL NOT be overwritten by the stale replacement
- **AND** the `sessionId` SHALL be unchanged

### Requirement: Compact and Reset share an idle-only concurrency boundary

Compact and Reset SHALL execute only when the logical AgentSession is idle (no active work turn). When a work turn is active, both operations SHALL return a conflict error identifying the active session and SHALL NOT mutate the session state or binding. The idle check and the conflict error SHALL be identical in shape across Compact and Reset and across both sources.

#### Scenario: Compact rejected while a turn is active

- **WHEN** Compact is requested on a session whose current turn is active
- **THEN** the system SHALL return a conflict error (not a success) referencing the active `sessionId`
- **AND** SHALL NOT change the runtime binding, lineage, or compaction records

#### Scenario: Reset rejected while a turn is active

- **WHEN** Reset is requested on a session whose current turn is active
- **THEN** the system SHALL return a conflict error (not a success) referencing the active `sessionId`
- **AND** SHALL NOT request a replacement Runtime Session or change the binding

### Requirement: Follow-up joins the active turn or starts a user-initiated idle turn without creating work units

Follow-up SHALL deliver the user's text to the current turn when the session is busy, and SHALL start a user-initiated session turn when the session is idle. Follow-up SHALL NOT create a new TaskRun or AgentJob in either case. The session `sessionId` SHALL be unchanged by Follow-up.

#### Scenario: Follow-up joins an active turn

- **WHEN** Follow-up is sent to a session that currently has an active work turn
- **THEN** the text SHALL be delivered to that active turn
- **AND** the system SHALL NOT create a new TaskRun or AgentJob

#### Scenario: Follow-up starts a user-initiated turn when idle

- **WHEN** Follow-up is sent to a session that is idle (no active turn)
- **THEN** the text SHALL start a user-initiated session turn
- **AND** the system SHALL NOT create a new TaskRun or AgentJob
- **AND** the `sessionId` SHALL be unchanged

### Requirement: Cancel interrupts only the current turn and never deletes the AgentSession

Cancel SHALL interrupt only the currently running turn. Cancel MUST NOT delete the AgentSession, its transcript, its lineage, or any persisted state. After Cancel the AgentSession SHALL remain queryable and auditable under the same stable `sessionId`.

#### Scenario: Cancel stops the turn but preserves the session

- **WHEN** Cancel is requested on a session with an active turn
- **THEN** the current turn SHALL be interrupted (best-effort over the execution backend)
- **AND** the AgentSession SHALL remain present and queryable under the same `sessionId`
- **AND** the session's transcript and lineage SHALL NOT be deleted

### Requirement: Commands route through a Mohist-owned request/result shape independent of source

Session commands SHALL be expressed in a Mohist-owned request/result shape that is independent of the Workflow Action Input shape and independent of the Agent definition shape. A runtime-specific handler SHALL be able to fulfil the command from the Mohist-owned request alone, without reading Workflow Action Input or Agent definitions. Both the Workflow source and the Agent-launch source SHALL resolve to the same canonical command routing so the command contract is uniform regardless of entry point.

#### Scenario: Handler fulfils a command without source-specific input

- **WHEN** a session command (compact, reset, follow-up, or cancel) is routed to a runtime-specific handler
- **THEN** the handler SHALL receive a Mohist-owned request/result shape
- **AND** SHALL NOT be required to read Workflow Action Input or Agent definitions to fulfil the command

#### Scenario: Both sources share one command routing contract

- **WHEN** the same command is issued from a Workflow-scoped entry and from an Agent-launch-scoped entry
- **THEN** both SHALL resolve through the same canonical AgentSession routing
- **AND** SHALL observe the same product semantics and response shape

### Requirement: A missing current Runtime Session fails explicitly with a Reset hint

When the current Runtime Session does not exist (for example after an execution-backend replacement of a legacy binding), any session command that requires a live runtime session SHALL fail explicitly with an error that names the missing runtime session and prompts a Reset. The system SHALL NOT fabricate a synthetic continuous conversation to mask the missing backend.

#### Scenario: Missing runtime session produces an explicit Reset hint

- **WHEN** a session command requiring a live runtime session targets an AgentSession whose current Runtime Session binding does not exist
- **THEN** the command SHALL fail with an explicit error indicating the runtime session is missing
- **AND** the error SHALL prompt the caller to Reset
- **AND** the system SHALL NOT synthesize a continuous conversation for the missing backend
