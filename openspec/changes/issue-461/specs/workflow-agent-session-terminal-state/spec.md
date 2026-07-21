### Requirement: Every executed Workflow turn produces one logical terminal event

After every Workflow-source OpenCode turn with an associated AgentSession finishes, the runner SHALL retain one logical `session.closed` event for that turn. A successful runtime turn SHALL use `status: completed`; a failed, interrupted, or timed-out runtime turn SHALL use `status: failed` and preserve the original runtime failure information. Repeated delivery attempts SHALL reuse the retained terminal event rather than generate a different terminal outcome.

#### Scenario: Successful turn produces a completed terminal event

- **WHEN** a Workflow OpenCode turn finishes successfully
- **THEN** the runner SHALL retain a `session.closed` event with `status: completed`
- **AND** SHALL keep it eligible for delivery until positively accepted

#### Scenario: Failed turn preserves its runtime cause

- **WHEN** a Workflow OpenCode turn fails, is interrupted, or times out
- **THEN** the runner SHALL retain a `session.closed` event with `status: failed`
- **AND** the terminal event SHALL preserve the original runtime failure information rather than an event-upload error

### Requirement: Terminal events eventually converge Workflow session state

A locally committed Workflow terminal event that is not positively accepted SHALL remain pending across transient upload failures, runner restart, and server reconnection. Once the server accepts the event, Workflow session reads SHALL resolve the corresponding latest turn to `completed` or `failed` and MUST NOT continue to present that turn as running.

#### Scenario: Completed close is accepted after restart

- **WHEN** a completed turn's terminal upload fails and the runner restarts before acceptance
- **THEN** the restarted runner SHALL resume delivery of the original completed terminal event
- **AND** after acceptance the Workflow AgentSession SHALL report the turn as completed rather than running

#### Scenario: Failed close is accepted after reconnection

- **WHEN** a failed turn's terminal event remains pending during a server disconnection
- **THEN** the runner SHALL resume its delivery after reconnection
- **AND** after acceptance the Workflow AgentSession SHALL report the turn as failed rather than running

### Requirement: Terminal delivery preserves turn order on reused sessions

The runner MUST NOT deliver a Workflow turn's terminal event before that turn's input and runtime activity are positively accepted. When multiple turns reuse one AgentSession, an earlier turn's pending terminal event MUST remain before the later turn's input so a delayed close cannot terminate the newer turn.

#### Scenario: Activity upload fails before close

- **WHEN** a Workflow turn produces a terminal event after an earlier activity event fails delivery
- **THEN** the runner SHALL retain the terminal event behind the failed activity
- **AND** MUST NOT deliver the terminal event until the earlier activity is accepted

#### Scenario: Later turn starts while an earlier close is pending

- **WHEN** a later Workflow turn reuses an AgentSession whose preceding turn still has an unaccepted terminal event
- **THEN** recovery SHALL deliver the preceding turn's terminal event before the later turn's input
- **AND** the preceding close MUST NOT be applied as the terminal event of the later turn

### Requirement: Terminal delivery failure does not control Workflow completion

Failure, timeout, or retry of a `session.closed` Server upload MUST NOT change, suppress, replace, or delay the Workflow turn result until terminal delivery succeeds. The reporter MUST complete local persistence of the terminal event before returning that result, and the terminal event SHALL remain pending independently of Server delivery.

#### Scenario: Successful turn's terminal upload fails

- **WHEN** the OpenCode turn succeeds but its `session.closed` upload fails
- **THEN** the Workflow turn SHALL return its successful runtime result without waiting for terminal acceptance
- **AND** the completed terminal event SHALL remain pending for later delivery

#### Scenario: Failed turn's terminal upload also fails

- **WHEN** the OpenCode turn fails and its `session.closed` upload also fails
- **THEN** the Workflow turn SHALL return the original runtime failure without waiting for terminal acceptance
- **AND** the failed terminal event SHALL remain pending for later delivery
