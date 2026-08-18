## Requirements

### Requirement: A confirmed update fence has one durable owner

The Server SHALL persist the current update-interrupt id with the Runner
before it confirms admission has been closed. The Runner SHALL remain fenced
after grain activation until that exact fence is released or a successful
Runner registration completes the handoff.

#### Scenario: Server restarts after confirming an update fence

- **WHEN** an update interrupt was confirmed and the Server activates the
  Runner grain again before the Runner reconnects
- **THEN** new poll and claim admission SHALL remain closed
- **AND** the Server SHALL retain the same update-interrupt id

#### Scenario: A second update races an existing fence

- **WHEN** a different update-interrupt id is submitted while a Runner has a
  confirmed pending fence
- **THEN** the Server SHALL retain the original id and closed admission
- **AND** the later caller SHALL NOT be able to cancel the original fence

### Requirement: Cancellation releases only its own pending update fence

The Server SHALL reopen admission only for a cancel request whose id equals
the current persisted update-interrupt id. It SHALL persist the release before
reporting success. Repeating a successful cancellation is idempotent; a stale
or superseded id SHALL not change admission.

#### Scenario: Candidate activation fails after interrupt confirmation

- **WHEN** the CLI has a confirmed update-interrupt id and managed activation
  or Runner restart fails
- **THEN** it SHALL request cancellation with that exact id
- **AND** the previously connected Runner SHALL be eligible to poll and claim
  again if it is still online

#### Scenario: Old rollback arrives after a successor fence

- **WHEN** a newer update fence is pending for the same Runner
- **AND** a previous update attempts cancellation with its old id
- **THEN** the Server SHALL report the request as superseded
- **AND** the newer fence SHALL remain closed

#### Scenario: Delayed begin arrives after its matching cancellation

- **WHEN** a confirmed update-interrupt id was cancelled successfully
- **AND** a delayed duplicate begin request arrives with that same id
- **THEN** the Server SHALL reject the duplicate as already cancelled
- **AND** it SHALL keep Runner admission open

### Requirement: Fence rollback never settles or replaces work

Cancelling an update-interrupt fence SHALL only change Runner admission. It
SHALL NOT infer a terminal result, remove a started-result fence, re-execute a
work item, or create a replacement Workflow/AgentJob attempt.

#### Scenario: Active work exists when the update transaction rolls back

- **WHEN** active work was listed by a confirmed update interrupt and the CLI
  later cancels that fence
- **THEN** the active work identity and owner state SHALL remain unchanged
- **AND** only normal existing work reconciliation may subsequently progress it
