### Requirement: Interrupted work resumes through its replacement dispatch after reconnect

After the new Runner reconnects and reconciliation runs, every work the durable update operation marked recoverably interrupted SHALL either continue through its replacement dispatch or enter an explicit terminal state. The Server SHALL make the replacement dispatch for each interrupted Workflow Agent task eligible to the reconnected Runner, and the replacement execution SHALL run as a new AgentTurn with its own delivery identity. The original attempt identity SHALL NOT be re-dispatched or re-executed.

#### Scenario: Reconnected Runner receives the replacement dispatch

- **WHEN** the new Runner reconnects and reconciliation runs after the Server committed a replacement attempt for an interrupted Workflow Agent task
- **THEN** the Server SHALL offer exactly one replacement dispatch for that work
- **AND** the Runner SHALL execute it as a new AgentTurn with a new delivery identity

#### Scenario: Interrupted AgentJob resumes under the same rule

- **WHEN** reconciliation runs after an AgentJob was marked recoverably interrupted by the update operation
- **THEN** the AgentJob SHALL continue through its replacement dispatch or enter an explicit terminal state
- **AND** it SHALL NOT remain silently parked in a non-dispatchable state with no recovery path

#### Scenario: Original attempt is never re-dispatched

- **WHEN** the replacement attempt has been created for interrupted work
- **THEN** the Server SHALL NOT render or redeliver a dispatch for the original attempt identity
- **AND** the original physical turn SHALL never execute again

#### Scenario: Work that cannot continue reaches an explicit terminal state

- **WHEN** interrupted work has no confirmed interruption receipt and its unresolved fence reaches its settlement deadline after the update
- **THEN** the work SHALL enter its explicit blocked or terminal state named for the unconfirmed result
- **AND** it SHALL NOT remain indefinitely in a silently running state

### Requirement: Duplicate delivery of a dispatch identity never causes duplicate execution

Redelivery of a dispatch identity the Runner already holds or has already executed SHALL NOT cause a second execution. Within one Runner process, work already in flight or awaiting acknowledgement SHALL NOT be re-executed when its dispatch is delivered again. Across Runner restarts, a dispatch that is durably journaled as started but unfinished SHALL remain a fence that refuses replay, and only a distinct replacement delivery identity may execute.

#### Scenario: Reconciliation redelivers work the Runner already holds

- **WHEN** server reconciliation offers a dispatch whose work identity the Runner already reports as in flight or awaiting acknowledgement
- **THEN** the Runner SHALL NOT execute the work a second time
- **AND** the Server SHALL suppress further redelivery of that work identity to the same Runner

#### Scenario: Replacement dispatch survives a mid-execution restart exactly once

- **WHEN** the Runner restarts while a replacement dispatch is journaled as started but unfinished
- **THEN** the journal fence SHALL refuse re-execution of that dispatch identity
- **AND** the work SHALL resolve only through the existing unresolved-result and update-recovery arbitration rather than a second physical execution

#### Scenario: Completed replacement result is replayed after restart

- **WHEN** a replacement execution returned a result that was durably journaled but not yet acknowledged when the Runner restarted
- **THEN** startup SHALL reload and replay that completed result with its original work identity
- **AND** the Server SHALL acknowledge it without triggering another execution
