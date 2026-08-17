### Requirement: The unknown-result deadline is a durable release boundary
The Workflow SHALL treat the persisted deadline of an unresolved Workflow-owned Agent execution as an exactly-once settlement boundary. When the deadline is reached, the Workflow MUST durably preserve the attempt's unresolved or physical-stop disposition and transition the settlement to its blocked outcome without recording task success, task failure, stage failure, WorkflowRun failure, or an implicit operator stop. The boundary SHALL retain the persisted reason, message, last physical observation, first-unknown time, deadline, stop-operation identity when present, and complete execution identity.

#### Scenario: An unresolved execution reaches its deadline
- **WHEN** fake time reaches or passes the persisted deadline and no authoritative Agent result has been accepted
- **THEN** the settlement SHALL become `blocked`
- **AND** the original reason and deadline SHALL remain unchanged
- **AND** the task and WorkflowRun SHALL remain unresolved for late-result arbitration rather than becoming successful or failed

#### Scenario: A stop disposition reaches the deadline
- **WHEN** a stop operation has produced an unknown or stop-unconfirmed settlement and its deadline expires without an authoritative result
- **THEN** the blocked settlement SHALL retain the stop-operation identity and physical stop observation
- **AND** deadline processing MUST NOT claim that the Agent succeeded, failed, or was authoritatively stopped

#### Scenario: A deadline is reconciled more than once
- **WHEN** the settlement reminder is replayed, the Workflow grain is activated again, or multiple callers reconcile the same expired settlement
- **THEN** the release boundary SHALL be applied at most once
- **AND** blocked settlement events and durable state transitions MUST NOT be duplicated or reversed

### Requirement: An expired attempt SHALL release active-work ownership and Runner capacity
After the release boundary is durably committed, the expired attempt MUST cease to be active Workflow work. Workflow assignment and active-work discovery, Runner active-work projections, Runner used-slot accounting, stage or resource ownership, and any other reservation that exists solely to hold the expired attempt SHALL no longer treat it as active. The blocked Workflow MUST NOT claim replacement work, and the expired attempt MUST NOT be eligible for fresh claims or redelivery.

#### Scenario: Deadline release makes a Runner slot available
- **WHEN** a Runner is at its configured workflow capacity and one of its Workflow Agent attempts reaches the unknown-result deadline
- **THEN** the released attempt SHALL no longer contribute to the Runner's used-slot count
- **AND** a different eligible work item SHALL be able to claim the newly available capacity

#### Scenario: Runner status excludes the released attempt
- **WHEN** a consumer reads Runner capacity or active-work status after deadline reconciliation
- **THEN** the expired Workflow attempt MUST be absent from active-work results
- **AND** the capacity view MUST report only work that still owns an active slot

#### Scenario: A blocked attempt is polled after release
- **WHEN** the recorded Runner polls after the attempt is blocked and released, regardless of whether the settlement has a full runtime binding
- **THEN** the Runner MUST receive no redelivery or recovery dispatch for the expired attempt
- **AND** the poll MUST NOT reserve a slot for that attempt

#### Scenario: Deadline release frees other active-work reservations
- **WHEN** deadline cleanup completes for an attempt that owns a stage lock, resource reservation, assignment lease, or equivalent active-work reservation
- **THEN** each such reservation SHALL be released or excluded from active ownership
- **AND** another eligible Workflow SHALL be able to acquire the released resource without stopping the blocked attempt

### Requirement: Deadline cleanup SHALL be idempotent and repairable
Deadline cleanup SHALL reconcile all resources associated with the expired attempt, including dispatch snapshot state, settlement reminder state, active-work ownership, and stage or resource reservations. Cleanup operations MUST be idempotent across reminder replay and grain activation. A failure in one cleanup operation MUST NOT undo the durable blocked-and-released boundary, and a later reconciliation MUST retry the unfinished cleanup without creating work, reacquiring a Runner slot, or emitting another settlement transition.

#### Scenario: Dispatch snapshot deletion fails after release
- **WHEN** the blocked-and-released boundary commits but deletion of the attempt's dispatch snapshot fails
- **THEN** the attempt MUST remain excluded from redelivery, fresh claims, active-work discovery, and slot accounting
- **AND** reminder replay or grain activation SHALL retry snapshot cleanup idempotently

#### Scenario: Reminder removal fails after release
- **WHEN** deadline cleanup releases the attempt but removal of its settlement reminder fails
- **THEN** replay of that reminder SHALL observe the already-blocked settlement and perform only the remaining cleanup
- **AND** it MUST NOT append another blocked event or recreate the released ownership

#### Scenario: Cleanup is interrupted between resource releases
- **WHEN** failure injection interrupts cleanup after one reservation has been released and before another reservation is released
- **THEN** the already-released reservation SHALL remain released
- **AND** a subsequent reconciliation SHALL release the remaining reservations without duplicating side effects or changing the persisted disposition

### Requirement: Workflow and consumer projections SHALL preserve blocked or unknown attention
Before its deadline, an unresolved attempt SHALL remain projected as unknown with its persisted reason and deadline. After its deadline, the task, stage, WorkflowRun attention, event projections, Issue attention, Inbox attention, and Runner-facing status surfaces SHALL expose a blocked, actionable result with the persisted reason or detail and deadline. These projections MUST NOT present the attempt as completed, failed, or as a running Runner reservation after release.

#### Scenario: Consumers read an unresolved attempt before the deadline
- **WHEN** an attempt has an unknown settlement whose persisted deadline is in the future
- **THEN** Workflow and task status SHALL expose the unknown state, reason, execution identity, and deadline
- **AND** the attempt SHALL remain an active reservation until the release boundary is reached

#### Scenario: Consumers read an attempt after the deadline
- **WHEN** an attempt has crossed its deadline and cleanup has released its active ownership
- **THEN** Workflow and task status SHALL expose the blocked state with its persisted reason and deadline
- **AND** Issue, Inbox, and event projections SHALL retain actionable blocked attention without reporting a failure

#### Scenario: Blocked projection is replayed
- **WHEN** status, Issue, Inbox, or event consumers process the same blocked settlement after reminder replay or grain activation
- **THEN** they SHALL observe one consistent blocked outcome and the same reason and deadline
- **AND** replay MUST NOT create duplicate blocked attention or failure notifications

### Requirement: Deadline processing MUST NOT infer an outcome or create replacement execution
The unknown-result deadline SHALL be a liveness boundary only. It MUST NOT infer success or failure from AgentSession, AgentTurn, runtime, Runner, stop, idle, completed, missing, or disconnected observations. It MUST NOT replay the old AgentTurn, redeliver the old dispatch, auto-retry the task, create replacement work, or make the blocked attempt claimable. An explicit operator stop, when committed separately, SHALL retain its existing cancellation and stale-receipt semantics.

#### Scenario: The physical target is idle without an authoritative result
- **WHEN** the bound AgentSession or runtime is observed as idle or completed but no authoritative Workflow result exists at the deadline
- **THEN** the Workflow SHALL settle as blocked or remain unknown according to the persisted deadline state
- **AND** it MUST NOT synthesize task success, task failure, or a replacement execution

#### Scenario: No Runner returns before the deadline
- **WHEN** the recorded Runner never returns and no authoritative result arrives before the deadline
- **THEN** the original attempt SHALL be blocked and released
- **AND** the Workflow MUST NOT retry the old task or create a new TaskRun or Work identity

#### Scenario: An operator explicitly stops the unresolved Workflow
- **WHEN** an operator explicitly stops a Workflow before or after deadline cleanup for an unresolved attempt
- **THEN** the explicit stop disposition SHALL be applied according to the existing stop contract
- **AND** deadline reconciliation MUST NOT turn an explicitly cancelled attempt back into blocked active work or a claimable replacement

### Requirement: The released attempt SHALL remain addressable by its original execution identity
The system SHALL durably retain the original WorkflowRun route and the attempt's TaskRunId, WorkId, RunnerId, AgentSessionId, AgentTurnId, runtime, runtime-session identity, and stop-operation identity when present after deadline release. Releasing active ownership MUST NOT clear or replace these facts. Late-result routing SHALL use the original identity so a grain activation, Runner reconnect, or report replay can address the original attempt without treating it as new work.

#### Scenario: The original identity is read after grain activation
- **WHEN** the Workflow grain is deactivated and reactivated after an attempt has been blocked and released
- **THEN** the persisted settlement SHALL still identify the original WorkflowRun, TaskRun, Work, Runner, AgentSession, AgentTurn, runtime, and runtime session
- **AND** no replacement identity SHALL be allocated for the blocked attempt

#### Scenario: A receipt names a different attempt
- **WHEN** a late receipt uses a different WorkflowRun, TaskRunId, WorkId, or RunnerId from the persisted attempt
- **THEN** the receipt SHALL be acknowledged as stale
- **AND** it MUST NOT mutate the blocked settlement, acquire ownership, or consume capacity

#### Scenario: A physical receipt uses a mismatched bound identity
- **WHEN** a late observation or result uses the original task and work but a different AgentSessionId, AgentTurnId, runtime, or runtime-session identity
- **THEN** the full identity fence SHALL reject it as stale
- **AND** the persisted reason, deadline, and blocked outcome SHALL remain unchanged

### Requirement: A late authoritative result SHALL settle the original attempt at most once
The server SHALL accept a late authoritative Agent result only when it passes the existing full WorkflowRun and execution identity fence. A matching result SHALL settle the original blocked attempt exactly once using the normal success or failure semantics, then clear the unresolved settlement through the terminal result path. The late result MUST NOT reacquire the released assignment, dispatch state, stage or resource reservation, or Runner slot. Duplicate, stale, or superseded receipts MUST be acknowledged as stale and MUST have no side effects.

#### Scenario: A matching authoritative success arrives after release
- **WHEN** a result proving success arrives after the attempt is blocked and released and matches the complete persisted execution identity
- **THEN** the original TaskRun SHALL settle successfully through the normal completion path
- **AND** the blocked settlement SHALL be cleared without creating replacement work
- **AND** the late receipt MUST NOT restore the old Runner assignment or slot

#### Scenario: A matching authoritative failure arrives after release
- **WHEN** a result proving failure arrives after the attempt is blocked and released and matches the complete persisted execution identity
- **THEN** the original TaskRun SHALL settle through the normal failure path
- **AND** the result SHALL be applied to that original attempt rather than creating a second failure or replacement task
- **AND** the late receipt MUST NOT restore the old active-work reservation

#### Scenario: The same authoritative result is delivered twice
- **WHEN** the same authoritative result is delivered again after the original result has settled the attempt
- **THEN** the later delivery SHALL receive a stale acknowledgement
- **AND** it MUST NOT append another terminal event, mutate output or artifacts, advance the Workflow again, or reacquire capacity

#### Scenario: A physical observation follows a blocked settlement
- **WHEN** an idle, stopped, disconnected, or other non-authoritative observation arrives after deadline release
- **THEN** the observation SHALL be acknowledged as stale
- **AND** it MUST NOT reopen the settlement, alter its reason or deadline, or restore active ownership

### Requirement: Deadline liveness behavior SHALL have deterministic time and failure-injection coverage
The server and Runner test suites SHALL cover the deadline boundary with a controllable clock and failure injection for reminder replay, grain activation, dispatch cleanup, reservation release, Runner capacity, and late-result arbitration. The coverage MUST verify both the durable state and the externally visible active-work and status projections.

#### Scenario: Tests advance exactly to the persisted deadline
- **WHEN** a deterministic test advances fake time to the recorded deadline without delivering an authoritative result
- **THEN** the test SHALL verify one blocked-and-released settlement and no success or failure outcome
- **AND** a second reconciliation at the same fake time SHALL leave state, events, capacity, and active-work projections unchanged

#### Scenario: Tests inject cleanup failures
- **WHEN** a deterministic test injects a failure into each deadline cleanup operation in turn and then replays reconciliation
- **THEN** the test SHALL verify that cleanup converges to the released state
- **AND** no retry SHALL produce duplicate events, replacement work, or renewed Runner capacity ownership

#### Scenario: Tests arbitrate a late result
- **WHEN** a deterministic test delivers a matching authoritative result after release followed by duplicate and mismatched receipts
- **THEN** exactly one original-attempt outcome SHALL be applied
- **AND** every duplicate or mismatched receipt SHALL be stale and side-effect free
