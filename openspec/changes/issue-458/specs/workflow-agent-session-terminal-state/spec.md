### Requirement: Every executed Workflow OpenCode turn reports a terminal outcome

After a Workflow-source OpenCode turn finishes against an associated physical runtime session, the runner SHALL attempt to report exactly one `session.closed` event to the same Workflow AgentSession. A successful runtime turn SHALL report `status: completed`; a failed, interrupted, or timed-out runtime turn SHALL report `status: failed`.

The terminal event SHALL be reported only after all input and projected runtime-event upload attempts for that turn have settled. When the server accepts and persists the terminal event, Workflow session reads SHALL resolve the latest turn to `completed` or `failed` from that event and MUST NOT continue to present it as running.

#### Scenario: Successful turn converges to completed

- **WHEN** a Workflow OpenCode turn finishes successfully and its terminal event is accepted
- **THEN** the AgentSession SHALL contain a `session.closed` event with `status: completed`
- **AND** Workflow session reads SHALL report the session as completed rather than running

#### Scenario: Failed turn converges to failed

- **WHEN** a Workflow OpenCode turn fails, is interrupted, or times out and its terminal event is accepted
- **THEN** the AgentSession SHALL contain a `session.closed` event with `status: failed`
- **AND** Workflow session reads SHALL report the session as failed rather than running

#### Scenario: Close follows all recorded turn activity

- **WHEN** a Workflow turn produces input, assistant, reasoning, tool, usage, or model events before it finishes
- **THEN** the runner SHALL settle the upload attempts for those events before attempting `session.closed`
- **AND** the accepted `session.closed` event SHALL be the terminal event for that recorded turn

### Requirement: Reused Workflow AgentSessions converge after each turn

When multiple Workflow OpenCode turns reuse the same logical Workflow AgentSession, each turn SHALL record its own input and runtime activity and SHALL attempt its own terminal event. Before accepting activity that resumes a session after `session.closed`, the system MUST persist the pending prior turn as a distinct transcript turn. This boundary MUST NOT depend on a persistence timer firing, elapsed wall time, runner delay, or an explicit test-only flush. Starting a later turn MUST allow the session to record new activity, and the later turn's accepted terminal event SHALL determine the session's latest completed or failed state.

#### Scenario: A later turn reuses a completed Workflow session

- **WHEN** a Workflow OpenCode turn starts on a logical AgentSession whose previous turn recorded `status: completed`
- **THEN** the later turn SHALL record new input and runtime activity in that same logical AgentSession
- **AND** the later turn SHALL attempt a new `session.closed` event after it finishes
- **AND** the latest accepted close event SHALL determine the session's current terminal state

#### Scenario: Back-to-back turns retain distinct transcript boundaries

- **WHEN** two `session.input` / activity / `session.closed` sequences for the same logical and physical Workflow AgentSession are accepted back-to-back without advancing time or manually flushing persistence
- **THEN** the transcript SHALL contain two distinct turns in input order
- **AND** each turn SHALL retain its own input and activity
- **AND** the first turn's input and parts MUST NOT be overwritten, merged into the second turn, or assigned to the second input

#### Scenario: Prior turn persistence fails before resume

- **WHEN** new activity would resume a Workflow AgentSession after `session.closed` but persistence of the pending prior turn fails
- **THEN** the system SHALL reject that new activity without appending it to the pending prior turn
- **AND** the pending prior turn SHALL remain available for a later persistence attempt

### Requirement: Terminal reporting failure does not control Workflow completion

A failed `session.closed` upload MUST be observable but MUST NOT change or suppress delivery of the Workflow turn result. The runner SHALL NOT retry the failed terminal upload or create a local fallback record as part of this change.

#### Scenario: Completed turn's close upload fails

- **WHEN** the OpenCode turn succeeds but its `session.closed` upload fails
- **THEN** the Workflow turn SHALL still return its successful runtime result
- **AND** the terminal reporting failure SHALL be observable
- **AND** the runner SHALL NOT retry or locally persist the failed close event

#### Scenario: Failed turn's close upload also fails

- **WHEN** the OpenCode turn fails and its `session.closed` upload also fails
- **THEN** the Workflow turn SHALL return the original runtime failure
- **AND** the upload failure MUST NOT replace or obscure that runtime failure
