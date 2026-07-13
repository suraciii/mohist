### Requirement: Dispatcher operates as an Orleans cluster singleton grain

The event dispatcher SHALL be an Orleans grain activated as a cluster singleton — exactly one active instance across the silo cluster at any time, keyed by a well-known grain id. Orleans placement SHALL guarantee unique activation; no external leader election or lease mechanism SHALL be required. The dispatcher grain SHALL be the sole component that reads undelivered event rows and fans them out to `ICloudEventHandler` subscriptions. `IEventPublisher.PublishAsync` SHALL remain write-only and SHALL NOT trigger handler invocation.

#### Scenario: Only one dispatcher instance is active cluster-wide

- **WHEN** the silo cluster starts with one or more silos
- **THEN** exactly one dispatcher grain instance SHALL be active across the cluster
- **AND** no second activation of the dispatcher grain SHALL exist simultaneously

#### Scenario: Publish does not dispatch; only the dispatcher dispatches

- **WHEN** a producer calls `IEventPublisher.PublishAsync`
- **THEN** the event row SHALL be appended to the event store
- **AND** no `ICloudEventHandler` SHALL be invoked by the publish call
- **AND** only the dispatcher grain SHALL subsequently read and dispatch the undelivered row

### Requirement: Persistent reminder drives self-wake approximately every second

The dispatcher grain SHALL self-activate via a persistent Orleans reminder with a period of approximately one second. The reminder SHALL be stored in the provisioned `OrleansRemindersTable` (ADO.NET SQLite reminder service). If the silo hosting the dispatcher crashes, the reminder SHALL reactivate the grain on another silo or on restart, resuming the dispatch loop from undelivered rows. The dispatcher's correctness SHALL NOT depend on any external signal, poke, or trigger from producers or other components — even if every external wake signal is lost, the next reminder tick SHALL query and dispatch undelivered rows.

#### Scenario: Reminder persists across silo crash and restores the dispatch loop

- **WHEN** the silo hosting the dispatcher grain crashes
- **AND** another silo is available (or the crashed silo restarts)
- **THEN** the Orleans reminder service SHALL reactivate the dispatcher grain
- **AND** the dispatcher SHALL resume querying undelivered event rows on the next tick

#### Scenario: Correctness does not depend on external triggers

- **WHEN** all external wake signals (producer pokes, manual triggers) are lost
- **THEN** the dispatcher SHALL still query and dispatch undelivered event rows on each reminder tick
- **AND** no undelivered event SHALL remain unprocessed solely because an external signal was not delivered

### Requirement: Single undelivered query per tick covers all event truth tables

Each reminder tick SHALL issue a single query via `IEventStore.ListUndeliveredAsync` that returns undelivered event rows from all event truth tables (`WorkflowRunEvents`, `IssueEvents`, `EpicEvents`, `AgentSessionEvents`) — rows where `DispatchedAt IS NULL`. The query SHALL order results by `(Source, Id)` so that events within the same stream (same `Source`) are returned in per-source `Id` order. The query SHALL be limited to a configurable batch size. When no undelivered rows exist, the tick SHALL complete with near-zero cost.

#### Scenario: Undelivered rows are pulled from all four event tables in one query

- **WHEN** the dispatcher processes a tick
- **AND** undelivered rows exist in one or more of `WorkflowRunEvents`, `IssueEvents`, `EpicEvents`, `AgentSessionEvents`
- **THEN** a single `ListUndeliveredAsync` call SHALL return rows from all tables where `DispatchedAt IS NULL`
- **AND** the results SHALL be ordered by `(Source, Id)`

#### Scenario: No undelivered rows yields an empty result with near-zero cost

- **WHEN** the dispatcher processes a tick
- **AND** no event rows have `DispatchedAt IS NULL`
- **THEN** the query SHALL return an empty result
- **AND** the tick SHALL complete without invoking any handler

### Requirement: Per-row CloudEvent reconstruction and reflective fan-out to matching handlers

For each undelivered event row, the dispatcher SHALL reconstruct a `CloudEvent` envelope from the row's stored fields (`Source`, `EventId`, `Type`, `Time`, `SpecVersion`, `Subject`, `DataContentType`, `Data`, `ExtensionsJson`). The dispatcher SHALL iterate the registered `IEnumerable<Subscription>` collection (populated by `AddCloudEventHandlersFromAssembly`) and match each subscription's type pattern against the event's `Type` via `CloudEventTypeMatcher` (exact, `|`-separated alternatives, `*` catch-all, `prefix.*`). Each matching subscription's `DispatchDelegate` SHALL be invoked in turn. Subscriptions that do not match SHALL be skipped.

#### Scenario: Event is fanned out to all matching handlers

- **WHEN** the dispatcher processes an undelivered event row of type `com.mohist.workflow.run.completed`
- **AND** multiple registered subscriptions match this type (exact match, `*` catch-all, `com.mohist.*` prefix)
- **THEN** each matching subscription's `DispatchDelegate` SHALL be invoked with the reconstructed `CloudEvent`
- **AND** non-matching subscriptions SHALL NOT be invoked

#### Scenario: Event with no matching handlers is still marked delivered

- **WHEN** the dispatcher processes an undelivered event row whose type matches no registered subscription
- **THEN** no handler SHALL be invoked
- **AND** the row SHALL still be marked `DispatchedAt` (the event is considered delivered)

### Requirement: Per-handler retry with exponential backoff on transient failure

When a handler's `DispatchDelegate` throws an exception, the dispatcher SHALL retry that handler with exponential backoff. The backoff delay SHALL increase with each attempt and SHALL be capped at a configurable maximum. Retry attempts SHALL be tracked per (event row, handler) — a failure of one handler SHALL NOT count toward another handler's retry budget. The maximum retry count and backoff parameters SHALL be configurable in dispatcher options registered in `MohistSiloRegistration`.

#### Scenario: Transient handler failure is retried with increasing delay

- **WHEN** a matching handler throws an exception on the first attempt
- **THEN** the dispatcher SHALL retry the same handler
- **AND** the delay before the next attempt SHALL be greater than the previous delay
- **AND** the delay SHALL NOT exceed the configured maximum

#### Scenario: Retry budget is per handler

- **WHEN** an event matches two handlers and handler A fails twice before succeeding
- **AND** handler B fails on its first attempt
- **THEN** handler B's retry count SHALL start from zero
- **AND** handler A's failed attempts SHALL NOT count toward handler B's retry budget

### Requirement: Per-stream FIFO ordering via serial dispatch

The dispatcher SHALL process undelivered rows serially in `(Source, Id)` order. Events sharing the same `Source` (same stream) SHALL be delivered to handlers in per-source `Id` ascending order. The dispatcher SHALL NOT parallelize dispatch across rows or across streams. No event within a stream SHALL be delivered before an earlier event in the same stream has been processed (delivered, dead-lettered, or otherwise marked).

#### Scenario: Events in the same stream are delivered in Id order

- **WHEN** the undelivered query returns two rows with the same `Source` and `Id` values 5 and 9
- **THEN** the row with `Id = 5` SHALL be processed and marked before the row with `Id = 9` is dispatched
- **AND** the handler SHALL NOT observe `Id = 9` before `Id = 5`

### Requirement: Per-row delivery marking after handler resolution

After all matching handlers for an event row have either succeeded or been dead-lettered (retries exhausted), the dispatcher SHALL stamp `DispatchedAt` on the source row via `IEventStore.MarkDispatchedAsync(source, id, now)`. Once `DispatchedAt` is stamped, the row SHALL no longer appear in the undelivered query. The timestamp used for `DispatchedAt` SHALL be obtained from an injected `TimeProvider`, not from wall-clock `DateTimeOffset.UtcNow`.

#### Scenario: Successful delivery marks the row delivered

- **WHEN** all matching handlers for an event row complete without throwing
- **THEN** the dispatcher SHALL call `MarkDispatchedAsync` with the source, id, and current timestamp
- **AND** the row SHALL no longer appear in subsequent `ListUndeliveredAsync` results

#### Scenario: Dead-lettered handler also marks the row delivered

- **WHEN** one or more matching handlers exhaust their retry budget and are written to the dead letter table
- **THEN** the dispatcher SHALL stamp `DispatchedAt` on the source row
- **AND** the row SHALL no longer appear in subsequent `ListUndeliveredAsync` results

### Requirement: At-least-once delivery with crash recovery

The dispatcher SHALL guarantee at-least-once delivery: an event row SHALL be delivered to matching handlers at least once. If the dispatcher crashes after a handler has been invoked but before `DispatchedAt` is stamped, the row SHALL remain `DispatchedAt IS NULL` and SHALL be redelivered on the next tick. Handlers SHALL be idempotent on `EventId` — a redelivered event SHALL be safely absorbed without side-effect duplication.

#### Scenario: Crash after delivery before marking triggers redelivery

- **WHEN** the dispatcher invokes a handler for an event row
- **AND** the handler completes successfully
- **AND** the dispatcher crashes before calling `MarkDispatchedAsync`
- **THEN** the row SHALL still have `DispatchedAt IS NULL`
- **AND** the next dispatcher tick SHALL re-query and redeliver the row to matching handlers

#### Scenario: Redelivered event is absorbed idempotently

- **WHEN** a handler receives the same event (same `EventId`) a second time due to redelivery
- **THEN** the handler SHALL absorb the duplicate without duplicating side effects
- **AND** the handler SHALL not throw or enter an error state solely because the event was already processed

### Requirement: Best-effort immediate trigger from producers

Producers MAY trigger an immediate dispatch cycle by calling the dispatcher grain after their transaction commits. This trigger SHALL be a best-effort latency optimization only — if the trigger is lost, delayed, or the dispatcher is unavailable, the next reminder tick SHALL still catch up. The correctness of at-least-once delivery SHALL NOT depend on the immediate trigger.

#### Scenario: Immediate trigger lowers latency but is not required for correctness

- **WHEN** a producer calls the dispatcher's immediate trigger after commit
- **AND** the dispatcher is available
- **THEN** the dispatcher SHALL initiate a dispatch cycle promptly
- **AND** undelivered rows SHALL be processed sooner than waiting for the next reminder tick

#### Scenario: Lost immediate trigger is recovered by the next tick

- **WHEN** a producer calls the dispatcher's immediate trigger after commit
- **AND** the trigger is lost (dispatcher unavailable, message dropped, or exception swallowed)
- **THEN** the event row SHALL still be `DispatchedAt IS NULL`
- **AND** the next reminder tick SHALL query and dispatch the row

### Requirement: Existing handler contract unchanged

The dispatcher SHALL invoke all existing `ICloudEventHandler` implementations through the same `Subscription` / `DispatchDelegate` machinery built by `AddCloudEventHandlersFromAssembly`. Each handler's `[Subscription]` attribute, `Filter` method, and `HandleAsync` signature SHALL remain unchanged. No handler SHALL require code modifications to receive at-least-once delivery from the dispatcher. The `AgentSubscriptionDispatchHandler` (`[Subscription(Type = "*")]`) SHALL receive at-least-once delivery, fulfilling the "future replay" fallback that was previously a log-and-swallow comment.

#### Scenario: All nine existing handlers receive at-least-once delivery without code changes

- **WHEN** the dispatcher processes an undelivered event row
- **AND** one or more of the nine existing `ICloudEventHandler` implementations match the event type
- **THEN** each matching handler SHALL be invoked via its registered `DispatchDelegate`
- **AND** no handler SHALL require source-code modification to receive delivery

#### Scenario: AgentSubscriptionDispatchHandler recovers missed Agent launches

- **WHEN** an event that would have triggered an Agent launch was previously missed (handler swallowed the failure)
- **AND** the event row remains `DispatchedAt IS NULL`
- **THEN** the dispatcher SHALL redeliver the event to `AgentSubscriptionDispatchHandler`
- **AND** the handler SHALL process the event to re-create the missed Agent launch opportunity

### Requirement: Time-dependent logic uses injected TimeProvider

All time-dependent logic in the dispatcher — `DispatchedAt` timestamp stamping, retry backoff delay computation — SHALL use an injected `TimeProvider` rather than wall-clock `DateTimeOffset.UtcNow`. This SHALL enable deterministic testing with a fake time provider. No wall-clock time access SHALL exist in the dispatch path.

#### Scenario: DispatchedAt timestamp comes from the injected TimeProvider

- **WHEN** the dispatcher stamps `DispatchedAt` on a delivered event row
- **THEN** the timestamp SHALL be obtained from the injected `TimeProvider.GetUtcNow()`
- **AND** the timestamp SHALL NOT be obtained from `DateTimeOffset.UtcNow` or any wall-clock source

#### Scenario: Retry backoff delays are governed by the injected TimeProvider

- **WHEN** the dispatcher waits for a backoff delay before retrying a failed handler
- **THEN** the delay computation SHALL be based on the injected `TimeProvider`
- **AND** tests SHALL be able to advance the fake clock to trigger the retry without real-time waits
