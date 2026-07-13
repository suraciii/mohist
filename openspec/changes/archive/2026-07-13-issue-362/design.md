## Context

This change is step 3 of the event-bus roadmap (`design/eventbus.md`, status: converged). Step 1 (#360) added the storage foundation — `DispatchedAt` columns, partial undelivered indexes, and `IEventStore` delivery-progress ports. Step 2 (#361, done) converged the three producers onto transactional event writes and severed the synchronous fan-out path — `IEventPublisher.PublishAsync` is now write-only. Every `ICloudEventHandler` is registered but never invoked. The system has durable events and no notifier.

Today's state (verified in code):

- `IEventStore.ListUndeliveredAsync` is fully implemented — single `UNION ALL` across `WorkflowRunEvents`, `IssueEvents`, `EpicEvents`, `AgentSessionEvents`, `WHERE "DispatchedAt" IS NULL`, `ORDER BY "Source", "Id"`, `LIMIT @limit` (`EventStore.cs:220-250`). Unused in production.
- `IEventStore.MarkDispatchedAsync` stamps `DispatchedAt` on the source row by source-prefix routing (`EventStore.cs:180-218`). Unused in production.
- `IEnumerable<Subscription>` + `DispatchDelegate` machinery is populated by `AddCloudEventHandlersFromAssembly` (`CloudEventBusServiceCollectionExtensions.cs:52-63`) — nine handlers registered, each with `[Subscription]` type pattern. The `DispatchDelegate` internally calls the handler's `Filter` and `HandleAsync`.
- `CloudEventTypeMatcher.Matches` (`CloudEventTypeMatcher.cs:28-47`) implements exact / `|` / `*` / `prefix.*` matching.
- `InMemoryEventBus.PublishAsync` is write-only — delegates to `IEventStore.AppendAsync`, never reads `_subscriptions` (`InMemoryEventBus.cs:38`).
- Orleans ADO.NET reminder service is configured (`MohistSiloRegistration.cs:33-37`, SQLite, `OrleansRemindersTable` created by initial migration). But **no grain currently registers a reminder** — `RunnerGrain` implements `IRemindable` but its `ReceiveReminder` is a documented no-op (`RunnerGrain.cs:122-129`); all periodic work uses `RegisterGrainTimer`. The dispatcher will be the first real reminder user.
- `DeadLetters` table schema exists in three migration `.Designer.cs` snapshots (`20260708053533`, `20260708113254`, `20260709000000`) with all 17 columns and indexes on `DeadLetteredAt` and `(FailingHandler, DeadLetteredAt)`. But there is **no** `DeadLetterRow` source class, **no** `DbSet<DeadLetterRow>`, **no** table-creation migration, and the entity is absent from the current `MohistDbContextModelSnapshot.cs` — a ghost that was sketched then removed without a migration.
- `TimeProvider` is registered as a singleton in the silo (`MohistSiloRegistration.cs:64`) and constructor-injected into grains/services throughout.
- All nine handlers keep their `[Subscription]` / `Filter` / `HandleAsync` contract — the dispatcher invokes them through the existing machinery, no handler code changes.

Stakeholders: every cross-aggregate reactor (`IssueWorkflowCompletionHandler`, `EpicAutoDoneHandler`, `AgentSubscriptionDispatchHandler`, `EventBridge`, etc.) depends on this dispatcher to receive events. The `AgentSubscriptionDispatchHandler` "future replay" fallback (`AgentSubscriptionDispatchHandler.cs:89-92`) is fulfilled — missed Agent launches are recovered on the next tick.

## Goals / Non-Goals

**Goals:**

- A self-driving dispatcher grain — Orleans cluster singleton, persistent reminder (~1s), crash-self-healing — that reads undelivered rows and fans them out to matching handlers at-least-once.
- Per-handler retry with exponential backoff governed by `TimeProvider` (not wall-clock); retry exhaustion writes to a `DeadLetters` table.
- Per-stream FIFO via serial dispatch ordered by `(Source, Id)`.
- Per-row `DispatchedAt` marking after all handlers resolve (succeed or dead-letter).
- Dead letter table: `DeadLetterRow` model + `DbSet` + migration + store service for write / query / manual-retry.
- Best-effort immediate trigger from producers (pure latency optimization; correctness depends only on the reminder).
- All time-dependent logic uses injected `TimeProvider`; no wall-clock.
- Test coverage: at-least-once (deliver → crash before mark → redeliver → idempotent absorb), per-stream FIFO, retry exhaustion → dead letter, dead letter query + manual retry.

**Non-Goals:**

- Parallel sharding (single dispatcher; future `hash(Source) % N` is additive, per `design/eventbus.md:77`).
- UI real-time push channel changes (EventBridge hub stays as-is; extracting it from the fan-out is a separate follow-up).
- Broker / queue / streaming SDK (no external infrastructure).
- Handler reaction logic changes (handlers stay as-is; they gain at-least-once delivery automatically).
- DTO / HTTP contract changes; no web, CLI, or runner changes.

## Decisions

### D1 — Dispatcher grain: cluster singleton, `IRemindable`, persistent reminder

New `IEventDispatcherGrain : IGrainWithStringKey, IRemindable` with one method:

```csharp
Task DispatchNowAsync();  // best-effort immediate trigger
```

Implementation `EventDispatcherGrain : Grain, IEventDispatcherGrain` keyed by a well-known constant string (following `RunnerRegistryKeys.Global = "__global__"` at `IRunnerRegistryGrain.cs:32-34`). On `OnActivateAsync`, register a persistent reminder with period from `EventDispatcherOptions.ReminderPeriod` (default ~1s) via `RegisterOrUpdateReminder`. `ReceiveReminder` and `DispatchNowAsync` both call the same internal `DispatchCycleAsync`.

**Placement:** `Events/Grains/` (namespace `Mohist.Server.Events.Grains`), mirroring the top-level `Events/` structure (`Events/Hub/`, `Events/Subscriptions/`, `Events/Hosting/`). The dispatcher is cross-slice — it depends on all event types and all handlers — so it does not belong inside any feature slice. This follows the `EpicReconciliationService` placement rationale (`EpicReconciliationService.cs:27-33`).

**Why Orleans reminder (not grain timer):** Grain timers (`RegisterGrainTimer`) are in-memory — lost on silo crash, not restored on activation. Reminders are persisted to `OrleansRemindersTable` and re-registered on silo restart. The spec requires crash-self-healing without external signals; only persistent reminders satisfy this. The dispatcher is the first grain to use this mechanism.

**Why singleton (not per-stream grains):** `design/eventbus.md:77` reserves per-stream sharding (`hash(Source) % N`) as a future additive step. A single singleton grain is the simplest correct design — serial dispatch naturally guarantees per-stream FIFO.

**Alternatives considered:**

- *BackgroundService polling (`EpicReconciliationService` pattern)* — rejected: background services are not cluster-singleton (run on every silo), require external leader election to avoid duplicate dispatch, and are not crash-self-healing across the cluster. Orleans singleton grain gives exactly-one-activation for free.
- *Grain timer* — rejected: in-memory only, lost on crash. Violates the "crash → self-heal" requirement.
- *Per-stream grains* — rejected: future work, explicitly non-goal. Adds key-routing complexity now for no benefit.

### D2 — Dispatch cycle: single query → serial per-row fan-out → mark

`DispatchCycleAsync(ct)`:

1. `rows = IEventStore.ListUndeliveredAsync(options.BatchSize, ct)` — reuses the existing UNION ALL query (`EventStore.cs:220-250`), already ordered by `(Source, Id)`.
2. For each row serially:
   a. Reconstruct `CloudEvent` from the row's stored fields (`Source`, `EventId`, `Type`, `Time`, `SpecVersion`, `Subject`, `DataContentType`, `Data`, `ExtensionsJson`).
   b. Find matching subscriptions: `subscriptions.Where(s => CloudEventTypeMatcher.Matches(s.Type, evt.Type))`. If none → `MarkDispatchedAsync` and continue (the event is considered delivered — nobody to deliver to).
   c. For each matching subscription (by index), invoke `sub.Dispatch(sub.Handler, evt, ct)` with per-handler retry (D3). The `DispatchDelegate` internally calls the handler's `Filter` (short-circuits if false) and `HandleAsync`.
   d. After all handlers resolve (succeed or dead-letter) → `MarkDispatchedAsync(row.Source, row.Id, timeProvider.GetUtcNow())`.
3. Near-zero cost when no undelivered rows exist (empty query result, no handler invocation).

**Serial dispatch = per-stream FIFO:** The query orders by `(Source, Id)` and the grain processes rows serially. Events in the same stream (same `Source`) are delivered to handlers in per-source `Id` ascending order. No handler observes `Id=9` before `Id=5` in the same stream. The grain is not `[Reentrant]` (per `architecture.md:90`), so dispatch cycles do not interleave.

**CloudEvent reconstruction:** `UndeliveredEvent` (from `IEventStore.cs:49-60`) carries all stored fields. The grain builds a `CloudEvent` envelope with `Data` from the stored `JsonElement` and `Extensions` parsed from `ExtensionsJson` — the inverse of what `EventStore.AppendAsync` writes, same field set round-tripped.

### D3 — Cross-tick per-handler retry with TimeProvider-governed backoff

Retry is tracked per (event row, handler) in grain memory:

```
Dictionary<(string Source, long Id), Dictionary<int HandlerIndex, HandlerState>>

HandlerState {
    int AttemptCount;
    DateTimeOffset? NextAttemptTime;  // null = ready to attempt
    HandlerStatus Status;             // Pending | Completed | DeadLettered
}
```

On each tick, for each row and each matching handler (by index):

- If `Status != Pending` → skip (already completed or dead-lettered — avoids re-invoking succeeded handlers).
- If `NextAttemptTime > now` → skip (in backoff; come back next tick).
- Otherwise invoke the handler:
  - **Success** → `Status = Completed`.
  - **Failure** → `AttemptCount++`. If `AttemptCount >= options.MaxAttempts` → write dead letter (D4), `Status = DeadLettered`. Else → `NextAttemptTime = now + Backoff(AttemptCount)`.

After iterating all handlers for a row: if every handler is `Completed` or `DeadLettered` → `MarkDispatchedAsync` and clear the row's state entry. Otherwise → don't mark (row stays undelivered; retried next tick).

**Why cross-tick (not in-tick `Task.Delay`):**

- `design/testing.md:108` plans to ban `Task.Delay` / `Thread.Sleep`. `TimeProvider.Delay` works with `FakeTimeProvider` but **blocks the grain call** — a single failing handler with 5 retries and exponential backoff (1+2+4+8+16 = 31s) would stall the entire dispatch loop for 31s, delaying every other undelivered row.
- Cross-tick retry skips the row during backoff and processes other rows in the meantime. The failing row is re-queried each tick (stays `DispatchedAt IS NULL`), but the grain skips handler invocation until `NextAttemptTime` has passed. Other rows continue flowing.
- Tests advance `FakeTimeProvider` to elapse backoff — no real-time waits.

**Backoff formula:** `Backoff(n) = min(options.BaseBackoff * 2^(n-1), options.MaxBackoff)`. Default: base 1s, max 30s, 5 attempts. All configurable via `EventDispatcherOptions` (D6).

**Per-handler isolation:** `AttemptCount` is keyed by `(row, handlerIndex)`. A failure of handler A does not count toward handler B's budget (spec: "Retry budget is per handler").

**Retry state lifetime:** In-memory only, lost on grain deactivation/crash. On recovery, rows are still `DispatchedAt IS NULL` → re-queried → retry budget resets to 0. Acceptable under at-least-once: the handler gets a fresh retry budget after a crash. The spec does not require persisting retry counts across crashes.

**Alternatives considered:**

- *Polly within-call retry* — rejected: Polly's delay uses `Task.Delay`, conflicting with the planned ban and blocking the grain call. Cross-tick retry fits the grain execution model.
- *Persisted retry state (`[PersistentState]`)* — rejected: adds a storage table and complexity for no spec requirement. Crash resets the budget, which is acceptable under at-least-once.

### D4 — DeadLetters table: `DeadLetterRow` + `DbSet` + migration + store

Create the `DeadLetterRow` entity (in `Infrastructure/Data/Events/DeadLetterRow.cs`), `DbSet<DeadLetterRow> DeadLetters` in `MohistDbContext`, `OnModelCreating` configuration, and a new EF migration `AddDeadLettersTable`. The schema matches the ghost designer snapshots (17 columns, indexes on `DeadLetteredAt` and `(FailingHandler, DeadLetteredAt)`):

| Column | Type | Notes |
|---|---|---|
| `DeadLetterId` | long | auto-generated PK |
| `Origin` | string(32) | `WorkflowRun` / `Issue` / `Epic` / `AgentSession` |
| `Source` | string(256) | |
| `Id` | long | per-source sequence |
| `EventId` | string(128) | |
| `Type` | string(256) | |
| `Time` | DateTimeOffset | |
| `SpecVersion` | string(16) | |
| `Subject` | string(256)? | nullable |
| `DataContentType` | string(64) | |
| `Data` | string (JSON) | |
| `ExtensionsJson` | string (JSON) | |
| `FailingHandler` | string(512) | handler type full name |
| `AttemptCount` | int | |
| `ErrorMessage` | string | |
| `ErrorStack` | string? | nullable |
| `DeadLetteredAt` | DateTimeOffset | from `TimeProvider` |

New `IDeadLetterStore` interface (in `Infrastructure/Events/`):

```csharp
Task WriteAsync(DeadLetterRow row, CancellationToken ct = default);
Task<IReadOnlyList<DeadLetterRow>> ListByHandlerAsync(string handler, int limit = 100, CancellationToken ct = default);
Task<IReadOnlyList<DeadLetterRow>> ListByTimeRangeAsync(DateTimeOffset from, DateTimeOffset to, int limit = 100, CancellationToken ct = default);
Task RetryAsync(long deadLetterId, CancellationToken ct = default);  // re-null source row DispatchedAt
```

`DeadLetterStore` implementation uses `IDbContextFactory<MohistDbContext>`. `RetryAsync` loads the dead letter entry, uses its `(Origin, Source, Id)` to find and re-null `DispatchedAt` on the source event row, and saves. The dead letter entry is preserved as a historical record (not deleted).

**Why a separate store (not inline in the grain):** Keeps the grain thin (dispatch logic only) and the persistence boundary explicit. The store owns "how a dead letter row is written, queried, and retried." The grain calls `IDeadLetterStore.WriteAsync` on retry exhaustion.

### D5 — Best-effort immediate trigger from producers

`IEventDispatcherGrain.DispatchNowAsync()` is a grain call producers can make after their transaction commits. The grain's implementation calls the same `DispatchCycleAsync` as `ReceiveReminder`. Since the grain is serial (not `[Reentrant]`), if a tick is in progress, `DispatchNowAsync` queues behind it — no concurrent dispatch cycles.

Producers obtain the grain via `IGrainFactory.GetGrain<IEventDispatcherGrain>(wellKnownKey)` and call `DispatchNowAsync()` fire-and-forget (best-effort). If the call throws or the grain is unavailable, the exception is swallowed — the next reminder tick catches up. Correctness never depends on this call.

**Wiring:** The three producers (`WorkflowRunStore`, `IssueGrain`/`IssueStore`, `AgentSessionGrain`/`AgentSessionStore`) gain a best-effort poke after commit. This is additive — removing the poke never breaks correctness.

**Alternatives considered:**

- *Orleans streams* — rejected: adds infrastructure for a pure latency optimization. A direct grain call is simpler and sufficient.
- *No immediate trigger (reminder-only)* — rejected: 1s reminder period is acceptable for correctness but adds up to 1s latency to every cross-aggregate reaction. The poke eliminates that latency in the common case at near-zero cost.

### D6 — `EventDispatcherOptions` + registration

```csharp
public sealed class EventDispatcherOptions
{
    public TimeSpan ReminderPeriod { get; set; } = TimeSpan.FromSeconds(1);
    public int BatchSize { get; set; } = 100;
    public int MaxAttempts { get; set; } = 5;
    public TimeSpan BaseBackoff { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(30);
}
```

Registered in `MohistSiloRegistration.ConfigureMohistSilo` (`:52-64`) alongside existing services:

- `silo.Services.Configure<EventDispatcherOptions>(configuration.GetSection("EventDispatcher"))`
- `silo.Services.AddSingleton<IDeadLetterStore, DeadLetterStore>()`

The grain injects `IEventStore`, `IEnumerable<Subscription>`, `IDeadLetterStore`, `TimeProvider`, and `IOptions<EventDispatcherOptions>` — all singletons already registered or added here. Orleans discovers the grain interface and implementation from the same assembly (no explicit grain registration needed, consistent with existing grains).

## Risks / Trade-offs

- **[First Orleans reminder user]** -> No grain currently registers a reminder (`RunnerGrain`'s `ReceiveReminder` is a no-op). Reminder behavior in the test cluster uses `UseInMemoryReminderService` + `ControllableReminderTable` (`GrainTestConfig.cs:211-212`) — provisioned but never exercised by a real reminder-driven grain. *Mitigation:* spec tests for the dispatcher exercise the reminder path; if reminder firing is unreliable with `FakeTimeProvider`, drive dispatch cycles via `DispatchNowAsync` in dispatch-logic specs and test the reminder self-healing path separately (see Open Questions).

- **[In-memory retry state lost on crash]** -> After a grain crash, retry budgets reset to zero. A handler that was 4 attempts into a 5-attempt budget gets a fresh 5 attempts. *Mitigation:* acceptable under at-least-once — the handler eventually succeeds or exhausts again and goes to DLQ. Persisting retry state would add a table and complexity for no spec requirement.

- **[Singleton grain is a throughput bottleneck]** -> One dispatcher, serial dispatch. If event volume is high, a 1s tick may not process all undelivered rows in one cycle. *Mitigation:* `BatchSize` (default 100) caps the query; if rows remain, the next tick continues. Throughput = `BatchSize / tick`. Future: per-stream sharding (non-goal, `design/eventbus.md:77`).

- **[Reminder tick queues while a cycle is in progress]** -> If a dispatch cycle takes >1s, the next reminder tick queues behind it (grain is serial). *Mitigation:* natural self-regulation — queued ticks coalesce (each runs `ListUndeliveredAsync` which returns the latest undelivered set). No unbounded queue growth because ticks complete and drain.

- **[DeadLetters ghost schema in designer snapshots]** -> The `DeadLetters` entity appears in three migration `.Designer.cs` files but not in the current `MohistDbContextModelSnapshot.cs`. Adding the entity + migration must converge cleanly — no unexpected model diff against the ghost. *Mitigation:* `dotnet ef migrations add AddDeadLettersTable` regenerates the aggregate snapshot; verify the diff is only the new table.

- **[AgentSession lifecycle events hit catch-all handlers]** -> `AgentSessionEvents` are persisted (per #361) and included in the undelivered UNION. High-frequency types (`UsageRecorded`, `ContextHealthUpdated`) flow to `AgentSubscriptionDispatchHandler` (`Type = "*"`) and `EventBridge` (`Type = "com.mohist.*"`) on every tick. *Mitigation:* their existing `Filter` logic can short-circuit irrelevant types. If volume becomes a problem, an origin/type filter on the dispatcher is a future tuning knob. See Open Questions.

## Migration Plan

Single-process, local-first deployment under active development (no version-compat constraints).

1. **Schema:** new EF migration `AddDeadLettersTable` — creates the `DeadLetters` table with 17 columns + 2 indexes. No existing table changes (all `DispatchedAt` columns and partial undelivered indexes already migrated by #360 / #361).
2. **Code:** implement D1–D6 together — grain interface + implementation, `DeadLetterRow` model + `DbSet` + `OnModelCreating` + migration, `IDeadLetterStore` + `DeadLetterStore`, `EventDispatcherOptions`, `MohistSiloRegistration` wiring, producer `DispatchNowAsync` pokes. Extend test fakes (`RecordingEventStore` to support seeded `ListUndeliveredAsync` results and record `MarkDispatchedAsync` calls).
3. **Deploy ordering:** #361 already severed synchronous fan-out — cross-aggregate reactions are currently suspended (events durable but nobody dispatches). This issue restores delivery. Land as soon as possible after #361 to close the suspended-reactions window.
4. **Rollback:** revert the commit + apply the migration **down** (`dotnet ef database update <prev>`). The `DeadLetters` table is new; dropping it loses no source-of-truth data (dead letters are an error record). Event rows' `DispatchedAt` markers are unaffected by rollback — rows already marked stay marked; unmarked rows would no longer be dispatched, which is the pre-issue state.
5. **Verification:** `npm test` (server, `TreatWarningsAsErrors` as lint); new spec tests for at-least-once / FIFO / retry-exhaustion / dead-letter-query / manual-retry; unit tests for backoff computation.

## Open Questions

1. **Reminder firing in the test cluster.** `GrainTestConfig` uses `UseInMemoryReminderService` + `ControllableReminderTable` (`GrainTestConfig.cs:211-212`) but no grain has exercised it with `FakeTimeProvider`. Do reminders fire reliably when the fake clock advances? If not, dispatch-logic specs drive cycles via `DispatchNowAsync` and use `FakeTimeProvider.Advance` for backoff elapse; the reminder self-healing path is tested separately (one spec verifying reminder registration + firing). Verify early — this determines the test structure.

2. **Should the dispatcher fan out `AgentSessionEvents`?** **Resolved:** fan out all undelivered rows regardless of origin. `AgentSubscriptionDispatchHandler` (`Type = "*"`) and `EventBridge` (`Type = "com.mohist.*"`') already receive these types and apply their own `Filter` logic to short-circuit irrelevant high-frequency types (`UsageRecorded`, `ContextHealthUpdated`). This is acceptable for now — if volume becomes a problem, an origin/type filter on the dispatcher is a pure additive tuning knob (non-goal for this issue).

3. **`FailingHandler` identifier format.** Handler type full name (`typeof(IssueWorkflowCompletionHandler).FullName`), the `[Subscription]` type pattern, or a human-friendly name? The designer snapshot allows 512 chars. Prefer type full name for unambiguous identification.

4. **Producer poke wiring.** Direct `IGrainFactory.GetGrain<IEventDispatcherGrain>(key).DispatchNowAsync()` in the stores, or a thin `IEventDispatcherTrigger` abstraction to keep the grain factory out of the stores? Direct call is simpler; the abstraction keeps stores decoupled from Orleans. Prefer direct call — stores already depend on `IDbContextFactory`, adding `IGrainFactory` is consistent.
