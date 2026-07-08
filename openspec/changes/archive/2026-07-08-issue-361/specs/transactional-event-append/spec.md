### Requirement: WorkflowRun event rows appended within the aggregate state transaction

`WorkflowRunStore.SaveAsync(run, events)` SHALL append every emitted WorkflowRun event row inside the same EF Core database transaction that persists the `WorkflowRun` aggregate state. The state write and the event-row writes SHALL commit atomically: on a successful commit both the state row and all event rows SHALL be durable; on any failure neither SHALL persist. The event append SHALL NOT occur in a separate `DbContext` or post-commit loop outside the state transaction.

#### Scenario: Successful save commits both state and events

- **WHEN** `WorkflowRunStore.SaveAsync` persists a run together with one or more events
- **AND** the transaction commits
- **THEN** the workflow run state row SHALL be persisted
- **AND** every emitted event row SHALL be persisted to the event store
- **AND** the event rows SHALL be visible inside the same committed transaction as the state

#### Scenario: Event write failure rolls back the state change

- **WHEN** writing a WorkflowRun event row raises an exception before commit
- **THEN** the transaction SHALL roll back
- **AND** the workflow run state change SHALL NOT be persisted
- **AND** no event row for that save SHALL be persisted

#### Scenario: Crash after commit loses no event

- **WHEN** the state and event rows are committed
- **AND** the process crashes immediately after commit and before any post-commit work
- **THEN** the event rows SHALL remain durable in the database

### Requirement: Issue event rows appended within the aggregate state transaction

When `IssueGrain` saves an issue that emitted events, the issue event rows SHALL be appended inside the same EF Core database transaction that persists the issue aggregate state. The state save and the event-row writes SHALL commit atomically; the event append SHALL NOT run as a separate post-commit publish step on its own `DbContext`.

#### Scenario: Issue state and events commit together

- **WHEN** an issue transition emits one or more events and the grain saves the issue
- **AND** the transaction commits
- **THEN** the issue state row SHALL be persisted
- **AND** every emitted issue event row SHALL be persisted in the same transaction

#### Scenario: Issue event failure rolls back the state change

- **WHEN** writing an issue event row raises an exception before commit
- **THEN** the transaction SHALL roll back
- **AND** the issue state change SHALL NOT be persisted
- **AND** no event row for that save SHALL be persisted

### Requirement: AgentSession lifecycle events appended within the session state transaction and made durable

`AgentSessionStore.SaveAsync(key, state, events)` SHALL append AgentSession lifecycle events as durable event rows inside the same EF Core database transaction that persists the session state. Lifecycle events SHALL be persisted (durable), not delivered solely through a best-effort in-memory bus with zero persistence. The bus-publish-post-commit path SHALL be removed from the session save flow.

#### Scenario: Lifecycle events persisted with session state

- **WHEN** an agent session transition emits lifecycle events
- **AND** the session state is saved
- **THEN** the session state row SHALL be persisted
- **AND** every emitted lifecycle event row SHALL be persisted in the same transaction

#### Scenario: Crash after commit keeps lifecycle events

- **WHEN** the session state and lifecycle event rows are committed
- **AND** the process crashes before any post-commit notification
- **THEN** the lifecycle event rows SHALL remain durable in the database
- **AND** a later read of the session's events SHALL return them

#### Scenario: Lifecycle event write failure rolls back the session state

- **WHEN** writing a session lifecycle event row raises an exception before commit
- **THEN** the transaction SHALL roll back
- **AND** the session state change SHALL NOT be persisted

### Requirement: Event write failures propagate and are never swallowed

The three producers (`WorkflowRunStore`, `IssueGrain`, `AgentSessionGrain`) SHALL NOT silently swallow event-write exceptions. A failure while writing event rows SHALL propagate to the caller and roll back the state transaction. The divergent swallow patterns that exist today — the bare `catch {}` in `WorkflowRunStore`, the log-and-swallow in `IssueGrain`, and the swallow-`InvalidOperationException` plus log-and-swallow in `AgentSessionGrain` — SHALL be removed.

#### Scenario: WorkflowRun event-write exception propagates

- **WHEN** `WorkflowRunStore.SaveAsync` encounters an exception while writing an event row
- **THEN** the exception SHALL propagate out of `SaveAsync`
- **AND** the state transaction SHALL roll back
- **AND** no bare `catch {}` swallowing the exception SHALL remain in the publish path

#### Scenario: Issue event-write exception propagates

- **WHEN** the issue save path encounters an exception while writing an event row
- **THEN** the exception SHALL propagate to the caller
- **AND** the state transaction SHALL roll back
- **AND** no log-and-swallow catch around the event write SHALL remain

#### Scenario: AgentSession lifecycle event-write exception propagates

- **WHEN** `AgentSessionGrain.CommitAsync` encounters an exception while writing a lifecycle event row
- **THEN** the exception SHALL propagate out of `CommitAsync`
- **AND** the state transaction SHALL roll back
- **AND** neither the swallow-`InvalidOperationException` catch nor the log-and-swallow catch SHALL remain around the event write

### Requirement: Identity stamped into event extensions at append time

When an event row is appended, the owning identity SHALL be stamped into the CloudEvent `extensions` at write time. `WorkflowRunStore` SHALL stamp both `projectid` and `issueid` (today it stamps only `projectid` and omits `issueid`). The issue save path (`IssueStore`, taking over from `IssueGrain`) SHALL stamp `projectid`, `issueid`, and `issueno`. Identity SHALL be present on the persisted event row so consumers can read it directly from extensions without performing a reverse database lookup to recover the owning aggregate.

#### Scenario: WorkflowRun event carries both projectid and issueid

- **WHEN** `WorkflowRunStore` appends an event row for a workflow run bound to an issue
- **THEN** the persisted event extensions SHALL contain `projectid`
- **AND** the persisted event extensions SHALL contain `issueid`

#### Scenario: Owning issue readable from extensions without a database lookup

- **WHEN** a consumer processes a WorkflowRun event whose owning issue was stamped at write time
- **THEN** the consumer SHALL be able to read `issueid` directly from the event extensions
- **AND** the consumer SHALL NOT be required to perform a reverse database lookup against the issue table to recover the owning issue
