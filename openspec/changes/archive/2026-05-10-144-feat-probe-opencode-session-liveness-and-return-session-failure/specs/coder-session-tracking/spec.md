## MODIFIED Requirements

### Requirement: REQ-CST-001 Coder sessions persist liveness fields

Persisted coder session records SHALL store session liveness data needed to understand the current opencode session call without writing session health into issue stage or status.

#### Scenario: New session initializes liveness data
- **WHEN** a coder session record is created for an opencode session call
- **THEN** its status SHALL be `running`
- **AND** `lastDataAt` SHALL be initialized to the session start time
- **AND** `probeSentAt`, `probeDeadlineAt`, and `failureReason` SHALL be empty

#### Scenario: Data refresh is persisted
- **WHEN** runtime observes valid ACP/opencode data for the session
- **THEN** the coder session record SHALL update `lastDataAt`
- **AND** issue `stage` and `status` SHALL NOT be modified by that update

#### Scenario: Probe state is persisted
- **WHEN** runtime transitions a session to `probing`
- **THEN** the coder session record SHALL store status `probing`, `probeSentAt`, and `probeDeadlineAt`

#### Scenario: Failure reason is persisted
- **WHEN** runtime marks a session as failed due to probe timeout, probe send failure, protocol disconnect, or process exit
- **THEN** the coder session record SHALL store status `failed`, terminal timestamp, and `failureReason`

### Requirement: REQ-CST-002 Coder session status remains a session-call state

Coder session status SHALL use only session-call states for this feature: `running`, `probing`, `completed`, `failed`, and `cancelled`.

#### Scenario: No health taxonomy is persisted
- **WHEN** a session is quiet but has not reached the probe threshold
- **THEN** no `quiet`, `stale`, `hung-suspected`, `healthy`, or `recoverable` state SHALL be persisted
