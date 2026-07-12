# Design — 落地自驱动事件分发器（issue-362）

> Motivation and product shape live in [`proposal.md`](./proposal.md); requirements in [`specs/event-dispatch/spec.md`](./specs/event-dispatch/spec.md) and [`specs/dead-letter/spec.md`](./specs/dead-letter/spec.md). This document explains **how** to implement them. It is landing step 3 of the [`design/eventbus-v2.md`](../../design/eventbus-v2.md) roadmap.

## Context

#361 made event writes durable (inside the aggregate state transaction) and removed the synchronous in-memory fan-out in `InMemoryEventBus`. Every `[Subscription]` handler is now dormant: the cross-aggregate reactions that drive the workflow forward (`WorkflowRunCompleted → CompleteIssue`, stage-lock release, epic auto-done, inbox projection, Hermes notification) no longer fire, and `AgentSubscriptionDispatchHandler` cannot receive future replay attempts. Events are truth on disk; nobody is pushing them to subscribers.

Current state of the relevant pieces (verified against the tree):

- **Pull surface already exists.** `IEventStore.ListUndeliveredAsync` (`Infrastructure/Data/Events/EventStore.cs:229`) runs a **single 4-way `UNION ALL`** over all event truth tables (`WorkflowRunEvents`, `IssueEvents`, `EpicEvents`, `AgentSessionEvents`), `WHERE DispatchedAt IS NULL`, `ORDER BY Source, Id LIMIT N`, returning `UndeliveredEvent`. Each result carries both `Source` (stream identity/order) and `Origin` (the authoritative truth-table identity). `MarkDispatchedAsync(origin, source, id, dispatchedAt)` updates the row in that reported table instead of re-inferring storage from the source URI. `DispatchedAt` already exists on all four row types (only mutable column; everything else `init`). Partial undelivered indexes already exist.
- **Fan-out surface already exists.** `AddCloudEventHandlersFromAssembly` compiles every `[Subscription]` handler into an `IEnumerable<Subscription>` of `DispatchDelegate`s (`CloudEventBusServiceCollectionExtensions.cs:52`); `CloudEventTypeMatcher.Matches(pattern, type)` matches pipe/wildcard type patterns. The dispatch delegates are compiled once at startup.
- **Publish path is already write-only.** `InMemoryEventBus.PublishAsync` (`InMemoryEventBus.cs:35`) appends a row and never invokes handlers. The dispatcher is greenfield wiring on top of two existing primitives.
- **Reminder subsystem is already enabled** via `UseAdoNetReminderService` (`MohistSiloRegistration.cs:33`), but **`RegisterOrUpdateReminder` is called nowhere in `packages/server/src`** — `RunnerGrain` is the only `IRemindable` and drives presence via a grain timer instead. This issue establishes the first live reminder.
- **DeadLetters layer is absent.** It was added then deleted in the test reorg (`ba50c2089`); only its ghost lingers in one migration Designer snapshot. It must be re-created.
- **Closed-generic handler discovery is broken** at `CloudEventBusServiceCollectionExtensions.cs:20-24`: `typeof(ICloudEventHandler<>).IsAssignableFrom(t)` is always `false` for concrete closed-generic types, so `EpicAutoDoneHandler`/`EpicCancelledReconcileHandler` (which implement only `ICloudEventHandler<TData>`) are silently excluded.
- **`TimeProvider` is registered** in the host-shared application service graph and overridden with `FakeTimeProvider` in `MohistIntegrationFixture`. The event path has one wall-clock offender left: `InMemoryEventBus.cs:75` (`DateTimeOffset.UtcNow`).

Constraints: no broker, no per-stream grain, no Orleans.Streaming (converged in `eventbus-v2.md`). Testing 铃律: no real time, no real external dependency, fast (<50ms unit, <500ms spec).

## Goals / Non-Goals

**Goals:**
- A sole, self-healing notifier that delivers every persisted event at least once to matching `[Subscription]` handlers, with per-stream FIFO, retry, and a poison-message dead-letter escape.
- Reactivate the 8 existing handlers with **no change to their contracts** (`AgentSubscriptionDispatchHandler` receives at-least-once event attempts but keeps best-effort launch failure handling; the Epic closed-generic handlers actually enter the fan-out set).
- A dispatch core that is a plain DI service, unit-testable with fake stores + injected `TimeProvider`; the grain is a thin shell.

**Non-Goals** (carry over from the issue):
- No parallel sharding (single dispatcher; future pure-additive via `hash(Source)%N`).
- No UI real-time channel extraction (EventBridge stays; landing step 4).
- No broad handler convergence beyond the reactions reactivated by this dispatcher. Those handlers must expose real Task outcomes and the Agent launch path must absorb duplicate event delivery; unrelated best-effort channels remain out of scope.
- No broker, no outbox table.

## Decisions

### D1 — Dispatcher = cluster-singleton grain (thin shell) over a pure DI fan-out service

`IDispatcherGrain : IGrainWithStringKey, IRemindable`, fixed key `"dispatcher"`. Orleans placement gives exactly one cluster-wide activation for that key → the sole notifier; the persisted reminder self-heals across silo crash. The grain rejects every other primary key during activation, before reminder registration or dispatch, so the string-key interface cannot be used to create a second notifier. `DispatcherActivationService` calls `EnsureStartedAsync` during host startup so a fresh database gets its first reminder without relying on a producer ping. The grain remains a thin shell: `ReceiveReminder` and `PulseAsync` call `fanOutService.DispatchAsync(ct)`.

The actual logic — pull → match → invoke → retry → dead-letter → mark — lives in an `EventDispatcherService` (plain singleton in the host-shared application service graph) with `IEventStore`, `IEnumerable<Subscription>`, `IDeadLetterStore`, `ILogger`, `TimeProvider` injected. This is the explicit directive of `eventbus-v2.md:178-189` and makes the core unit-testable without a silo (`MohistDbFixture` provides the real application graph minus Orleans infrastructure; pure unit tests use a fake `IEventStore` + `IDeadLetterStore` + `FakeTimeProvider`).

- **Alternative: per-stream consumer grains.** Rejected (`eventbus-v2.md:89,119-136`): puller count must be constant, not proportional to stream count; 1M workflows must not mean 1M grains.
- **Alternative: signal-driven dispatch (producers trigger).** Rejected (`eventbus-v2.md:114-117`): correctness must not depend on any external signal; a self-waking reminder is the only driver that survives lost pings.
- **Alternative: put the loop directly in the grain.** Rejected: untestable without the silo, violates the 铃律's fake-seam posture and the doc's "薄壳" mandate.

### D2 — Self-wake on a persisted reminder (~1s); `PulseAsync` is best-effort only

On activation the grain registers a reminder via `RegisterOrUpdateReminder("dispatcher-tick", dueTime: ~1s, period: ~1s)` — the first such call in the codebase. `ReceiveReminder` runs one pull–fan-out–mark cycle. `IDispatcherGrain.PulseAsync(ct)` runs the same cycle once, immediately, for producers that want a latency optimization (~24h → ~1s). Correctness never depends on `PulseAsync`; if every ping is lost, the next reminder tick still delivers everything.

Registration alone does not activate an Orleans grain. The host therefore starts `DispatcherActivationService` after the silo is available; its only responsibility is to call the fixed-key grain's `EnsureStartedAsync`. Startup fails visibly if the sole notifier cannot register its reminder instead of leaving the process healthy-but-inert.

- **Alternative: grain timer** (`RegisterGrainTimer`, as `RunnerGrain:139` does). Rejected for the sole notifier: timers are **not persisted** and do **not** reactivate on another silo after crash — the dispatcher must self-heal. Reminder it is.
- **Alternative: external scheduler / cron.** Rejected: reintroduces a single point of failure and an external dependency.

### D3 — One pull via the existing `ListUndeliveredAsync`; dispatcher is table-agnostic

Each tick calls `IEventStore.ListUndeliveredAsync(limit)` — already a single 4-way `UNION ALL` over all truth tables, already `ORDER BY Source, Id`. The dispatcher knows nothing about individual tables; adding a future truth table is a store change, not a dispatcher change.

> **Clarification on the 3-vs-4 tables wording.** The issue body says "三张事件真相表" (`WorkflowRunEvents` + `IssueEvents` + `EpicEvents`); the specs and proposal say four (adding `AgentSessionEvents`). The implementation is unambiguous: `ListUndeliveredAsync` already covers **all four** tables, and the dispatcher is table-agnostic. AgentSession events therefore get delivered too — consistent with the `AgentSubscriptionDispatchHandler` `[Subscription(Type="*")]` contract. No code diverges from the specs. (See Open Questions.)

### D4 — Per-stream FIFO via serial `(Source, Id)` processing; atomic poison settlement

Process the returned batch serially in the `ListUndeliveredAsync` order (`Source, Id`). For a fully delivered event, call `MarkDispatchedAsync(origin, source, id, _time.GetUtcNow())`; `Origin` selects the persisted table while `Source` remains the stream key. This preserves the store's fallback contract for custom/future CloudEvent sources and prevents settlement from failing after a successful handler invocation merely because a source URI has no known aggregate prefix. For an event with exhausted handlers, call one dead-letter settlement operation that upserts every handler-keyed dead-letter row and sets the source row's `DispatchedAt` in the same database transaction using the same reported origin. Any persistence failure aborts the tick and propagates to the reminder/Pulse caller. Continuing would allow a higher `Id` from the same source to overtake an unsettled lower row. Because the batch is globally sorted and failures stop progress, no higher `Id` in a stream is delivered before a lower one settles → **per-stream FIFO, no reorder, no skip**.

- **Alternative: per-stream cursor / `DeliveryOffsets` table.** Rejected (`eventbus-v2.md:75-77`): a cursor advanced before handlers finish degrades to at-most-once on crash. Per-row `DispatchedAt` is both simplest and correct.
- **Fairness note** (`eventbus-v2.md:142`): `ORDER BY Source, Id` drains one stream before the next; a chatty stream could starve others. Personal-scale event volume makes this a non-issue now; a future "oldest-per-stream round-robin" is pure-additive and out of scope.

### D5 — Per-type fan-out including closed-generic handlers; per-handler retry isolation

For each undelivered event, iterate the registered `IEnumerable<Subscription>`; for each whose `CloudEventTypeMatcher.Matches(sub.Type, evt.Type)` is true, invoke its `DispatchDelegate`. Each matching handler is retried **independently** — one handler's transient failure or exhaustion never affects a sibling's delivery of the same event (spec: "per-handler isolation"). The event row is marked dispatched once all matching handlers have settled (succeeded or dead-lettered).

The same `CloudEventTypeMatcher` owns pattern validation. Reflection discovery validates every `[Subscription(Type=...)]` value before registering the handler, so malformed wildcard patterns fail host construction instead of producing a handler set that can silently mark events with no match.

The returned `Task` is the delivery outcome contract for durable domain reactions. A handler may return normally for an intentional no-op, but it must propagate an exception when its required side effect fails; it must not detach work that determines success. Explicitly best-effort channels keep their published contracts: Hermes logs and absorbs webhook failures, and `AgentSubscriptionDispatchHandler` logs and absorbs launch-path failures. The dispatcher therefore sees either case as a settled best-effort attempt and does not retry or dead-letter it.

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

### D7 — DeadLetters use handler-keyed atomic settlement and explicit recovery state

Re-introduce the layer deleted in `ba50c2089`, modeled on its historical shape:
- `DeadLetterRow` (`Infrastructure/Data/Events/DeadLetterRow.cs`): `DeadLetterId` (PK, `long`), a snapshot of the event (source/id/eventId/type/data/extensions), `FailingHandler`, `Error`/`Reason`, `AttemptCount`, `DeadLetteredAt` (`DateTimeOffset`), and recovery state (`Pending`, `Redelivering`, `Resolved`) with attempt/resolution timestamps.
- `DbSet<DeadLetterRow> DeadLetters` in `MohistDbContext`; key `DeadLetterId`; indexes on `DeadLetteredAt` and `(FailingHandler, DeadLetteredAt)` (matches the queryable-by-handler requirement).
- `IDeadLetterStore` with direct write/query/get primitives plus `SettleAsync(event, rows, dispatchedAt)`, `StartRedeliveryAsync(id, attemptedAt)`, and `ResolveAsync(id, resolvedAt)`, plus a `NoopDeadLetterStore` test fake.
- EF migration creating the `DeadLetters` table.

Flow on exhaustion: collect one `DeadLetterRow` per exhausted handler, then atomically upsert them by `(Source, Id, FailingHandler)` and set `DispatchedAt` on the **original event row** in the same `SaveChanges` transaction. The natural key prevents duplicate rows if an earlier commit outcome is retried. A sibling handler that succeeded does **not** get dead-lettered; only exhausted outcomes are recorded.

Manual re-delivery targets the recorded `FailingHandler` only. Before invocation the row is durably moved to `Redelivering`; success moves it to `Resolved`, while failure returns it to `Pending` with the latest failure. If persistence fails after handler success, the row remains `Redelivering`, accurately exposing an ambiguous at-least-once outcome instead of falsely reporting success. Repeating recovery uses the same CloudEvent id, so durable handlers must absorb duplicates. `GET /api/events/dead-letters` lists unresolved rows and `POST /api/events/dead-letters/{id}/redeliver`, surfaced by `mo event dead-letter list|redeliver`, form the operator boundary.

### D8 — Fix closed-generic handler discovery in the reflection scan

At `CloudEventBusServiceCollectionExtensions.cs:20-24`, replace the dead `typeof(ICloudEventHandler<>).IsAssignableFrom(t)` check (always false for closed generics) with a proper closed-generic scan: `t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICloudEventHandler<>))`. The rest of the registration path (`:40-49` closed-generic registration, `:52-63` subscription building, `MakeTypedDelegate<TData>` deserialization) already exists and is correct — only discovery was broken.

- **Alternative: require closed-generic handlers to also implement the non-generic `ICloudEventHandler`.** Rejected: ergonomic regression, loses the typed `CloudEvent<TData>` deserialization the typed path provides.

### D9 — Inject `TimeProvider`; eliminate the last wall-clock in the event path

`EventDispatcherService` and `DispatcherGrain` take `TimeProvider` (constructor → `readonly TimeProvider _time` → `_time.GetUtcNow()` for `DispatchedAt` and `DeadLetteredAt`), mirroring the pervasive existing idiom (`WorkflowGrain.cs:32,45`, etc.). Replace `DateTimeOffset.UtcNow` at `InMemoryEventBus.cs:75` with the same injected `TimeProvider`. `TimeProvider.System` is already registered; tests inject `FakeTimeProvider`.

### D10 — One host-shared application service graph

Under Generic Host, `ISiloBuilder.Services` is the same collection that `AddMohistServerCore` configures. Register `EventDispatcherService`, `IDeadLetterStore` → `DeadLetterStore` (+ test replacement through `TryAdd`), handler discovery, options, and observer defaults once through `ConfigureMohistServices`; `ConfigureMohistSilo` owns only Orleans clustering, persistence, reminder, telemetry, and logging infrastructure. `AddCloudEventHandlersFromAssembly` feeds one `IEnumerable<Subscription>` to the dispatcher.

### D11 — Agent event launches use durable stable job identities

`AgentSubscriptionDispatchHandler` passes the trigger event/subscription labels it already owns. `AgentLauncher` derives stable AgentSession and AgentJob grain keys from `(projectId, triggerEventId, triggerSubscriptionId)` for subscription-driven launches; manual launches keep random identities. It always submits the stable job on replay instead of treating session labels as the launch claim.

`AgentLauncher` writes trigger event/subscription labels in the initial session open, before durable job submission. `AgentJobGrain` persists the input, lifecycle, candidate runner, a stable work id derived from the grain key, and an explicit Runner-accepted checkpoint before acknowledging `SubmitAsync` or calling the Runner. Agent config is persisted as raw JSON text and reconstructed on demand, avoiding nullable `JsonElement` serializer drift across activation. A prepared candidate is reconciled by replaying the same `(ownerId, workId)` to that Runner: existing durable work is accepted idempotently even after the Runner activation is offline, while an offline Runner with no matching work rejects it. Only that proven-unaccepted candidate is released so another eligible Runner can receive the same stable work id. A crash after Runner acceptance but before the checkpoint replays the same identity and then persists acceptance, so recovery neither duplicates work nor pins the job to a Runner that never admitted it.

`RunnerGrain.AssignAgentJobAsync` is the authoritative admission boundary. Runner lifecycle transitions and agent-job admission share one serialized gate; the method rejects offline runners and checks configured capacity before persisting a new work item. Caller-side status/capacity reads remain latency optimizations, not correctness checks. This makes unregister-versus-assign and two-jobs-for-one-slot races linearizable without removing the grain's existing reentrancy.

Agent jobs participate in the same poll reconciliation contract as workflow work. The runner reports stable `agent-job:{agentJobId}:{workId}` keys in `inFlight` or `awaitingAck`; missing running work is reoffered with the persisted dispatch snapshot, so a lost poll response does not turn Runner acceptance into at-most-once delivery. One Runner poll owns a transient poll gate. Overlapping polls return no work, and new Agent admission is retried while reconciliation is active, so agent assignment cannot race a workflow claim based on stale capacity. `DispatchService` subtracts every active Agent work before claiming workflows.

The Runner's last registration profile is persisted beside its durable Agent work. Activation starts offline but restores that profile; the first poll can therefore prove presence, rebuild the registry entry, and reconcile outstanding Agent work without waiting for a separate heartbeat or registration. Explicit unregister waits for any admitted poll to leave the shared poll-admission boundary before clearing the profile and marking the Runner offline. New polls after that linearization point observe no registration and cannot claim workflow work, so unregister cannot race a previously admitted capacity snapshot or let a stray poll resurrect an intentionally removed runner.

Poll admission returns the current slots snapshot while holding the same gate through the complete reconciliation round. `DispatchService` no longer accepts caller-supplied capacity, and the HTTP route no longer reads slots before admission. Capacity updates wait for the poll gate, making every poll linearize wholly before or after a slots change; the snapshot cannot become stale between admission and workflow claims.

### D12 — Dead-letter operator routes require a credential on a loopback listener and redact diagnostics

A public listener does not map the dead-letter routes. A loopback listener requires a high-entropy operator credential on every dead-letter request; the server creates a user-only, non-symlink local credential file by default, while an explicitly configured path may resolve through a managed secret-volume symlink and is read without chmod or replacement. Server and CLI resolve one contract in this order: `MOHIST_OPERATOR_TOKEN`, `Mohist:OperatorToken`, `MOHIST_OPERATOR_TOKEN_PATH`, `Mohist:OperatorTokenPath`, then `~/.mohist/operator-token`. Before reading any credential, the `mo` CLI requires the configured server base address to be loopback; only then does it send the resolved value in a dedicated header. Remote/local addresses and forwarding headers are not authentication signals, because a loopback reverse proxy can reproduce them. List responses are `no-store`; API responses return only bounded diagnostic summaries with embedded stack frames, POSIX paths, drive-letter paths, UNC paths, ANSI sequences, and controls removed, while raw exception details remain in protected storage and server logs. CLI table output shows recovery status and strips carriage returns, ANSI escapes, and other terminal controls from untrusted cells.

### D13 — Singleton handlers open a scope per delivery

Subscription instances remain singleton because the dispatcher owns a compiled, stable handler set. Any handler that needs a scoped query service receives `IServiceScopeFactory` and resolves that service inside `HandleAsync`. In particular, the Epic terminal handlers resolve `EpicQuerier` per delivery rather than retaining a root scoped instance.

### D14 — Inbox projection and durable hint commit atomically

`InboxProjectionHandler` writes the inbox row and its `com.mohist.inbox.item-persisted` event through one caller-owned `MohistDbContext` transaction. `InboxStore.InsertAsync(MohistDbContext, ...)` stages the projection on that context and `IEventStore.AppendAsync(MohistDbContext, ...)` stages the hint before the shared commit. If the event append fails, the inbox row rolls back; dispatcher retry therefore performs both writes again instead of treating the projection as a completed duplicate and losing the hint.

The transaction contract is pinned against the production `EventStore`, not only an immediate-publisher fake. An SQLite trigger aborts the persisted hint insert after the inbox row has already been saved inside the open transaction; verification observes zero rows on both sides, then removes the fault and proves replay commits exactly one inbox row and one durable hint.

## Risks / Trade-offs

- **[First live reminder in the codebase]** → Mitigation: reminder service is already configured (`UseAdoNetReminderService`); a two-silo fixture registers `DispatcherActivationService` as the actual hosted service and advances fake time until its persisted reminder delivers an appended event without manual activation, callback invocation, or `PulseAsync`. The same fixture verifies reactivation after killing the hosting silo. Keep reminder cadence configurable.
- **[Polly absence vs. proposal claim]** → Mitigation: hand-rolled retry (D6); record the decision here so reviewers don't expect Polly. If timed backoff is later required, it lands as a single seam swap.
- **[Single dispatcher = throughput ceiling]** → Mitigation: `LIMIT N` per tick caps work; no sharding is a Non-Goal. The `hash(Source)%N` extension is modeled and pure-additive (`eventbus-v2.md:155-161`).
- **[Deliver-before-mark crash → re-delivery]** → Mitigation: domain reactions are state-check/idempotent; inbox projection plus its durable hint commit atomically (D14); Agent subscription launches use stable session/job identities and poll reconciliation (D11). The test suite exercises real production idempotency rather than a recorder-only fake.
- **[Starvation under a chatty stream]** → Mitigation: out of scope this issue (personal-scale volume); future round-robin is documented. No data model change needed.
- **[Reminder table in tests]** → Orleans ADO.NET reminder table is bootstrapped by Orleans scripts, not EF migrations; the full-host spec verifies the persisted row and the two-silo spec verifies callback-driven delivery and reactivation without `PulseAsync`.
- **[Handler failure visibility]** → Mitigation: required side effects are awaited and exceptions propagate to the dispatcher. Best-effort behavior is expressed as an intentional successful no-op, not an invisible failed Task.

## Migration Plan

- **DB:** one migration creates `DeadLetters`; a hardening migration adds recovery state and the unique handler-keyed settlement index. No source-event data migration is needed because `DispatchedAt` already exists on all four truth tables.
- **Code:** add the grain, activation service, dispatch service, store, row type, operator API/CLI; fix the reflection scan; inject `TimeProvider`; keep best-effort handler contracts explicit; persist AgentJob launch state and stable work identities.
- **Deploy:** start the host → `DispatcherActivationService` activates the fixed-key grain → the grain registers its reminder → ticking begins. Event rows left `DispatchedAt IS NULL` from the dormant window are picked up on the first tick.
- **Rollback:** stop the silo / disable the grain registration. Producers keep appending (they only append). Undelivered rows accumulate but are never lost; redeploying resumes from where the tables are. Dropping the `DeadLetters` table is safe (it only held poison records) but unnecessary to roll back.

## Open Questions

1. **3 vs 4 truth tables (issue body vs specs).** Issue body says three; specs/proposal say four (incl. `AgentSessionEvents`). Implementation is table-agnostic and already covers all four via `ListUndeliveredAsync`. **Proposed resolution: follow the specs (all four delivered); confirm with the issue author that AgentSession delivery is desired** — it is consistent with the `AgentSubscriptionDispatchHandler` `[Subscription(Type="*")]` contract, so the default is safe.
2. **Retry policy specifics.** Attempt cap value, and whether any (time-source-driven) backoff is wanted in v1. Current decision: fixed cap, no backoff. Confirm acceptable.
3. **Dead-letter re-delivery scope.** Resolved: retry only the recorded failing handler and mark its row resolved after success; already-successful sibling handlers are not repeated.
4. **Producer `Pulse` wiring scope.** The grain exposes `PulseAsync`; whether to wire producers (via `IGrainFactory`) to call it after commit is in this issue or a follow-up. Current read: expose the entry point here; wiring all producers is best-effort and can follow up. Confirm.
5. **Reminder cadence / `LIMIT`.** Exact reminder period (~1s) and per-tick `LIMIT N` default. Current: configurable, ~1s / 100. Confirm.
