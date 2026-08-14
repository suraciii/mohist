# Runtime Readiness Witness

### Requirement: New claims require a current runtime witness

The Server MUST resolve the runtime required by a pending work item without
changing owner state before it calls the owner claim operation.

#### Scenario: Unknown witness keeps work pending

- **GIVEN** a pending workflow or AgentJob work item requires runtime `pi`
- **AND** the poll request has no current `pi` readiness witness
- **WHEN** the Server evaluates new claims
- **THEN** it MUST return no dispatch for that work
- **AND** the owner MUST remain pending and unassigned or assigned-pending
- **AND** the Server MUST NOT create a deferred claim record

#### Scenario: Ready witness admits only its runtime

- **GIVEN** one pending work item requires `pi` and another requires
  `opencode`
- **AND** the poll request contains a current `pi` witness with `ready=true`
- **WHEN** the Server evaluates new claims
- **THEN** it MAY claim the Pi work
- **AND** it MUST NOT claim the OpenCode work in that round

#### Scenario: Stale witness is rejected

- **GIVEN** a poll witness has an old connection or runtime generation
- **WHEN** the Runner has registered a newer connection or runtime generation
- **THEN** the Server MUST treat the witness as unknown
- **AND** it MUST NOT claim new work for that runtime

### Requirement: Held work remains reportable while runtime is unhealthy

The Runner MUST continue the poll/report path for work it already holds when a
runtime witness is `ready=false`. It MUST NOT first claim new work and then
wait indefinitely for runtime recovery.

#### Scenario: Unhealthy runtime with held work

- **GIVEN** a work key is in `inFlight` or `awaitingAck`
- **AND** the matching runtime witness is `ready=false`
- **WHEN** the Runner polls
- **THEN** the request MUST include the held work key
- **AND** the Server MUST reconcile that key using existing ownership and ack
  rules
- **AND** no new work may be claimed for that runtime

### Requirement: Readiness cannot settle execution

A readiness witness MUST NOT be used as a terminal result, a replay
authorization, or evidence that a lost runtime submitted or completed input.

#### Scenario: Runtime restart leaves execution uncertain

- **GIVEN** a runtime becomes ready again after a process interruption
- **AND** the original work has no runtime-owned terminal receipt
- **WHEN** the Runner sends a new ready witness
- **THEN** the Server MUST preserve the existing uncertain state
- **AND** it MUST NOT replay the original input or synthesize a result
