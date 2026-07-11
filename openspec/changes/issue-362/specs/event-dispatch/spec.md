### Requirement: Cluster-singleton self-waking sole notifier

The event dispatcher SHALL run as a single cluster-wide active notifier, realized as an Orleans cluster-singleton grain activated under a fixed key. It SHALL self-wake on a persisted reminder with a period of approximately one second. The persisted reminder SHALL make the dispatcher self-heal: after the hosting silo crashes it SHALL reactivate on another silo and resume ticking. The dispatcher SHALL be the sole notifier of subscribers — producers SHALL only append event rows and SHALL NOT trigger notification.

#### Scenario: Single active notifier across the cluster

- **WHEN** multiple silos are running in the cluster
- **THEN** exactly one dispatcher activation SHALL be active cluster-wide
- **AND** every undelivered event row SHALL be delivered by that single activation

#### Scenario: Self-healing after silo crash

- **WHEN** the silo hosting the dispatcher crashes
- **THEN** the persisted reminder SHALL reactivate the dispatcher on another silo
- **AND** undelivered event rows SHALL continue to be delivered on the reminder cadence

#### Scenario: Fresh host starts without a producer ping

- **WHEN** the host starts with a fresh reminder table
- **THEN** it SHALL activate the fixed-key dispatcher grain
- **AND** the dispatcher SHALL register its persisted reminder before startup completes
- **AND** delivery SHALL begin without any producer calling `Pulse()`

### Requirement: Single-query pull of all undelivered event rows

On each tick, the dispatcher SHALL run a single query that pulls every undelivered event row (`DispatchedAt IS NULL`) across all event truth tables — WorkflowRun, Issue, Epic, and AgentSession. The query SHALL order the rows by `(Source, Id)` so that each stream (`Source`) is seen in per-source `Id` order.

#### Scenario: One query covers every event truth table

- **WHEN** the dispatcher ticks and undelivered rows exist in any of the WorkflowRun, Issue, Epic, or AgentSession tables
- **THEN** a single query SHALL return all of them
- **AND** every returned row SHALL have `DispatchedAt IS NULL`

#### Scenario: Rows ordered by stream then per-stream sequence

- **WHEN** the query returns multiple undelivered rows
- **THEN** the rows SHALL be ordered by `(Source, Id)`
- **SO THAT** events belonging to the same stream (`Source`) are processed in per-source `Id` order

### Requirement: Per-type fan-out including closed-generic handlers

The dispatcher SHALL fan each event out by event type to every registered `[Subscription]` handler whose type matches the event (matched via the reflection scan and `CloudEventTypeMatcher`). The fan-out set SHALL include closed-generic handlers (`ICloudEventHandler<TData>`), so that handlers such as those keyed on `IssueCompleted` / `IssueCancelled` are delivered.

#### Scenario: Event delivered to all matching handlers

- **WHEN** the dispatcher processes an undelivered event
- **THEN** every registered `[Subscription]` handler whose type matches the event SHALL be invoked
- **AND** handlers whose type does not match SHALL NOT be invoked

#### Scenario: Closed-generic handlers are included in the fan-out

- **WHEN** the dispatcher processes an event whose type matches a closed-generic `[Subscription]` handler (`ICloudEventHandler<TData>`)
- **THEN** that handler SHALL be invoked alongside any non-generic matching handlers

### Requirement: At-least-once delivery with per-stream FIFO

Delivery SHALL be at-least-once. The dispatcher SHALL process undelivered rows serially in `(Source, Id)` order, guaranteeing that each stream (`Source`) is delivered in order with no reorder and no skip: an event with a higher per-source `Id` SHALL NOT be delivered before a lower one in the same stream is delivered.

#### Scenario: Per-stream order preserved

- **WHEN** a stream has undelivered events with per-source `Id` 1, 2, and 3
- **THEN** the dispatcher SHALL deliver them in that `Id` order
- **AND** SHALL NOT deliver `Id` 3 before `Id` 1 has been delivered

#### Scenario: Mark failure stops progress for the tick

- **WHEN** delivery of per-source `Id` 1 succeeds but persisting its `DispatchedAt` fails
- **THEN** the tick SHALL fail before delivering `Id` 2 from the same source
- **AND** the next tick SHALL retry `Id` 1 first

### Requirement: Per-row delivery mark applied only after delivery

The dispatcher SHALL set `DispatchedAt` on an event row only after that row has been delivered (or routed to the dead-letter table). Marking SHALL be per row, so the progress marker is exact to the delivered event.

#### Scenario: Row marked delivered after delivery

- **WHEN** the dispatcher has delivered an event to its matching handlers
- **THEN** that event row's `DispatchedAt` SHALL be set to the current timestamp
- **AND** no undelivered row SHALL be marked before it has been delivered

### Requirement: Transient handler failures retried per handler

The dispatcher SHALL retry transient handler failures before treating a message as poison. Retry SHALL be tracked per handler, so one handler's transient failure does not affect another handler's delivery of the same event. When a handler's retries are exhausted, the message SHALL be treated as poison and dead-lettered so the dispatcher stops retrying it.

#### Scenario: Transient failure is retried then succeeds

- **WHEN** a matching handler fails transiently and then succeeds on a subsequent attempt
- **THEN** the event SHALL be considered delivered for that handler
- **AND** the row SHALL be marked dispatched once all matching handlers have settled

#### Scenario: Production handler failure reaches the dispatcher

- **WHEN** a required handler side effect fails
- **THEN** its returned Task SHALL fail
- **AND** the dispatcher SHALL retry or dead-letter that handler outcome
- **AND** the handler SHALL NOT hide the failure by logging-and-returning or detached work

### Requirement: Crash recovery via re-delivery, independent of any external signal

Correctness SHALL NOT depend on any external signal or best-effort ping. If the dispatcher crashes after delivering an event but before setting `DispatchedAt`, the row SHALL remain undelivered and SHALL be re-delivered on restart. Handlers SHALL absorb the redelivered duplicate idempotently by event id.

#### Scenario: Deliver-before-mark crash causes re-delivery

- **WHEN** the dispatcher delivers an event but crashes before setting `DispatchedAt`
- **THEN** the row SHALL remain `DispatchedAt IS NULL`
- **AND** the next tick SHALL re-deliver the same event
- **AND** the handler SHALL absorb the duplicate idempotently by event id

#### Scenario: Agent launch duplicate is absorbed by stable identity

- **WHEN** an Agent subscription event is delivered successfully and then re-delivered before its row is marked
- **THEN** the same AgentSession and AgentJob identities SHALL be reused
- **AND** no second Agent launch SHALL be minted for that event/subscription pair

#### Scenario: Correctness holds without any ping

- **WHEN** every producer ping signal is lost
- **THEN** the dispatcher SHALL still deliver all undelivered rows on its next reminder tick

### Requirement: Best-effort Pulse is a latency optimization only

A `Pulse()` entry point SHALL trigger one immediate tick. `Pulse()` SHALL be a latency optimization only — correctness SHALL NOT depend on it. Whether or not any producer calls `Pulse()`, at-least-once delivery SHALL still be guaranteed by the reminder alone.

#### Scenario: Pulse triggers an immediate tick

- **WHEN** a producer calls `Pulse()` after commit
- **THEN** the dispatcher SHALL perform one immediate pull–fan-out–mark cycle

#### Scenario: Absence of Pulse does not affect delivery

- **WHEN** no producer ever calls `Pulse()`
- **THEN** every appended event SHALL still be delivered within one reminder period of being appended

### Requirement: All timestamps come from an injected time source

The dispatcher SHALL obtain every timestamp from an injected `TimeProvider` and SHALL NOT read a wall clock (`DateTimeOffset.UtcNow`). This makes delivery timing deterministic and testable.

#### Scenario: DispatchedAt timestamp sourced from injected TimeProvider

- **WHEN** the dispatcher marks a row delivered
- **THEN** the `DispatchedAt` value SHALL be derived from the injected `TimeProvider`
