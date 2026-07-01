## ADDED Requirements

### Requirement: Issue completes when its bound workflow run completes

When a workflow run that is bound to an issue reaches the `Completed` terminal state, the system SHALL transition the owning issue to `Done` by an event-driven path. A cloud-event handler SHALL subscribe to `com.mohist.workflow.run.completed`, resolve the owning issue (`issueId`) from the completed run, and invoke `IIssueGrain.CompleteWorkAsync(workflowRunId)`. This path is symmetric to `EpicAutoDoneHandler` (issue→epic). The transition SHALL NOT depend on a periodic background sweep or on a user opening the issue (lazy read-path reconciliation); it SHALL be verifiable with injectable time asserting that the event subscription drives the transition.

#### Scenario: Completed workflow run drives the bound issue to Done
- **WHEN** a workflow run bound to an `InProgress` issue reaches the `Completed` terminal state and the `com.mohist.workflow.run.completed` event is delivered
- **THEN** the subscription SHALL resolve the owning `issueId` from the run and invoke `CompleteWorkAsync(workflowRunId)`
- **AND** the issue SHALL transition to `Done` without waiting for any scan period or a user opening the issue

#### Scenario: Issue resolution derives from the workflow run
- **WHEN** the completed-event handler processes a run
- **THEN** it SHALL resolve the owning `issueId` from the completed workflow run's issue context
- **AND** SHALL target that issue (and no other) for completion

#### Scenario: Transition is verifiable with injected time
- **WHEN** the completed event is delivered for a run bound to an `InProgress` issue
- **THEN** the transition to `Done` SHALL be observable through the event subscription alone under an injectable `TimeProvider`
- **AND** SHALL NOT require advancing a sweep schedule or simulating a read-path open to take effect

### Requirement: Issue completion trigger is idempotent

Repeated delivery of `com.mohist.workflow.run.completed` for the same run SHALL NOT cause duplicate transitions or errors. Idempotency SHALL be inherited from `CompleteWorkAsync`'s existing status guards: the issue MUST be `InProgress` and the `workflowRunId` MUST match. Once the issue has left `InProgress`, further deliveries SHALL be safe no-ops.

#### Scenario: Duplicate event delivery is a no-op
- **WHEN** the `com.mohist.workflow.run.completed` event is delivered more than once for the same run after the issue has already reached `Done`
- **THEN** each subsequent delivery SHALL be a no-op
- **AND** SHALL NOT throw, re-transition the issue, or mutate any issue field

#### Scenario: Mismatched workflow run id is ignored
- **WHEN** the completed-event handler invokes completion for an issue whose `workflowRunId` does not match the completed run
- **THEN** the invocation SHALL be guarded out by `CompleteWorkAsync`
- **AND** SHALL NOT transition or corrupt the issue

### Requirement: Only the Completed terminal state drives issue completion

The subscription SHALL handle only the `Completed` terminal state. Workflow runs reaching `failed` or `stopped` terminal states SHALL NOT trigger an issue transition through this subscription; their behavior is unchanged and out of scope for this change.

#### Scenario: Failed terminal state does not transition the issue
- **WHEN** a workflow run reaches the `failed` terminal state
- **THEN** the subscription SHALL NOT invoke `CompleteWorkAsync`
- **AND** the issue status SHALL remain unchanged by this path

#### Scenario: Stopped terminal state does not transition the issue
- **WHEN** a workflow run reaches the `stopped` terminal state
- **THEN** the subscription SHALL NOT invoke `CompleteWorkAsync`
- **AND** the issue status SHALL remain unchanged by this path
