### Requirement: The cancellation terminal transition records an IssueCancelled event

The Issue aggregate's cancellation transition (`Close`) carries exactly one domain fact: the issue has been cancelled. It SHALL set the issue status to `Cancelled` and SHALL record an `IssueCancelled` domain event. The event type SHALL be named `IssueCancelled` and SHALL NOT be named `IssueClosed` — the name MUST reflect the transition's sole semantics (cancellation), since the transition's precondition rejects an already-`Done` or archived issue and therefore can never carry a generic "closed" meaning. The cancelled status reached by the transition SHALL remain `IssueStatus.Cancelled` (the `IssueStatus` enum is unchanged).

#### Scenario: Closing an in-progress issue records IssueCancelled and sets Cancelled status

- **WHEN** an issue that is not `Done` and not archived is closed with a reason
- **THEN** the issue status SHALL become `Cancelled`
- **AND** the transition SHALL record exactly one `IssueCancelled` domain event carrying the reason

#### Scenario: Closing an issue that is already done is rejected

- **WHEN** an issue whose status is `Done` is closed
- **THEN** the transition SHALL be rejected and SHALL record no terminal event

### Requirement: The completion terminal transition records an IssueCompleted event

The Issue aggregate's completion transition (`Complete`) carries exactly one domain fact: the issue's work has finished and it has entered `Done`. It SHALL set the issue status to `Done` and SHALL record an `IssueCompleted` domain event. The event type SHALL be named `IssueCompleted` and SHALL NOT be named `IssueWorkCompleted` — the name MUST reflect the completion fact the catalog declares, not the narrower "work-completed" misnomer. The completed status reached by the transition SHALL remain `IssueStatus.Done` (the `IssueStatus` enum is unchanged).

#### Scenario: Completing an in-progress issue records IssueCompleted and sets Done status

- **WHEN** an in-progress issue bound to a workflow run is completed for that run
- **THEN** the issue status SHALL become `Done`
- **AND** the transition SHALL record exactly one `IssueCompleted` domain event carrying the workflow run id

#### Scenario: Completing an issue not bound to the supplied workflow run is a no-op

- **WHEN** an issue's bound workflow run id differs from the supplied workflow run id
- **THEN** the transition SHALL record no event and SHALL leave the status unchanged

### Requirement: The event serializer emits canonical reverse-DNS terminal ids through catalog constants

The serializer that maps Issue domain events to CloudEvents `type` strings SHALL emit the canonical reverse-DNS ids for the two terminal transitions: `com.mohist.issue.cancelled` for `IssueCancelled` and `com.mohist.issue.completed` for `IssueCompleted`. These ids SHALL be sourced from the shared catalog constants (`EventCatalog.ReverseDns.IssueCancelled` / `EventCatalog.ReverseDns.IssueCompleted`) rather than divergent inline string literals, so the serializer and the catalog cannot drift. The persisted storage-facing type of a terminal event SHALL be the renamed variant's CLR type name (`IssueCancelled` / `IssueCompleted`).

#### Scenario: An IssueCancelled event serializes to the cancelled reverse-DNS id

- **WHEN** an `IssueCancelled` event is serialized for the event bus
- **THEN** its CloudEvents `type` SHALL equal `com.mohist.issue.cancelled`

#### Scenario: An IssueCompleted event serializes to the completed reverse-DNS id

- **WHEN** an `IssueCompleted` event is serialized for the event bus
- **THEN** its CloudEvents `type` SHALL equal `com.mohist.issue.completed`

### Requirement: The event catalog declares exactly the terminal events that producers emit

The event catalog SHALL list both terminal issue ids (`com.mohist.issue.cancelled` and `com.mohist.issue.completed`), and each SHALL have a real producer that emits it (the `Close` and `Complete` transitions respectively). The catalog SHALL NOT contain a dead `com.mohist.issue.work-completed` entry that no producer emits, and SHALL NOT contain a `com.mohist.issue.closed` entry. Every catalog-declared terminal id SHALL correspond to exactly one producer, and every terminal-event producer SHALL emit a catalog-declared id.

#### Scenario: Both terminal catalog entries have real producers

- **WHEN** the catalog's terminal issue entries are inspected
- **THEN** `com.mohist.issue.cancelled` SHALL be emitted by the cancellation transition
- **AND** `com.mohist.issue.completed` SHALL be emitted by the completion transition

#### Scenario: No legacy terminal entry remains in the catalog

- **WHEN** the catalog's terminal issue entries are inspected
- **THEN** the catalog SHALL NOT declare `com.mohist.issue.work-completed`
- **AND** the catalog SHALL NOT declare `com.mohist.issue.closed`

### Requirement: The legacy terminal event types and ids no longer exist

The codebase SHALL NOT contain the `IssueClosed` event type, the `IssueWorkCompleted` event type, the `com.mohist.issue.closed` id, or the `com.mohist.issue.work-completed` id. The `IssueEvent` union SHALL contain `IssueCancelled` and `IssueCompleted` in place of the removed legacy variants. Non-terminal event names (`created`, `work-started`, `labels-changed`, etc.) SHALL remain unchanged — only the two misnamed terminal events are renamed, because those are the only terminal facts whose carrier names betrayed their semantics.

#### Scenario: The IssueEvent union exposes the renamed terminal variants

- **WHEN** the `IssueEvent` union's terminal cases are inspected
- **THEN** the union SHALL include `IssueCancelled` and `IssueCompleted`
- **AND** the union SHALL NOT include `IssueClosed` or `IssueWorkCompleted`

#### Scenario: Non-terminal event names are untouched

- **WHEN** the non-terminal issue event types and their reverse-DNS ids are inspected
- **THEN** `created`, `work-started`, `labels-changed`, and the remaining non-terminal names SHALL be unchanged from before this change
