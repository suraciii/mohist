## Why

Domain events are now durably persisted — #361 moved event writes inside the aggregate state transaction — but nothing delivers them. #361 removed the synchronous in-memory fan-out, so every `[Subscription]` handler is dormant: the cross-aggregate reactions that drive the workflow forward (`WorkflowRunCompleted → CompleteIssue`, stage-lock release, epic auto-done, inbox projection) no longer fire, and `AgentSubscriptionDispatchHandler`'s at-least-once contract is unfulfilled. We need a reliable, self-healing notifier that pushes already-persisted events to subscribers at least once, without becoming a new single point of failure.

This is landing step 3 of the event-bus v2 roadmap (`design/eventbus-v2.md:230`); the #361 review explicitly calls for landing the dispatcher immediately next.

## What Changes

- **New self-driven dispatcher as an Orleans cluster-singleton grain.** `IDispatcherGrain` (fixed key `"dispatcher"`, `IRemindable`) self-wakes on a ~1s reminder. Orleans placement guarantees a single cluster-wide activation (sole notifier); the persisted reminder makes it self-heal after silo crash. This is the first live reminder registration in the codebase.
- **Single-query pull, per-type fan-out, per-row mark.** Each tick runs one UNION query over all event truth tables (`DispatchedAt IS NULL`), fans each event out by type to matching `[Subscription]` handlers via the existing reflection scan + `CloudEventTypeMatcher`, retries transient failures, then marks `DispatchedAt` per row after delivery.
- **At-least-once with per-stream FIFO.** Serial processing in `(Source, Id)` order guarantees each stream (`Source`) is delivered in order with no reorder and no skip. Per-row marking means a crash between delivery and mark leaves the row undelivered → re-delivered on restart → absorbed by idempotent handlers.
- **Best-effort `Pulse()`.** Producers may trigger an immediate tick after commit as a latency optimization (~24h → ~1s); correctness does not depend on it.
- **Re-introduce the DeadLetters layer** (table + store, previously deleted in a test reorg). Poison messages whose retries exhaust move to the dead-letter table, `DispatchedAt` is set so the dispatcher stops retrying, and rows are queryable and manually re-deliverable.
- **Fix closed-generic handler discovery** so all `[Subscription]` handlers (including `EpicAutoDoneHandler`/`EpicCancelledReconcileHandler`) land in the fan-out set.
- **Inject `TimeProvider`** into the dispatcher and replace the wall-clock `DateTimeOffset.UtcNow` in `InMemoryEventBus` (testing 铁律).

## Capabilities

- `event-dispatch`: A single cluster-wide dispatcher SHALL self-wake on a ~1s reminder and, per tick, pull all undelivered event rows (across the WorkflowRun / Issue / Epic / AgentSession truth tables) in one query, fan them out by event type to matching `[Subscription]` handlers, retry transient handler failures, and mark each row delivered (`DispatchedAt`) only after delivery. Delivery SHALL be at-least-once with per-stream FIFO — serial processing in `(Source, Id)` order, no reorder, no skip. The dispatcher SHALL be the sole notifier (producers only append); correctness SHALL NOT depend on any external signal or best-effort ping. A `Pulse()` entry point SHALL allow a best-effort immediate tick as a latency optimization only.
- `dead-letter`: When a handler's retries exhaust on a poison message, the event SHALL be written to the dead-letter table, `DispatchedAt` SHALL be set so the dispatcher stops retrying it, and the dead-letter row SHALL be queryable and manually re-deliverable. Exhaustion on one handler SHALL NOT block delivery of the same event to other matching handlers.

## Impact

- **New components (greenfield, first of kind):**
  - `IDispatcherGrain` + `DispatcherGrain` — Orleans `IGrainWithStringKey`, fixed key `"dispatcher"`, `IRemindable`; establishes the first `RegisterOrUpdateReminder` usage (currently zero in `packages/server/src`).
  - Fan-out DI service: pull `IEventStore.ListUndeliveredAsync` → match via `CloudEventTypeMatcher` → invoke compiled `DispatchDelegate`s → Polly retry → `IDeadLetterStore` on exhaustion → `IEventStore.MarkDispatchedAsync`. Designed as a pure DI service unit-testable with a fake store + injected `TimeProvider` (`eventbus-v2.md:187-189`); the grain is a thin shell.
- **Re-created layer** (verified absent from `MohistDbContext` after a prior test reorg): `DeadLetters` table, `DeadLetterRow`, `IDeadLetterStore`/`DeadLetterStore`, EF migration, `NoopDeadLetterStore` test fake.
- **Existing primitives consumed as-is:** `IEventStore.ListUndeliveredAsync` / `MarkDispatchedAsync` (`Infrastructure/Data/Events/EventStore.cs:180-250`), `[Subscription]` + reflection scan (`Infrastructure/Events/CloudEventBusServiceCollectionExtensions.cs`), `CloudEventTypeMatcher`.
- **Enabling fixes:** closed-generic handler discovery (`CloudEventBusServiceCollectionExtensions.cs:20-24`); `TimeProvider` injection replacing `DateTimeOffset.UtcNow` (`Infrastructure/Events/InMemoryEventBus.cs:75`).
- **DI registration:** `Infrastructure/Hosting/MohistSiloRegistration.cs` (silo container) for the fan-out service + dead-letter store.
- **Reactivated, no code change required:** the dormant `[Subscription]` handlers (`IssueWorkflowCompletionHandler`, `WorkflowStageLockReleaseHandler`, `RunnerWorkflowTerminalStatusHandler`, `EpicAutoDoneHandler`, `EpicCancelledReconcileHandler`, `InboxProjectionHandler`, `HermesIssueNotificationHandler`, `AgentSubscriptionDispatchHandler`) regain a trigger. Notably `AgentSubscriptionDispatchHandler` (`[Subscription(Type="*")]`) gains at-least-once delivery — fulfilling the "future replay re-creates the missed Agent launch" fallback.
- **No web / CLI / runner changes** — pure server-side; no HTTP contract change; no new external dependencies. Retry is hand-rolled with a fixed per-handler attempt cap (see `design.md` D6 — Polly is *not* referenced in the codebase despite an earlier draft of this section claiming so).
