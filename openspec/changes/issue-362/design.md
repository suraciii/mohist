# Design — 落地自驱动事件分发器（issue-362）

> Motivation and product shape live in [`proposal.md`](./proposal.md); requirements in [`specs/event-dispatch/spec.md`](./specs/event-dispatch/spec.md) and [`specs/dead-letter/spec.md`](./specs/dead-letter/spec.md). This document explains **how** to implement them. It is landing step 3 of the [`design/eventbus-v2.md`](../../design/eventbus-v2.md) roadmap.

## Context

#361 made event writes durable (inside the aggregate state transaction) and removed the synchronous in-memory fan-out in `InMemoryEventBus`. Every `[Subscription]` handler is now dormant: the cross-aggregate reactions that drive the workflow forward (`WorkflowRunCompleted → CompleteIssue`, stage-lock release, epic auto-done, inbox projection, Hermes notification) no longer fire, and `AgentSubscriptionDispatchHandler`'s at-least-once contract is unfulfilled. Events are truth on disk; nobody is pushing them to subscribers.

Current state of the relevant pieces (verified against the tree):

- **Pull surface already exists.** `IEventStore.ListUndeliveredAsync` (`Infrastructure/Data/Events/EventStore.cs:220`) runs a **single 4-way `UNION ALL`** over all event truth tables (`WorkflowRunEvents`, `IssueEvents`, `EpicEvents`, `AgentSessionEvents`), `WHERE DispatchedAt IS NULL`, `ORDER BY Source, Id LIMIT N`, returning `UndeliveredEvent`. `MarkDispatchedAsync(source, id, dispatchedAt)` (`EventStore.cs:180`) updates one row. `DispatchedAt` already exists on all four row types (only mutable column; everything else `init`). Partial undelivered indexes already exist.
- **Fan-out surface already exists.** `AddCloudEventHandlersFromAssembly` compiles every `[Subscription]` handler into an `IEnumerable<Subscription>` of `DispatchDelegate`s (`CloudEventBusServiceCollectionExtensions.cs:52`); `CloudEventTypeMatcher.Matches(pattern, type)` matches pipe/wildcard type patterns. The dispatch delegates are compiled once at startup.
- **Publish path is already write-only.** `InMemoryEventBus.PublishAsync` (`InMemoryEventBus.cs:35`) appends a row and never invokes handlers. The dispatcher is greenfield wiring on top of two existing primitives.
- **Reminder subsystem is already enabled** via `UseAdoNetReminderService` (`MohistSiloRegistration.cs:33`), but **`RegisterOrUpdateReminder` is called nowhere in `packages/server/src`** — `RunnerGrain` is the only `IRemindable` and drives presence via a grain timer instead. This issue establishes the first live reminder.
- **DeadLetters layer is absent.** It was added then deleted in the test reorg (`ba50c2089`); only its ghost lingers in one migration Designer snapshot. It must be re-created.
- **Closed-generic handler discovery is broken** at `CloudEventBusServiceCollectionExtensions.cs:20-24`: `typeof(ICloudEventHandler<>).IsAssignableFrom(t)` is always `false` for concrete closed-generic types, so `EpicAutoDoneHandler`/`EpicCancelledReconcileHandler` (which implement only `ICloudEventHandler<TData>`) are silently excluded.
- **`TimeProvider` is registered** in both containers (`MohistSiloRegistration.cs:64`, `MohistServiceRegistration.cs:97`) and overridden with `FakeTimeProvider` in `MohistIntegrationFixture`. The event path has one wall-clock offender left: `InMemoryEventBus.cs:75` (`DateTimeOffset.UtcNow`).

Constraints: no broker, no per-stream grain, no Orleans.Streaming (converged in `eventbus-v2.md`). Testing 铃律: no real time, no real external dependency, fast (<50ms unit, <500ms spec).

## Goals / Non-Goals

**Goals:**
- A sole, self-healing notifier that delivers every persisted event at least once to matching `[Subscription]` handlers, with per-stream FIFO, retry, and a poison-message dead-letter escape.
- Reactivate the 8 existing handlers with **no change to their contracts** (`AgentSubscriptionDispatchHandler` gains at-least-once; the Epic closed-generic handlers actually enter the fan-out set).
- A dispatch core that is a plain DI service, unit-testable with fake stores + injected `TimeProvider`; the grain is a thin shell.

**Non-Goals** (carry over from the issue):
- No parallel sharding (single dispatcher; future pure-additive via `hash(Source)%N`).
- No UI real-time channel extraction (EventBridge stays; landing step 4).
- No handler convergence (removing their internal try/catch, hardening idempotency — landing step 5). Handlers keep swallowing today; the dispatcher aggregates per-handler outcomes on top.
- No broker, no outbox table.

## Decisions

### D1 — Dispatcher = cluster-singleton grain (thin shell) over a pure DI fan-out service

`IDispatcherGrain : IGrainWithStringKey, IRemindable`, fixed key `"dispatcher"`. Orleans placement gives exactly one cluster-wide activation → the sole notifier; the persisted reminder self-heals across silo crash. The grain body is a one-line delegator: `ReceiveReminder` and `PulseAsync` both call `fanOutService.DispatchAsync(ct)`.

The actual logic — pull → match → invoke → retry → dead-letter → mark — lives in an `EventDispatcherService` (plain singleton in the silo container) with `IEventStore`, `IEnumerable<Subscription>`, `IDeadLetterStore`, `ILogger`, `TimeProvider` injected. This is the explicit directive of `eventbus-v2.md:178-189` and makes the core unit-testable without a silo (`MohistDbFixture` provides the real service graph minus the silo; pure unit tests use a fake `IEventStore` + `IDeadLetterStore` + `FakeTimeProvider`).

- **Alternative: per-stream consumer grains.** Rejected (`eventbus-v2.md:89,119-136`): puller count must be constant, not proportional to stream count; 1M workflows must not mean 1M grains.
- **Alternative: signal-driven dispatch (producers trigger).** Rejected (`eventbus-v2.md:114-117`): correctness must not depend on any external signal; a self-waking reminder is the only driver that survives lost pings.
- **Alternative: put the loop directly in the grain.** Rejected: untestable without the silo, violates the 铃律's fake-seam posture and the doc's "薄壳" mandate.

### D2 — Self-wake on a persisted reminder (~1s); `PulseAsync` is best-effort only

On activation the grain registers a reminder via `RegisterOrUpdateReminder("dispatcher-tick", dueTime: ~1s, period: ~1s)` — the first such call in the codebase. `ReceiveReminder` runs one pull–fan-out–mark cycle. `IDispatcherGrain.PulseAsync(ct)` runs the same cycle once, immediately, for producers that want a latency optimization (~24h → ~1s). Correctness never depends on `PulseAsync`; if every ping is lost, the next reminder tick still delivers everything.

- **Alternative: grain timer** (`RegisterGrainTimer`, as `RunnerGrain:139` does). Rejected for the sole notifier: timers are **not persisted** and do **not** reactivate on another silo after crash — the dispatcher must self-heal. Reminder it is.
- **Alternative: external scheduler / cron.** Rejected: reintroduces a single point of failure and an external dependency.

### D3 — One pull via the existing `ListUndeliveredAsync`; dispatcher is table-agnostic

Each tick calls `IEventStore.ListUndeliveredAsync(limit)` — already a single 4-way `UNION ALL` over all truth tables, already `ORDER BY Source, Id`. The dispatcher knows nothing about individual tables; adding a future truth table is a store change, not a dispatcher change.

> **Clarification on the 3-vs-4 tables wording.** The issue body says "三张事件真相表" (`WorkflowRunEvents` + `IssueEvents` + `EpicEvents`); the specs and proposal say four (adding `AgentSessionEvents`). The implementation is unambiguous: `ListUndeliveredAsync` already covers **all four** tables, and the dispatcher is table-agnostic. AgentSession events therefore get delivered too — consistent with the `AgentSubscriptionDispatchHandler` `[Subscription(Type="*")]` contract. No code diverges from the specs. (See Open Questions.)

### D4 — Per-stream FIFO via serial `(Source, Id)` processing; per-row mark after delivery

Process the returned batch serially in the `ListUndeliveredAsync` order (`Source, Id`). For each event: fan out (D5), then `MarkDispatchedAsync(source, id, _time.GetUtcNow())`. Because the batch is globally sorted by `Source` then per-source `Id`, and a row is only marked after its delivery, no higher `Id` in a stream is ever delivered before a lower one → **per-stream FIFO, no reorder, no skip**.

- **Alternative: per-stream cursor / `DeliveryOffsets` table.** Rejected (`eventbus-v2.md:75-77`): a cursor advanced before handlers finish degrades to at-most-once on crash. Per-row `DispatchedAt` is both simplest and correct.
- **Fairness note** (`eventbus-v2.md:142`): `ORDER BY Source, Id` drains one stream before the next; a chatty stream could starve others. Personal-scale event volume makes this a non-issue now; a future "oldest-per-stream round-robin" is pure-additive and out of scope.

### D5 — Per-type fan-out including closed-generic handlers; per-handler retry isolation

For each undelivered event, iterate the registered `IEnumerable<Subscription>`; for each whose `CloudEventTypeMatcher.Matches(sub.Type, evt.Type)` is true, invoke its `DispatchDelegate`. Each matching handler is retried **independently** — one handler's transient failure or exhaustion never affects a sibling's delivery of the same event (spec: "per-handler isolation"). The event row is marked dispatched once all matching handlers have settled (succeeded or dead-lettered).

Fixing the closed-generic discovery bug (D8) is what puts `EpicAutoDoneHandler`/`EpicCancelledReconcileHandler` into this set.

### D6 — Retry: hand-rolled per-handler attempt cap (no Polly in v1)

The proposal asserts "Polly already referenced." **It is not** — `Polly` appears nowhere in `Directory.Packages.props` or any csproj (verified). `eventbus-v2.md` borrows Polly's *shape* (resilience pipeline mount point, `:207`) and leaves scope open (`:224`: "DLQ 是否首期必须… 极简可先只做 at-least-once 重试").

Decision: implement a minimal per-handler retry loop with a **fixed attempt cap** (e.g. 3) and **no wall-clock backoff/jitter** in v1. Rationale:
- The testing 铃律 forbids real time. Polly's value-add is exponential backoff + jitter, which is wall-clock/randomness-based and awkward to make deterministic; a pure attempt cap is trivially testable.
- No net-new central package version, no new transitive surface, no version-pin governance.
- `eventbus-v2.md:224` explicitly permits at-least-once-without-DLQ as the floor; we keep the DLQ but keep retry minimal.
- The retry shape stays a single seam (`IDispatchPolicy` or a local loop), so swapping in Polly later (landing step 6 hardening) is mechanical.

- **Alternative: add Polly now.** Viable but rejected for v1: net-new dependency for a feature (timed backoff) we cannot exercise deterministically under the 铃律, and the doc frames it as borrowed design rather than a required package.
- **Alternative: no retry, straight to DLQ.** Rejected: defeats the point of tolerating transient handler failures (spec: "transient failures retried per handler").

### D7 — DeadLetters layer re-created; exhaustion sets `DispatchedAt`

Re-introduce the layer deleted in `ba50c2089`, modeled on its historical shape:
- `DeadLetterRow` (`Infrastructure/Data/Events/DeadLetterRow.cs`): `DeadLetterId` (PK, `long`), a snapshot of the event (source/id/eventId/type/data/extensions), `FailingHandler`, `Error`/`Reason`, `AttemptCount`, `DeadLetteredAt` (`DateTimeOffset`).
- `DbSet<DeadLetterRow> DeadLetters` in `MohistDbContext`; key `DeadLetterId`; indexes on `DeadLetteredAt` and `(FailingHandler, DeadLetteredAt)` (matches the queryable-by-handler requirement).
- `IDeadLetterStore` with `WriteAsync(row)`, `QueryAsync(handlerFilter?)`, plus `NoopDeadLetterStore` test fake.
- EF migration creating the `DeadLetters` table.

Flow on exhaustion (per handler): write a `DeadLetterRow` (failing handler + error + attempt count), then the dispatcher sets `DispatchedAt` on the **original event row** so it stops retrying that event on subsequent ticks. Because exhaustion is tracked per handler (D5), a sibling handler that succeeded does **not** get dead-lettered; only the failing handler's outcome is recorded. A dead-lettered event is manually re-deliverable (operator action re-dispatches it to its matching handlers), satisfying the spec.

> Re-delivery semantics (interpretation): "re-deliver" re-dispatches to **all matching handlers** (fresh dispatch), not just the one that failed — the dead-letter row is an operator-facing record of *who* failed, not a per-handler work queue. See Open Questions.

### D8 — Fix closed-generic handler discovery in the reflection scan

At `CloudEventBusServiceCollectionExtensions.cs:20-24`, replace the dead `typeof(ICloudEventHandler<>).IsAssignableFrom(t)` check (always false for closed generics) with a proper closed-generic scan: `t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICloudEventHandler<>))`. The rest of the registration path (`:40-49` closed-generic registration, `:52-63` subscription building, `MakeTypedDelegate<TData>` deserialization) already exists and is correct — only discovery was broken.

- **Alternative: require closed-generic handlers to also implement the non-generic `ICloudEventHandler`.** Rejected: ergonomic regression, loses the typed `CloudEvent<TData>` deserialization the typed path provides.

### D9 — Inject `TimeProvider`; eliminate the last wall-clock in the event path

`EventDispatcherService` and `DispatcherGrain` take `TimeProvider` (constructor → `readonly TimeProvider _time` → `_time.GetUtcNow()` for `DispatchedAt` and `DeadLetteredAt`), mirroring the pervasive existing idiom (`WorkflowGrain.cs:32,45`, etc.). Replace `DateTimeOffset.UtcNow` at `InMemoryEventBus.cs:75` with the same injected `TimeProvider`. `TimeProvider.System` is already registered; tests inject `FakeTimeProvider`.

### D10 — DI registration site: `MohistSiloRegistration.cs`

Register in the **silo** container (the dispatcher is a grain and must resolve its dependencies from the silo graph, not the web container): `EventDispatcherService` (singleton), `IDeadLetterStore` → `DeadLetterStore` (+ `NoopDeadLetterStore` as the test fake via `TryAdd`), and the grain implementation. `AddCloudEventHandlersFromAssembly` (already called at `:62`) feeds `IEnumerable<Subscription>` to the service.

## Risks / Trade-offs

- **[First live reminder in the codebase]** → Mitigation: reminder service is already configured (`UseAdoNetReminderService`); in spec tests, assert reminder registration + drive a tick via `PulseAsync` (the pure-DI path) rather than asserting real reminder timing, and add one integration spec (`MohistIntegrationFixture`, real silo) that the reminder actually fires. Keep reminder cadence configurable.
- **[Polly absence vs. proposal claim]** → Mitigation: hand-rolled retry (D6); record the decision here so reviewers don't expect Polly. If timed backoff is later required, it lands as a single seam swap.
- **[Single dispatcher = throughput ceiling]** → Mitigation: `LIMIT N` per tick caps work; no sharding is a Non-Goal. The `hash(Source)%N` extension is modeled and pure-additive (`eventbus-v2.md:155-161`).
- **[Deliver-before-mark crash → re-delivery → duplicate]** → Mitigation: this is by design (at-least-once); handlers must absorb duplicates by `EventId` (spec). `IssueGrain.CompleteWorkAsync` is already state-check idempotent. Landing step 5 hardens the rest.
- **[Starvation under a chatty stream]** → Mitigation: out of scope this issue (personal-scale volume); future round-robin is documented. No data model change needed.
- **[Reminder table in tests]** → Orleans ADO.NET reminder table is bootstrapped by Orleans scripts, not EF migrations; verify the test DB template (`MigratedSqliteTemplate`) has the reminder schema, or assert delivery via `PulseAsync` only.
- **[Existing handlers still swallow internally]** → Mitigation: that is landing step 5 (Non-Goal here). The dispatcher's per-handler aggregation is additive and correct regardless of whether handlers swallow.

## Migration Plan

- **DB:** one new EF migration creating the `DeadLetters` table + indexes. No data migration of existing event rows — `DispatchedAt` already exists on all four tables (from prior issues), and producers only append. DeadLetters starts empty.
- **Code:** add the grain, service, store, row type; fix the reflection scan; inject `TimeProvider`. The 8 handlers are unchanged and reactivate automatically once the dispatcher ticks.
- **Deploy:** start the silo → the singleton activates → registers the reminder → begins ticking. Event rows left `DispatchedAt IS NULL` from the dormant window are picked up on the first tick (that is the point — they were never lost, only unnotified).
- **Rollback:** stop the silo / disable the grain registration. Producers keep appending (they only append). Undelivered rows accumulate but are never lost; redeploying resumes from where the tables are. Dropping the `DeadLetters` table is safe (it only held poison records) but unnecessary to roll back.

## Open Questions

1. **3 vs 4 truth tables (issue body vs specs).** Issue body says three; specs/proposal say four (incl. `AgentSessionEvents`). Implementation is table-agnostic and already covers all four via `ListUndeliveredAsync`. **Proposed resolution: follow the specs (all four delivered); confirm with the issue author that AgentSession delivery is desired** — it is consistent with the `AgentSubscriptionDispatchHandler` `[Subscription(Type="*")]` contract, so the default is safe.
2. **Retry policy specifics.** Attempt cap value, and whether any (time-source-driven) backoff is wanted in v1. Current decision: fixed cap, no backoff. Confirm acceptable.
3. **Dead-letter re-delivery scope.** Re-dispatch to **all matching handlers** (fresh dispatch) vs. only the handler that failed. Current interpretation: all matching (the DLQ row records *who* failed for operator visibility). Confirm.
4. **Producer `Pulse` wiring scope.** The grain exposes `PulseAsync`; whether to wire producers (via `IGrainFactory`) to call it after commit is in this issue or a follow-up. Current read: expose the entry point here; wiring all producers is best-effort and can follow up. Confirm.
5. **Reminder cadence / `LIMIT`.** Exact reminder period (~1s) and per-tick `LIMIT N` default. Current: configurable, ~1s / 100. Confirm.
