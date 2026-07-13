## Why

Issue #361 made event rows durable inside the state transaction and severed the synchronous fan-out path — `IEventPublisher.PublishAsync` is now write-only. Every `ICloudEventHandler` (cross-aggregate reactors like `IssueWorkflowCompletionHandler`, `EpicAutoDoneHandler`, `AgentSubscriptionDispatchHandler`, the SignalR `EventBridge`, and the rest) is registered but never invoked. The system has durable events and no notifier. A self-driving dispatcher is the sole missing piece to restore at-least-once delivery and unblock all downstream reactions.

## What Changes

- **New Orleans cluster singleton dispatcher grain.** A single grain keyed by a well-known id, self-activating via a persistent Orleans reminder (~1s tick). The reminder is stored in the already-provisioned `OrleansRemindersTable` (SQLite ADO.NET reminder service configured in `MohistSiloRegistration`). Crash → reminder restarts the grain → next tick resumes from undelivered rows. No external signal required.
- **Single undelivered query per tick.** Reuses the existing `IEventStore.ListUndeliveredAsync` — one `UNION ALL` across `WorkflowRunEvents`, `IssueEvents`, `EpicEvents` (and `AgentSessionEvents`) ordered by `(Source, Id)`, returning rows where `DispatchedAt IS NULL`.
- **Per-row reflective fan-out.** For each undelivered row, the grain reconstructs a `CloudEvent` envelope and iterates the registered `IEnumerable<Subscription>` (populated by `AddCloudEventHandlersFromAssembly`), matching via `CloudEventTypeMatcher` (exact / `|` / `*` / `prefix.*`). Each matching handler's `DispatchDelegate` is invoked in turn.
- **Per-handler retry with exponential backoff.** A handler that throws is retried with increasing delay (configurable, capped). Retry count is tracked per (event row, handler). When retries are exhausted the event is sent to the dead letter table and `DispatchedAt` is stamped — the row stops appearing in the undelivered query.
- **Dead letter table.** The `DeadLetters` table schema already exists in migration model snapshots (columns: `DeadLetterId`, `Origin`, `Source`, `Id`, `EventId`, `Type`, `Time`, `SpecVersion`, `Subject`, `DataContentType`, `Data`, `ExtensionsJson`, `FailingHandler`, `AttemptCount`, `ErrorMessage`, `ErrorStack`, `DeadLetteredAt`) but no source class, `DbSet`, or table-creation migration exists. This change adds the `DeadLetterRow` model, registers it in `MohistDbContext`, and creates the migration. Dead letters are queryable (by handler, by time) and manually retryable (re-null `DispatchedAt` on the source row).
- **Per-stream FIFO.** Serial dispatch ordered by `(Source, Id)` guarantees each stream's events are delivered in sequence. No parallel sharding (non-goal).
- **Per-row delivery marking.** After all matching handlers succeed (or poison rows are dead-lettered), `IEventStore.MarkDispatchedAsync(source, id, now)` stamps `DispatchedAt`. Crash after delivery but before marking → row still `NULL` → redelivered on next tick → handlers must be idempotent on `EventId` (existing contract, unchanged).
- **Best-effort immediate trigger.** Producers may poke the dispatcher grain after commit (`DispatchNowAsync`) for lower latency. Correctness does not depend on this — if the poke is lost, the next reminder tick catches up. Pure latency optimization.
- **Existing handlers unchanged.** All `ICloudEventHandler` implementations keep their `[Subscription]` attributes, `Filter`, and `HandleAsync` signatures. The dispatcher invokes them through the same `Subscription` / `DispatchDelegate` machinery already built by `CloudEventBusServiceCollectionExtensions`.

## Capabilities

- `event-dispatcher`: The self-driving dispatcher grain — cluster singleton, reminder-driven wake-up, undelivered query, per-row reflective fan-out to matching handlers, per-handler retry with backoff, per-stream FIFO ordering, per-row `DispatchedAt` marking, crash-recovery via unmarked rows, and best-effort immediate trigger from producers.
- `dead-letter-queue`: The `DeadLetters` table — `DeadLetterRow` model + `DbSet` + migration to create the table; write poison events on retry exhaustion (with failing handler, attempt count, error message/stack); query dead letters (by handler, by time range); manual re-delivery (re-dispatch the event only to the failing handler recorded on the dead-letter row and mark the row resolved).

## Impact

- **New code**:
  - Dispatcher grain interface + implementation (`Events/` or `Infrastructure/Hosting/` area, following the `EpicReconciliationService` placement convention for cross-slice components).
  - `DeadLetterRow` model class, `DbSet<DeadLetterRow>` in `MohistDbContext`, `OnModelCreating` configuration, and a new EF Core migration to create the `DeadLetters` table.
  - Dead letter store/query service for write + list + retry operations.
  - Dispatcher options (reminder period, retry policy, batch size) configured in `MohistSiloRegistration`.
- **Existing infrastructure reused**:
  - `IEventStore.ListUndeliveredAsync` — already implemented, single `UNION ALL` query.
  - `IEventStore.MarkDispatchedAsync` — already implemented, per-row delivery marker.
  - `IEnumerable<Subscription>` + `DispatchDelegate` — already built by `AddCloudEventHandlersFromAssembly`, the dispatcher consumes this list.
  - `CloudEventTypeMatcher` — already shared by bus validation and `SubscriptionFilter`.
  - Orleans ADO.NET reminder service — already configured (`UseAdoNetReminderService` in `MohistSiloRegistration`), `OrleansRemindersTable` already migrated.
  - `DispatchedAt` column + partial indexes on `WorkflowRunEvents`, `IssueEvents`, `EpicEvents` — already migrated (`AddEventDeliveryDispatchedAt`).
- **Handler activation**: All nine existing `ICloudEventHandler` implementations (`IssueWorkflowCompletionHandler`, `RunnerWorkflowTerminalStatusHandler`, `EpicAutoDoneHandler`, `EpicCancelledReconcileHandler`, `AgentSubscriptionDispatchHandler`, `InboxProjectionHandler`, `EventBridge`, `WorkflowStageLockReleaseHandler`, `HermesIssueNotificationHandler`) gain at-least-once delivery. No handler code changes required — they already implement `Filter` + `HandleAsync` and are registered as singleton `Subscription` entries.
- **`AgentSubscriptionDispatchHandler`**: The `future replay would re-create the missed Agent launch opportunity` fallback (currently a log-and-swallow comment at `AgentSubscriptionDispatchHandler.cs:89-96`) is fulfilled — the dispatcher delivers events at-least-once, so missed Agent launches are recovered on the next tick.
- **`InMemoryEventBus`**: The `Subscription` list and `DispatchDelegate` machinery stay; the bus's `PublishAsync` remains write-only. The dispatcher reads the same subscription list the bus populated.
- **No web / CLI / runner changes** — pure server-side; no HTTP contract change, no new external dependencies.
- **Tests**: spec tests for at-least-once delivery (deliver → crash before mark → redeliver → idempotent absorption), per-stream FIFO, retry exhaustion → dead letter, dead letter query + manual retry. Unit tests for retry policy and handler matching.
