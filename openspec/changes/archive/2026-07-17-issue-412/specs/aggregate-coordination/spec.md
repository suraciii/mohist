### Requirement: One transaction commits one aggregate and its events

Every Issue, Epic, and WorkflowRun command SHALL persist only the receiving aggregate's state and
that aggregate's newly raised domain events in its database transaction. No transaction SHALL read,
lock, or write another aggregate to make a cross-aggregate operation atomic.

#### Scenario: Epic links an Issue

- **WHEN** Epic accepts a LinkIssue request and commands Issue.AssignEpic
- **THEN** the Issue transaction writes only Issue state and Issue events
- **AND** any later Epic recompute uses its own transaction

#### Scenario: Issue starts work

- **WHEN** Issue starts work
- **THEN** its transaction stores its lifecycle, allocated WorkflowRunId, and IssueWorkStarted
- **AND** it does not create or write WorkflowRun state in that transaction

### Requirement: Durable IssueWorkStarted creates WorkflowRun idempotently

The `IssueWorkStarted` reaction SHALL re-read the Issue and call
`WorkflowRun.EnsureStarted(workflowRunId, { ProjectId, IssueNumber, EpicNumber? })` only when the
event still refers to the Issue's current active run. EnsureStarted SHALL create/start the run in one
WorkflowRun transaction and SHALL be idempotent by WorkflowRunId.

The model SHALL NOT contain `AwaitingBinding`, `WorkflowBindingPending`, binding confirmation, or a
lineage revision.

#### Scenario: Crash before WorkflowRun creation

- **GIVEN** Issue committed IssueWorkStarted and no WorkflowRun row exists
- **WHEN** the durable reaction is delivered or redelivered
- **THEN** EnsureStarted creates and starts exactly that allocated run
- **AND** no transitional Workflow status or Issue pending marker is required

#### Scenario: Reply is lost after WorkflowRun creation

- **GIVEN** EnsureStarted committed but its response was lost
- **WHEN** the reaction retries
- **THEN** EnsureStarted returns the existing equivalent run without duplicate start events

#### Scenario: Delayed start event refers to a superseded run

- **GIVEN** the Issue no longer has the event's WorkflowRunId as its active run
- **WHEN** the old IssueWorkStarted event is delivered
- **THEN** the handler performs no Workflow creation or mutation

### Requirement: WorkflowRun stores only the current local Issue context

WorkflowRun SHALL store `ProjectId`, `IssueNumber`, and nullable `EpicNumber` for correlation,
profile lookup, and event stamping. It SHALL NOT load Issue during an append. Affiliation reactions
SHALL refresh the complete context from current Issue state; terminal or superseded runs SHALL no-op.

#### Scenario: Active Issue changes Epic

- **WHEN** an active Issue commits a new EpicNumber
- **THEN** the durable handler reads the Issue and updates its current WorkflowRun in a separate
  transaction
- **AND** future Workflow events stamp the updated local context

### Requirement: Workflow results are guarded by expected run identity

Workflow completion/failure reactions SHALL command Issue with the expected WorkflowRunId. Issue
SHALL update only when that id is still current, then raise its own terminal event for parent/Epic
progression.

#### Scenario: Old Workflow completion arrives after a new run starts

- **WHEN** completion for the old WorkflowRunId is delivered
- **THEN** Issue rejects or no-ops without completing the new run's work

### Requirement: Synchronous call stacks do not cycle

Aggregates in the same context MAY depend on and command each other, but a synchronous command SHALL
NOT call back into an aggregate already on the stack. Reverse progress SHALL use committed durable
events.

#### Scenario: Epic advances an Issue and later receives completion

- **WHEN** Epic synchronously commands Issue.TryStartFromEpic
- **THEN** Issue does not synchronously call Epic
- **AND** Epic receives later Issue progress through durable events after Issue commit
