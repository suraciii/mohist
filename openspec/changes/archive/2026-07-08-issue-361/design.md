## Context

This change is step 2 of the event-bus v2 roadmap (`design/eventbus-v2.md:226-233`). Step 1 (#360, done) added the storage foundation — `DispatchedAt` columns on the event tables, the `DeadLetters` table, and the `IEventStore` delivery-progress/undelivered read ports. Step 2 converges the **producers** onto that storage.

Today each of the three producers persists aggregate state and emits events in **two separate operations**, on **two separate `DbContext`s**:

| Producer | State save | Event write | Swallow form |
|---|---|---|---|
| `WorkflowRunStore` (`Infrastructure/Data/Workflow/WorkflowRunStore.cs:43-79`) | own `DbContext` + transaction, then `CommitAsync` | post-commit loop: `EventStore.AppendAsync` (own `DbContext`) + `IEventPublisher.PublishAsync` | bare `catch {}` (`:74`) |
| `IssueGrain` (`Issue/Grains/IssueGrain.cs:638-687`) | `IssueStore.SaveAsync` (own `DbContext`, no explicit tx) | post-commit `PublishIssueEventsAsync`: `EventStore.AppendAsync` + `_eventBus.PublishAsync` | log-and-swallow (`:683`) |
| `AgentSessionGrain` (`Sessions/Grains/AgentSessionGrain.cs:669-708`) | `AgentSessionStore.SaveAsync` (own `DbContext` + tx) | post-commit `_eventPublisher.PublishAsync` (typed) — **zero persistence** | swallow-`InvalidOperationException` + log-and-swallow (`:681-689`) |

The gap between state commit and event write is a correctness hole: a crash there leaves the state transition durable while every downstream subscriber silently never learns it happened. `EventStore.AppendAsync` opens its **own** `DbContext` (`Infrastructure/Data/Events/EventStore.cs:27`), which structurally prevents it from enlisting in the producer's transaction.

`IEventPublisher` (impl `InMemoryEventBus`, `Infrastructure/Events/InMemoryEventBus.cs:71-93`) currently does **two** things in one call: append nothing, and synchronously fan out to every matching `ICloudEventHandler` on the producer's own call stack. That coupling is what forces workarounds like detached grain calls and reverse DB lookups (`IssueWorkflowCompletionHandler` querying `IssueRow.WorkflowRunId` to recover the owning issue, `Events/Subscriptions/IssueWorkflowCompletionHandler.cs:88-101`) — the producer already knew the identity but never stamped it on the event.

Constraints:
- **SQLite** via `UseSqlite` (`Infrastructure/Hosting/MohistServiceRegistration.cs:71`). Cross-`DbContext` transaction sharing is unreliable; the only robust shared-transaction primitive is a **single shared `MohistDbContext` instance**. (Postgres is a future-cluster concern, not this issue.)
- **EF Core migrations** are in use (`Program.cs:66` `db.Database.Migrate()`); schema changes ship as migrations.
- All event tables (`WorkflowRunEvents`, `IssueEvents`, `EpicEvents`) live on the **same** `MohistDbContext` as the aggregate state rows — so a shared `DbContext` already sees all of them.
- There is **no** `AgentSessionEvents` table today; `EventOrigin` is `{ WorkflowRun, Issue, Epic }` only, and `EventStore.AppendAsync` routes by source prefix with `WorkflowRunEvents` as the implicit default.
- Orleans grains are single-writer per aggregate key (one activation, serialized calls), so per-source `Id` sequence assignment is already serial per source.

## Goals / Non-Goals

**Goals:**
- WorkflowRun, Issue, and AgentSession event rows commit **atomically** with their aggregate state in one EF Core transaction; commit makes both durable, crash-between-commit-and-publish loses nothing.
- Event-write failures **propagate** and roll back the state transaction — one exception form replaces the three divergent swallow patterns.
- `IEventPublisher.PublishAsync` converges to "append one event row"; synchronous handler fan-out is removed from the publish path.
- `projectid` + `issueid` stamped into event `extensions` at append time (WorkflowRunStore gains `issueid`), eliminating reverse DB lookups.
- AgentSession lifecycle events become durable (persisted rows), not zero-persistence bus-only notifications.
- Crash-after-commit durability covered by tests.

**Non-Goals:**
- The dispatcher (step 3, future issue) — no polling/reminder/fan-out service is built here.
- Handler reaction logic changes; handlers stay registered but lose their synchronous trigger path until the dispatcher lands.
- DTO / HTTP contract changes; no web, CLI, or runner changes.
- `EpicGrain` event-write path ownership (decided by #360; epic event table shape unchanged here).

## Decisions

### D1 — Share the transaction via a `DbContext`-scoped append on `IEventStore`

Add a transaction-scoped write primitive to `IEventStore`:

```csharp
Task AppendAsync(MohistDbContext db, CloudEvent envelope, CancellationToken ct = default);
```

It computes the per-source `Id`, constructs the correct row type by source prefix, and `db.<Set>.Add(...)` **on the caller's `DbContext`** — no `SaveChangesAsync`, no own transaction. The commit is the caller's responsibility. The existing `AppendAsync(CloudEvent)` (no `DbContext`) becomes a thin wrapper: open own `DbContext`, begin tx, call the scoped overload, commit — preserving the non-transactional entry point for callers outside a producer transaction.

**Why not the alternatives:**
- *Stores write rows directly into their own `DbContext`* — rejected. It duplicates the source-prefix routing + per-source `Id` + row-construction logic across three stores; `EventStore` is the deep module that owns "how an event row is persisted" and should stay the single source of that knowledge.
- *`TransactionScope` / ambient transaction enlistment* — rejected. Unreliable on SQLite (Microsoft.Data.Sqlite has no real distributed-transaction support); would also hide the transaction boundary, hurting readability.
- *Thread the `DbContext` through `IEventPublisher.PublishAsync`* — rejected. Couples the "publish" abstraction to persistence internals and breaks information hiding (see D3).

This keeps `EventStore` the single owner of event-row mapping while letting producers enlist. The per-source `Id` computation (`MAX(Id)+1`) moves onto the **shared** `DbContext`/transaction, so it sees both committed rows and this transaction's own pending rows — correct under the single-writer-grain invariant.

### D2 — Producers pass domain events into the store; the store owns the transactional row write

The three producers stop doing a post-commit publish loop. Instead they hand their **domain events** to their store, which writes both state and event rows in one transaction:

- `WorkflowRunStore.SaveAsync(run, events)` — event append moves **inside** the existing `BeginTransactionAsync` block (`:50-61`); the post-commit loop (`:63-78`) is deleted. It calls the D1 scoped overload on its own `db`. Drops `_eventPublisher`.
- `IssueStore` gains `SaveAsync(string key, DomainIssue state, IReadOnlyList<IssueEvent> events, ct)`. `IssueGrain.SaveIssueAsync` (`:638-650`) reduces to: snapshot pending → `ClearPendingEvents` → `_issueStore.SaveAsync(id, issue, pending)`. The grain drops `_eventStore`/`_eventBus` and the `PublishIssueEventsAsync` method (`:652-687`).
- `AgentSessionStore.SaveAsync(key, state, events)` (`:44-59`) — already receives `events` and already runs a transaction; it now also writes event rows via the D1 scoped overload inside that transaction.

Grains emit **domain events only**; translation to `CloudEvent` envelopes moves into the stores (see D5).

### D3 — `IEventPublisher` converges to "append one row, no dispatch"

`InMemoryEventBus.PublishAsync` / `DispatchAsync` synchronous fan-out (`InMemoryEventBus.cs:71-93`) is removed from the publish path. `IEventPublisher.PublishAsync` becomes write-only: it delegates to `IEventStore.AppendAsync(envelope)` (the non-transactional overload) and returns once the row is staged. Registered `ICloudEventHandler`s stay registered (for the future dispatcher) but are **not** invoked by publish.

After D2, the three producers no longer call `IEventPublisher` for the durable path. The remaining production caller is `InboxProjectionHandler` (`Events/Subscriptions/InboxProjectionHandler.cs:183`), which publishes the `com.mohist.inbox.item-persisted` hint. Under the converged semantics that call appends a row instead of fanning out — see Open Questions.

**Why keep `IEventPublisher` at all** (rather than delete it): it is the canonical write-only port for callers that are **not** inside a producer's state transaction (inbox hints today; possibly future projection/event-bridge signals). Deleting it to force everyone through `IEventStore` directly would expand scope onto the inbox/projection channels, which this issue explicitly excludes. The name no longer fits perfectly (it appends, not "publishes to handlers") — a rename is cosmetic and deferred (Open Questions).

### D4 — AgentSession lifecycle events get their own `AgentSessionEvents` table + migration

`/mohist/agent-session/{id}` has no storage today: no table, no `EventOrigin` member, and `EventStore.AppendAsync` would misroute it to `WorkflowRunEvents` as the implicit default. To persist lifecycle events durably per the spec, add:

- a `AgentSessionEventRow` (mirror of `WorkflowRunEventRow` incl. `DispatchedAt`),
- a `AgentSessionEvents` `DbSet` on `MohistDbContext`,
- an `EventOrigin.AgentSession` enum member,
- a `/mohist/agent-session/` prefix branch in `EventStore`'s routing + `ListUndeliveredAsync` UNION (the query from #360 gains a fourth `UNION ALL` arm),
- an EF migration creating the table.

The envelope source/subject stay `/mohist/agent-session/{id}` / `session.Id` (unchanged from today's bus-publish path).

**Tension acknowledged:** `design/eventbus-v2.md:43-47` classifies Session events as ❌ (best-effort trace, not the durable domain bus). This issue's spec deliberately makes them **persisted** (durable rows) — but durability ≠ delivery SLA. Persisting the row is the crash-safety prerequisite; **whether the future dispatcher fans these rows out to domain handlers or keeps them audit-only** is a step-3 SLA-tiering decision (Open Questions). Nothing here forces them into the domain-reaction fan-out.

### D5 — Envelope construction + identity stamping consolidate into the stores

To match the existing `WorkflowRunStore.ToCloudEvent` pattern and to kill the reverse-lookup class of bug at its source, envelope construction and identity stamping move to the store (the persistence boundary):

- `WorkflowRunStore.ToCloudEvent` (`:81-97`) stamps **both** `projectid` and `issueid` (today only `projectid`; reads `issueId` from the same `Annotations` it already reads `projectId` from — the annotation is already populated by `IssueGrain` at `:230`).
- `IssueStore` stamps `projectid` + `issueid` + `issueno` (moved from `IssueGrain.PublishIssueEventsAsync:657-662`).
- `AgentSessionStore` stamps session identity (`subject` = session id; `projectid` only if available on the session — see Open Questions).

**Why not let grains keep building envelopes and pass them to the store:** that splits identity-stamping across grain and store and re-opens exactly the "producer knew the identity but didn't stamp it" gap that caused `IssueWorkflowCompletionHandler`'s reverse lookup. One place stamps; consumers read `extensions` directly.

`IssueWorkflowCompletionHandler.HandleAsync` can then read `issueid` from `evt.Extensions` instead of the scoped `IssueQuerier` reverse lookup (`IssueWorkflowCompletionHandler.cs:88-101`). Per Non-Goals the handler's **reaction** logic is unchanged; only its identity source changes.

## Risks / Trade-offs

- **[Cross-aggregate reactions stop firing until the dispatcher lands]** -> Between this issue and step 3, no `ICloudEventHandler` is synchronously triggered by publish (e.g. `WorkflowRunCompleted → CompleteIssue`, issue→epic auto-done). The events are durable, so the dispatcher will pick them up, but there is a functional gap. *Mitigation:* land this change immediately before / together with the dispatcher (step 3), or explicitly accept suspended auto-progression during the window. This is the dominant risk and drives the deployment ordering in the Migration Plan.
- **[AgentSession durability deviates from eventbus-v2's "Session = best-effort"]** -> Resolved by separating *persistence* (durable row, this issue) from *delivery SLA* (dispatcher tiering, step 3). Persisting does not obligate the dispatcher to fan out; see Open Questions.
- **[AgentSession lifecycle events are high-frequency telemetry]** -> `UsageRecorded` / `ContextHealthUpdated` fire often; one durable row each grows the event tables and the dispatcher's undelivered set. *Mitigation:* consider filtering which lifecycle types are persisted (e.g. persist only transition-significant events) — see Open Questions.
- **[`MAX(Id)+1` sequence on a shared `DbContext`]** -> Safe under the single-writer-grain invariant (one activation per source, serialized calls); the shared transaction sees its own pending rows. The broader "switch to DB autoincrement" cleanup is noted in `eventbus-v2.md:215` and is **not** required here.
- **[Test fakes must model the scoped overload]** -> `NoopEventStore`, `RecordingEventStore`, `RecordingIEventPublisher`, the nested fakes in `InboxProjectionTestSupport.cs`, and `WorkflowRunStoreSpecs`'s `CapturingEventPublisher`/`FakeWorkflowRunStoreDbContextFactory` all need the new `IEventStore`/`IEventPublisher` shapes. Specs that asserted on captured publishes must re-assert on persisted rows (`ListAsync` / `IEventStore` reads) instead.
- **[`InboxProjectionHandler` hint now appends a durable row]** -> The `com.mohist.inbox.item-persisted` signal, meant to be a non-domain best-effort hint (`eventbus-v2.md:48`), now lands as a durable event row under the converged publisher. *Mitigation:* acceptable for now (it's durable + will be redelivered by the dispatcher); pulling it off `IEventPublisher` onto a dedicated best-effort channel is step 4 — flagged in Open Questions.

## Migration Plan

This is a single-process, local-first deployment under active development (no version-compat constraints), so the plan is code + schema shipped together, no compat shims.

1. **Schema:** add the EF migration creating `AgentSessionEvents` (with `DispatchedAt`, mirroring the other event tables). #360's `DispatchedAt` columns already exist; this migration only adds the new table + its partial index.
2. **Code:** implement D1–D5 together — the `IEventStore` scoped overload, the three store changes, `InMemoryEventBus` dispatch removal, identity stamping, `IssueWorkflowCompletionHandler` identity-source switch, and the test-fake updates. Update `EventOrigin` + the undelivered UNION.
3. **Deploy ordering:** because reactions stop firing synchronously (top risk), this should land **immediately before** the dispatcher (step 3) in the same release window, not sit alone on `master`. If they must land separately, document the suspended-auto-progression window explicitly.
4. **Rollback:** revert the commit + apply the migration **down** (`dotnet ef database update <prev>`). No data requiring preservation (events that were never reliably emitted before cannot be "lost" by rolling back).
5. **Verification:** `npm test` (server, treats warnings as errors), plus new crash-after-commit specs per the `transactional-event-append` scenarios (save + simulate post-commit crash + re-read events on a fresh `DbContext` → rows present; event-write failure → state row absent).

## Open Questions

1. **Which AgentSession lifecycle types should be persisted?** Persist all six (`RuntimeBound`, `UsageRecorded`, `ModelChanged`, `ContextCompacted`, `ContextExhausted`, `ContextHealthUpdated`), or filter to transition-significant ones to control row volume? Affects table growth and the dispatcher's working set.
2. **Should the future dispatcher fan out AgentSession rows?** I.e. are persisted agent-session rows audit/replay-only, or do they enter the domain-reaction fan-out? Decide in step 3; this issue only guarantees durability.
3. **Does AgentSession need `projectid` on its event extensions?** `AgentSubscriptionDispatchHandler` resolves `projectid` from the **domain** events it consumes (`workflow`/`issue`), not from agent-session lifecycle events. If no consumer ever needs `projectid` on an agent-session row, leave it unstamped; otherwise the session aggregate / store must surface the owning project.
4. **Inbox hint channel.** Should `com.mohist.inbox.item-persisted` move off `IEventPublisher` to a dedicated best-effort channel now (pull step 4 forward for this one signal), or stay durable until step 4?
5. **`IEventPublisher` naming.** It now appends rather than publishes-to-handlers. Rename (`IEventAppender`?) is cosmetic and deferred; flagged only to avoid future confusion.
