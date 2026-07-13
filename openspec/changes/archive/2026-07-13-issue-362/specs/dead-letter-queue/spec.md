### Requirement: DeadLetters table with DeadLetterRow model and migration

The system SHALL include a `DeadLetterRow` entity model, a `DbSet<DeadLetterRow>` registered in `MohistDbContext`, and an EF Core migration that creates the `DeadLetters` table. The table SHALL persist poison event snapshots captured when a handler exhausts its retry budget. The table schema SHALL include: `DeadLetterId` (auto-generated primary key), `Origin` (event table origin: `WorkflowRun`, `Issue`, `Epic`, `AgentSession`), `Source`, `Id` (per-source sequence), `EventId`, `Type`, `Time`, `SpecVersion`, `Subject` (nullable), `DataContentType`, `Data` (JSON), `ExtensionsJson` (JSON), `FailingHandler` (the handler that exhausted retries), `AttemptCount`, `ErrorMessage`, `ErrorStack` (nullable), `DeadLetteredAt`, `Status` (`Pending` / `Redelivering` / `Resolved`), `RedeliveryAttemptedAt` (nullable), `ResolvedAt` (nullable). The table SHALL include indexes on `DeadLetteredAt`, `(FailingHandler, DeadLetteredAt)`, and a unique natural-key index on `(Source, Id, FailingHandler)` so one row per source event and failing handler is enforced at the database boundary.

#### Scenario: DeadLetters table is created by migration

- **WHEN** the EF Core migration is applied to a database
- **THEN** a `DeadLetters` table SHALL exist with all specified columns
- **AND** `DeadLetterId` SHALL be the auto-generated primary key
- **AND** indexes on `DeadLetteredAt`, `(FailingHandler, DeadLetteredAt)`, and `(Source, Id, FailingHandler)` SHALL exist

#### Scenario: DeadLetterRow is registered in MohistDbContext

- **WHEN** the `MohistDbContext` model is inspected
- **THEN** a `DbSet<DeadLetterRow>` SHALL be present
- **AND** `OnModelCreating` SHALL configure the `DeadLetters` table mapping

### Requirement: Poison events written to dead letter table on retry exhaustion

When a handler exhausts its configured retry budget for an event row, the dispatcher SHALL write a dead letter entry to the `DeadLetters` table. The entry SHALL capture: the full event snapshot (`Origin`, `Source`, `Id`, `EventId`, `Type`, `Time`, `SpecVersion`, `Subject`, `DataContentType`, `Data`, `ExtensionsJson`), the `FailingHandler` identifier, the `AttemptCount` (number of retries performed), the `ErrorMessage` and `ErrorStack` from the last failure, and `DeadLetteredAt` (timestamp from the injected `TimeProvider`). After the dead letter entry is written, the dispatcher SHALL stamp `DispatchedAt` on the source event row so it stops appearing in the undelivered query. The dead letter write and the source-event `DispatchedAt` update SHALL commit atomically in a single database transaction so neither side commits unless both do.

#### Scenario: Exhausted retries produce a dead letter entry

- **WHEN** a handler fails and its retry count reaches the configured maximum
- **THEN** a dead letter entry SHALL be written to the `DeadLetters` table
- **AND** the entry SHALL contain the failing handler's identifier, the attempt count, and the error message and stack trace
- **AND** the entry SHALL contain a full snapshot of the event row

#### Scenario: Source row is marked after dead-lettering

- **WHEN** a poison event is written to the dead letter table
- **THEN** the dispatcher SHALL stamp `DispatchedAt` on the source event row
- **AND** the source row SHALL no longer appear in `ListUndeliveredAsync` results

#### Scenario: Each failing handler gets its own dead letter entry

- **WHEN** an event matches two handlers and both exhaust their retry budgets
- **THEN** two separate dead letter entries SHALL be written
- **AND** each entry SHALL identify its own `FailingHandler`

#### Scenario: Natural key keeps one row per source event and failing handler

- **WHEN** two poison events share the same `(Source, Id, FailingHandler)` triple
- **THEN** only one row SHALL exist in the `DeadLetters` table
- **AND** retrying settlement SHALL NOT create a duplicate handler row
- **AND** the dispatcher SHALL NOT retry that row on subsequent ticks

### Requirement: Dead letters queryable by failing handler and time range

The dead letter store SHALL support querying dead letter entries by `FailingHandler` and by `DeadLetteredAt` time range. A query by handler SHALL return all dead letter entries for that handler, ordered by `DeadLetteredAt` descending. A query by time range SHALL return all dead letter entries within the specified range. The operator-facing list query SHALL return only unresolved entries (`Status != Resolved`).

#### Scenario: Query dead letters by failing handler

- **WHEN** a user queries dead letters for a specific handler identifier
- **THEN** all dead letter entries with that `FailingHandler` SHALL be returned
- **AND** the results SHALL be ordered by `DeadLetteredAt` descending

#### Scenario: Query dead letters by time range

- **WHEN** a user queries dead letters within a time range
- **THEN** all dead letter entries with `DeadLetteredAt` within the range SHALL be returned

### Requirement: Operator re-delivery invokes only the failing handler and marks the row resolved

A manual re-delivery operation SHALL re-dispatch the event recorded in the dead letter entry **only to the failing handler recorded by that row**, and on success SHALL mark the dead letter row `Status = Resolved` with `ResolvedAt` set. Already-successful sibling handlers SHALL NOT be invoked again. The dead letter entry SHALL be preserved as a historical record and SHALL NOT be deleted by the re-delivery operation.

#### Scenario: Operator requests re-delivery

- **WHEN** an operator requests re-delivery of a dead-lettered event
- **THEN** the event SHALL be re-dispatched only to the failing handler recorded by that dead-letter row
- **AND** already-successful sibling handlers SHALL NOT be invoked again
- **AND** recovery state (`Status = Redelivering`, `RedeliveryAttemptedAt` set) SHALL be persisted before invoking the handler
- **AND** a successful re-delivery SHALL mark the row `Resolved`
- **AND** a handler-side failure SHALL leave the row `Pending` with the replacement error recorded
- **AND** a persistence failure after handler success SHALL leave an explicit ambiguous redelivery state (row stays `Redelivering`) rather than report false success

#### Scenario: Re-delivery to an unregistered handler is rejected

- **WHEN** an operator requests re-delivery for a row whose `FailingHandler` is not registered for that event type
- **THEN** no handler side effect SHALL run
- **AND** the dead letter row SHALL remain in its prior status (no `Redelivering` transition)

#### Scenario: Re-delivery of an already-resolved row is rejected

- **WHEN** an operator requests re-delivery for a row whose `Status` is already `Resolved`
- **THEN** no handler side effect SHALL run
- **AND** the row SHALL remain `Resolved`
- **AND** the operator surface SHALL return a non-success result that distinguishes "already resolved" from "not found"

### Requirement: Dead-letter operator surface is loopback-only and operator-credential gated

Dead-letter list and re-delivery operations SHALL only be mapped on a loopback listener and SHALL require an operator credential. The default operator credential SHALL be stored outside the API and supplied by the `mo` CLI. Network addresses and forwarding headers SHALL NOT be treated as proof of operator identity.

#### Scenario: Remote caller cannot inspect or replay

- **WHEN** a caller without the operator credential requests a dead-letter list or re-delivery
- **THEN** the server SHALL reject the request
- **AND** no handler side effect SHALL run

#### Scenario: Reverse proxy cannot expose local operator routes

- **WHEN** a caller reaches the loopback listener through a reverse proxy without the operator credential
- **THEN** the server SHALL reject the request
- **AND** no handler side effect SHALL run

### Requirement: Dead-letter list responses expose only bounded, stack-free diagnostic summaries

Dead-letter list responses SHALL expose a redacted summary of the failure (no raw exception stack, stack frames, or file paths) and SHALL NOT return fields that would let a caller reconstruct the original server-side stack.

#### Scenario: Local operator receives redacted diagnostics

- **WHEN** an authenticated operator lists unresolved dead letters
- **THEN** the event identity and a redacted error summary SHALL be returned
- **AND** the raw server exception stack, stack frames, and file paths SHALL NOT be returned

### Requirement: Dead-letter operator access via CLI

Dead-letter query and re-delivery SHALL be available through the server API and the `mo` CLI; internal store or grain methods alone SHALL NOT satisfy the operator contract.

#### Scenario: Operator lists and re-delivers through `mo`

- **WHEN** an operator runs `mo event dead-letter list`
- **THEN** the CLI SHALL authenticate with the local operator credential
- **AND** unresolved dead-letter rows SHALL be displayed with recovery status and MAY be filtered by failing handler
- **WHEN** the operator runs `mo event dead-letter redeliver <id>`
- **THEN** the CLI SHALL authenticate with the local operator credential
- **AND** the corresponding API recovery operation SHALL run and report whether delivery succeeded