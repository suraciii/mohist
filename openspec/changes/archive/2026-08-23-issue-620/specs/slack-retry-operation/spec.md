### Requirement: The Retry operation is persisted before dispatch

Before dispatching any new execution attempt, the system MUST durably persist a Retry operation record containing a stable operation identity, the pre-allocated identity of the execution attempt it will create, and a pending state. No dispatch to the execution path MUST begin before that record is committed.

#### Scenario: Crash between commit and dispatch leaves a recoverable record

- **WHEN** the Server stops after the Retry operation record is committed but before the dispatch is issued
- **THEN** the durable record MUST exist with its pending state and pre-allocated execution identity
- **AND** recovery MUST be possible from that record alone, without the original click

#### Scenario: No dispatch without a record

- **WHEN** the execution path receives a Retry dispatch request
- **THEN** a committed Retry operation record with a pending state MUST already exist for it
- **AND** an unbacked dispatch MUST NOT be created

### Requirement: The operation receipt is the source of truth for click idempotency

Click idempotency and recovery MUST be decided by the Retry operation receipt, not by the Slack provider inbox, which continues to own only raw Slack message ingress. Concurrent clicks, Slack interaction redelivery, adapter failover, and lost interaction responses MUST all resolve to the same operation and return the same recorded result, creating at most one new execution attempt.

#### Scenario: Concurrent clicks create one attempt

- **WHEN** two Retry clicks for the same action arrive concurrently
- **THEN** both MUST resolve to one Retry operation
- **AND** at most one new execution attempt MUST result
- **AND** both interactions MUST report the same recorded operation result

#### Scenario: Slack redelivery returns the recorded result

- **WHEN** Slack redelivers an interaction whose Retry operation was already committed
- **THEN** the redelivery MUST recover the existing operation and return its recorded result
- **AND** no second attempt MUST be created

#### Scenario: Lost interaction response is recoverable

- **WHEN** the Server commits the Retry operation but its interaction response is lost before Slack records it
- **THEN** a later click or redelivery for the same action MUST return the same recorded operation result
- **AND** no second attempt MUST be created

#### Scenario: Adapter failover preserves the operation

- **WHEN** the interaction arrives through a different adapter instance after failover
- **THEN** acceptance MUST still resolve to the same committed operation
- **AND** the at-most-one-attempt invariant MUST hold

### Requirement: Server restart resumes pending Retry operations

After a Server restart, the system MUST recover committed-but-pending Retry operations and resume the same pending dispatch, without requiring the original click lease or interaction, and without creating a second execution attempt.

#### Scenario: Restart recovery of a committed-pending operation

- **WHEN** the Server restarts while a Retry operation is committed and pending
- **THEN** a recovery worker MUST resume that operation's dispatch using the persisted record
- **AND** the resumed dispatch MUST target the pre-allocated execution identity recorded for that operation
- **AND** exactly one new execution attempt MUST result

### Requirement: Finished operation records are cleaned up under a bounded retention rule

The system MUST clean up finished Retry operation records under a bounded retention rule. Pending operations MUST NOT be removed by that cleanup.

#### Scenario: Finished records are eventually removed

- **WHEN** a Retry operation has reached a finished state and its retention window has elapsed
- **THEN** the operation record MUST be eligible for deletion by the cleanup rule

#### Scenario: Pending records are never cleaned up

- **WHEN** the cleanup rule runs while a Retry operation is still pending
- **THEN** that record MUST be retained
