## Why

Domain events are now durably persisted — #361 moved event writes inside the aggregate state transaction — but nothing delivers them. #361 removed the synchronous in-memory fan-out, so every `[Subscription]` handler is dormant: the cross-aggregate reactions that drive the workflow forward (`WorkflowRunCompleted → CompleteIssue`, stage-lock release, epic auto-done, inbox projection) no longer fire, and `AgentSubscriptionDispatchHandler` cannot receive future replay attempts. We need a reliable, self-healing notifier that pushes already-persisted events to subscribers at least once, without becoming a new single point of failure.

This is landing step 3 of the event-bus v2 roadmap (`design/eventbus-v2.md:230`); the #361 review explicitly calls for landing the dispatcher immediately next.

## What Changes

- **New self-driven dispatcher as an Orleans cluster-singleton grain.** `IDispatcherGrain` (fixed key `"dispatcher"`, `IRemindable`) self-wakes on a ~1s reminder. Orleans placement guarantees a single cluster-wide activation (sole notifier); the persisted reminder makes it self-heal after silo crash. This is the first live reminder registration in the codebase.
- **Single-query pull, per-type fan-out, per-row mark.** Each tick runs one UNION query over all event truth tables (`DispatchedAt IS NULL`), fans each event out by type to matching `[Subscription]` handlers via the existing reflection scan + `CloudEventTypeMatcher`, retries transient failures, then marks `DispatchedAt` per row after delivery.
- **At-least-once with per-stream FIFO.** Serial processing in `(Source, Id)` order guarantees each stream (`Source`) is delivered in order with no reorder and no skip. Per-row marking means a crash between delivery and mark leaves the row undelivered → re-delivered on restart → absorbed by idempotent handlers.
- **Best-effort `Pulse()`.** Producers may trigger an immediate tick after commit as a latency optimization (~24h → ~1s); correctness does not depend on it.
- **Re-introduce and harden the DeadLetters layer** (table + store, previously deleted in a test reorg). Poison settlement atomically writes one row per failing handler and marks the source event; explicit recovery state makes operator re-delivery auditable.
- **Durable Agent launch replay.** Trigger-driven AgentJob input and stable runner work identity are persisted before acknowledgement, so silo loss resumes one launch instead of losing or duplicating it.
- **Fix closed-generic handler discovery** so all `[Subscription]` handlers (including `EpicAutoDoneHandler`/`EpicCancelledReconcileHandler`) land in the fan-out set.
- **Inject `TimeProvider`** into the dispatcher and replace the wall-clock `DateTimeOffset.UtcNow` in `InMemoryEventBus` (testing 铁律).

## Capabilities

- `event-dispatch`: A single cluster-wide dispatcher SHALL self-wake on a ~1s reminder and, per tick, pull all undelivered event rows (across the WorkflowRun / Issue / Epic / AgentSession truth tables) in one query, fan them out by event type to matching `[Subscription]` handlers, retry transient handler failures, and mark each row delivered (`DispatchedAt`) only after delivery. Delivery SHALL be at-least-once with per-stream FIFO — serial processing in `(Source, Id)` order, no reorder, no skip. The dispatcher SHALL be the sole notifier (producers only append); correctness SHALL NOT depend on any external signal or best-effort ping. A `Pulse()` entry point SHALL allow a best-effort immediate tick as a latency optimization only.
- `dead-letter`: When a handler's retries exhaust, handler-keyed dead-letter rows and the source `DispatchedAt` SHALL commit atomically. Unresolved rows SHALL be locally queryable and manually re-deliverable with explicit recovery state. Exhaustion on one handler SHALL NOT block siblings.

## Impact

- **New components (greenfield, first of kind):**
  - `IDispatcherGrain` + `DispatcherGrain` — Orleans `IGrainWithStringKey`, fixed key `"dispatcher"`, `IRemindable`; establishes the first `RegisterOrUpdateReminder` usage (currently zero in `packages/server/src`).
  - Fan-out DI service: pull `IEventStore.ListUndeliveredAsync` → match via `CloudEventTypeMatcher` → invoke compiled `DispatchDelegate`s → fixed-cap retry → atomic dead-letter settlement/mark. Designed as a pure DI service unit-testable with fakes + injected `TimeProvider` (`eventbus-v2.md:187-189`); the grain is a thin shell.
- **Re-created layer** (verified absent from `MohistDbContext` after a prior test reorg): `DeadLetters` table, `DeadLetterRow`, `IDeadLetterStore`/`DeadLetterStore`, EF migration, `NoopDeadLetterStore` test fake.
- **Existing primitives consumed as-is:** `IEventStore.ListUndeliveredAsync` / `MarkDispatchedAsync` (`Infrastructure/Data/Events/EventStore.cs:180-250`), `[Subscription]` + reflection scan (`Infrastructure/Events/CloudEventBusServiceCollectionExtensions.cs`), `CloudEventTypeMatcher`.
- **Enabling fixes:** closed-generic handler discovery (`CloudEventBusServiceCollectionExtensions.cs:20-24`); `TimeProvider` injection replacing `DateTimeOffset.UtcNow` (`Infrastructure/Events/InMemoryEventBus.cs:75`).
- **DI registration:** `Infrastructure/Hosting/MohistServiceRegistration.cs` owns the single host-shared application service graph; `MohistSiloRegistration.cs` adds Orleans infrastructure only.
- **Reactivated without changing consumer contracts:** the dormant `[Subscription]` handlers (`IssueWorkflowCompletionHandler`, `WorkflowStageLockReleaseHandler`, `RunnerWorkflowTerminalStatusHandler`, `EpicAutoDoneHandler`, `EpicCancelledReconcileHandler`, `InboxProjectionHandler`, `HermesIssueNotificationHandler`, `AgentSubscriptionDispatchHandler`) regain a trigger. `AgentSubscriptionDispatchHandler` (`[Subscription(Type="*")]`) receives at-least-once event attempts while preserving its best-effort catch-and-log launch contract; replay can therefore recreate a missed launch opportunity without turning an individual launch failure into a dispatcher dead letter.
- **Small operator API/CLI surface; no Web changes.** Loopback-only, operator-credential-protected, stack-redacted dead-letter inspection and recovery are exposed as `GET /api/events/dead-letters`, `POST /api/events/dead-letters/{id}/redeliver`, and `mo event dead-letter list|redeliver`. Retry remains hand-rolled with a fixed per-handler attempt cap.
