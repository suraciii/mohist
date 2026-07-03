## Context

The Issue aggregate's two terminal transitions carry one domain fact each, but emit events whose names betray those facts. `Issue.Close()` sets status to `Cancelled` yet records `IssueClosed` (persisted/emitted as `com.mohist.issue.closed`); `Issue.Complete()` sets status to `Done` yet records `IssueWorkCompleted` (`com.mohist.issue.work-completed`). Meanwhile the catalog declares `com.mohist.issue.cancelled` / `com.mohist.issue.completed` that **no producer emits**. Consumers paper over the lie — e.g. `IssueStageAttribution.cs:165` projects `closed` back to `State.Cancelled`, and three code comments admit "the durable facts diverge from the spec; the implementation follows the facts." Prior design `issue-297` even flagged this as a known divergence to work around. This change makes the carrier names faithful.

Verified current state (code):

- **The persisted `IssueEvents.Type` column stores the reverse-DNS bus type, not the CLR type name.** `IssueGrain.PublishIssueEventsAsync` builds the envelope with `type: IssueEventSerializer.BusType(evt)` (`Issue/Grains/IssueGrain.cs:626`), and `EventStore.AppendAsync` persists `Type = envelope.Type` (`Infrastructure/Data/Events/EventStore.cs:37`). So **one rename point (`BusType`) moves both the bus type and the persisted `Type` value together.** `IssueEventSerializer.Type()` (the CLR-name helper) is currently dead — no references in `src/` or `tests/` — but is kept consistent for free because it derives from `Unwrap(payload).GetType().Name`.
- **`IssueEvent` is a C# 14 `union`** (`Issue/Domain/Events/IssueEvent.cs:3`). The serializer's `BusType` and `Unwrap` switches are exhaustive over the union, so renaming a variant is a **compile-checked sweep** — the compiler flags every variant reference.
- **Catalog already half-aligned.** `EventCatalog.ReverseDns` already defines `IssueCompleted` (`:127`) and `IssueCancelled` (`:128`) constants and lists both in `All` (`:77-78`). It still also defines/lists `IssueWorkCompleted` (`:130`, `:80`). There is **no** `IssueClosed`/`com.mohist.issue.closed` catalog entry at all — `closed` was never catalogued. So the catalog edit is "drop the dead `work-completed` entry; `closed` was never there."
- **Issue is state-stored: snapshot is truth, grains do NOT replay events on startup.** Therefore an event-type rename cannot corrupt grain state. Persisted events are read only by query/projection surfaces (timeline, metrics, stage attribution, stage-population snapshot, epic reconcile, inbox).
- **Backfill precedent exists:** `20260629120000_BackfillIssueCompletedAt.cs` — a raw-SQL, no-`BuildTargetModel`, idempotent EF migration gated on a null check. This change adds a sibling migration that rewrites `IssueEvents.Type` strings in place.
- **Consumer surface (server):** `EpicAutoDoneHandler.cs` + `EpicClosedReconcileHandler` (`[Subscription]` literals, `ICloudEventHandler<T>` type params, `evtType` log strings); `InboxProjectionHandler.cs` (subscription pipe literal + two dispatch arms); `IssueStageAttribution.cs` (catalog const + one bare literal); `IssueQuerier.cs` (`WorkCompletedType`/`ClosedType` consts + ~6 usages); `StagePopulationSnapshotService.cs` (three bare literals); `EpicReconciliationService.cs` (doc only).
- **Consumer surface (web):** `shared/lib/canonical-event-types.ts`, `widgets/issue-event-timeline/model/describe.ts`, `entities/issue/@x/events.ts`, `app/providers/handle-event.ts`, `app/providers/model/reverse-dns-outcome.ts`.
- **CLI / runner:** none.

Constraints / stakeholders: per `design/architecture.md`, persisted events are durable facts interpreted server-side; the rename must not split historical from new data. Per `design/testing.md`, the migration spec runs under SQLite with raw SQL (no wall-clock, no real DB engine). AGENTS.md states the project is in active development with **no version-compatibility obligation**, so a single coordinated rename + backfill is acceptable over a staged dual-read rollout.

## Goals / Non-Goals

**Goals:**
- Rename the two terminal event types to faithful names: `IssueClosed`→`IssueCancelled`, `IssueWorkCompleted`→`IssueCompleted`; serialized ids `...closed`→`...cancelled`, `...work-completed`→`...completed`.
- Make the catalog consistent with producers: both terminal ids gain real emitters; the dead `work-completed` catalog entry is removed; the serializer routes terminal ids through `EventCatalog.ReverseDns` constants (not divergent literals).
- Sweep every consumer (subscription + hardcoded type match, server and web) to the canonical ids with **zero external behavior change** — cancelled issues still go `Cancelled`, still reconcile the epic, still drop from stage population, still bucket as failed; completed issues still go `Done`, still drive auto-done, still project to inbox, still bucket as completed.
- Backfill historical `IssueEvents.Type` rows so pre- and post-rename data share one vocabulary; timeline rendering and terminal bucketing correct across both.

**Non-Goals** (per proposal):
- No change to the `IssueStatus` enum (`Backlog/InProgress/Done/Cancelled`) — state names are already faithful.
- No event sourcing introduced; snapshot remains truth, grains still do not replay.
- No rename of non-terminal events (`created`, `work-started`, `labels-changed`, …) — already faithful.
- No fix for the broader "catalog lists 4 of 13 emitted issue ids" deficit — deferred.
- No `EventBridge` change (its `com.mohist.*` wildcard is naturally compatible).

## Decisions

### D1. Rename the union variants and route serialization through catalog constants.

- `IssueEvent` union: replace `IssueWorkCompleted` with `IssueCompleted(string WorkflowRunId)` and `IssueClosed` with `IssueCancelled(string? Reason)` (`Issue/Domain/Events/IssueEvent.cs`). Field shapes are identical, so payloads are wire-compatible.
- `Issue.Transitions.cs`: `Complete()` records `new IssueCompleted(workflowRunId)` (`:181`); `Close()` records `new IssueCancelled(reason)` (`:211`).
- `IssueEventSerializer.BusType`: map `IssueCompleted => EventCatalog.ReverseDns.IssueCompleted`, `IssueCancelled => EventCatalog.ReverseDns.IssueCancelled`, and switch the remaining inline literals to their `ReverseDns` constants so the serializer and catalog cannot drift again. `Unwrap` gets the renamed arms.

Rationale: because the persisted `IssueEvents.Type` is the `BusType` value (verified above), this single edit moves the bus type, the persisted type, and the storage-facing CLR name together. The C# 14 union makes the sweep compile-checked.

Alternatives considered: **keep the CLR names, only change the serialized id** (rejected — leaves the misnomer in the domain model and in `Unwrap`/`Type()`, so the lie persists at the source); **add brand-new event types alongside the old** (rejected — violates the "legacy types no longer exist" requirement and leaves two vocabularies).

### D2. Catalog: drop the dead `work-completed` entry; `closed` was never listed.

- Remove `ReverseDns.IssueWorkCompleted` (`:130`) and its `All` entry (`:80`).
- The `IssueCompleted` / `IssueCancelled` constants and `All` entries already exist (`:77-78`, `:127-128`) — now they gain real producers, satisfying "every catalog-declared terminal id has exactly one producer."
- Do **not** add a historical `closed` entry: it was never catalogued, and cataloguing-then-removing would be churn.

### D3. Consumer sweep keys every match on the canonical `EventCatalog.ReverseDns` constants — no surviving bare literals.

Server:
- `EpicAutoDoneHandler` / `EpicClosedReconcileHandler`: `[Subscription(Type = …)]` → the canonical constants; `ICloudEventHandler<IssueCompleted>` / `<IssueCancelled>`; `evtType` log strings → `"completed"` / `"cancelled"`.
- `InboxProjectionHandler`: subscription pipe + both dispatch arms → `EventCatalog.ReverseDns.IssueCompleted` (cancellation stays excluded — it never was an inbox signal).
- `IssueStageAttribution`: `IssueWorkCompleted` arm → `IssueCompleted`; bare `"com.mohist.issue.closed"` literal (`:165`) → `EventCatalog.ReverseDns.IssueCancelled`.
- `IssueQuerier`: replace the `WorkCompletedType`/`ClosedType` consts with `CompletedType = EventCatalog.ReverseDns.IssueCompleted` / `CancelledType = …IssueCancelled` and update the ~6 references + doc.
- `StagePopulationSnapshotService`: replace the three bare literals (`:301-302`, `:318`) with the constants.
- `EpicReconciliationService`: doc-only update.

Web:
- `canonical-event-types.ts`: `IssueClosed`→`IssueCancelled: 'com.mohist.issue.cancelled'`, `IssueWorkCompleted`→`IssueCompleted: 'com.mohist.issue.completed'`.
- `describe.ts`: two `case` arms + human labels ("Issue closed"→"Issue cancelled", "Work completed"→"Issue completed").
- `entities/issue/@x/events.ts`, `handle-event.ts`, `reverse-dns-outcome.ts`: re-key on the renamed constants.

Rationale: the spec requires the legacy ids vanish entirely. Routing every match through catalog/registry constants (instead of new bare literals) structurally prevents the catalog-vs-implementation drift that caused this issue. The exhaustive union + the `ReverseDns` constant set make a partial sweep fail loudly at compile time (server) or via the canonical-registry unit test (web).

### D4. Backfill is an in-place rewrite of `IssueEvents.Type`, modeled on `20260629120000_BackfillIssueCompletedAt`.

New raw-SQL EF migration (timestamp **later than** `20260629120000`), no `BuildTargetModel` (pure data backfill, same precedent):

```sql
UPDATE IssueEvents SET Type = 'com.mohist.issue.cancelled'
  WHERE Type = 'com.mohist.issue.closed';
UPDATE IssueEvents SET Type = 'com.mohist.issue.completed'
  WHERE Type = 'com.mohist.issue.work-completed';
```

Idempotent: a second run matches zero rows. `Down` rewrites back to the legacy ids (symmetric, unlike the additive-no-op `completedAt` backfill) so the change is reversible in the event layer. No snapshot/status/enum change — Issue is state-stored and grains do not replay, so snapshot integrity is untouched.

Rationale: in-place rewrite is the only way to satisfy "legacy ids no longer exist" + "old and new data behave identically." The `(Type, Source, Id)` index (`MohistDbContext.cs:361`) is unaffected by a value-only update.

Alternatives considered: **dual-read (consumers accept both old and new ids forever)** — rejected: it permanently carries two vocabularies and directly violates the spec's "legacy ids no longer exist" requirement; **replay/re-derive from snapshots** — rejected: events are the durable record being repaired, not derived data.

### D5. Single coordinated deploy (code + migration together); no dual-read bridge release.

`mo update server` ships the renamed code and applies EF migrations on startup in one step. The rename and the backfill land atomically per host. AGENTS.md waives version compatibility, and the only transient gap (renamed code live, backfill not yet applied within the same startup) is bounded by EF's startup migration step — no cross-version request can observe it.

Alternatives considered: **three-stage rollout (dual-read release → backfill → dual-read removal)** — rejected as over-engineering for a local-first single-operator tool where one coordinated deploy is safe and far simpler.

### D6. Interaction with the existing `BackfillIssueCompletedAt` migration is benign.

That migration's SQL references the old ids (`work-completed`/`closed`, `:43`, `:57`). EF records applied migrations and never re-runs them, so on already-migrated databases its SQL is **historical-only** — already executed, never re-evaluated against renamed rows. On a fresh database, migrations apply in timestamp order: `20260629120000` runs first on empty data (no-op), then the new rename migration runs on empty data (no-op). Its `completedAt`-backfill work is now superseded by live writes from `Close()`/`Complete()`. No edit to the historical migration is needed or allowed (migrations are immutable history).

## Risks / Trade-offs

- **[Persisted event-type rename can split historical from new data if a consumer or the backfill is missed]** → mitigated by D3 (exhaustive sweep keyed on one constant set, compile-checked by the C# 14 union + the web canonical-registry test) and D4 (idempotent in-place backfill). State-stored snapshots mean even a missed sweep cannot corrupt grain state — only a projection would mis-bucket, and that surfaces in tests.
- **[Missed consumer surfaces a silent regression (e.g. epic deadlocks when its in-progress issue is cancelled)]** → mitigated by preserving both epic subscriptions (completion AND cancellation) under the new ids; the `terminal-event-consumption` spec covers each consumer with a dedicated scenario.
- **[Renamed code live before backfill applied within a startup window]** → bounded to EF's migration-on-startup step; no cross-version observer. Accepted for a single-host local-first tool (D5).
- **[Backfill migration ordering vs `20260629120000`]** → mitigated by D6: new migration timestamp is later; both are no-ops on empty/fresh DBs and the old one never re-runs.
- **[Web bundle ships before server, or vice-versa, across a live event]** → low impact: the live event id is produced by the server. A pre-rename web receiving a post-rename event would simply not match the terminal case (timeline misses one row); a post-rename web receiving a pre-rename event likewise. Both vanish the moment the backfill lands, and the backfilled rows use the canonical id. No state corruption.
- **[Removing `IssueEventSerializer.Type()` (dead CLR-name helper)]** → not removed; kept consistent. It is dead but harmless, and the spec expects the storage-facing CLR name to track the rename, which it does for free.

## Migration Plan

1. **Server domain + infra:** rename the two union variants and records; update `Close()`/`Complete()`; rewrite `BusType`/`Unwrap` to route through `EventCatalog.ReverseDns`; drop the dead `work-completed` catalog constant + `All` entry.
2. **Server consumers (D3):** sweep the two epic handlers, inbox projection, stage attribution, querier consts, stage-population snapshot, reconciliation doc comments.
3. **Server persistence:** add the raw-SQL rename migration (D4), timestamped after `20260629120000`.
4. **Web (D3):** update the canonical registry, timeline labels, typed event map, event router, outcome decider.
5. **Tests:** sweep specs referencing the legacy types/ids — `EpicAutoDoneHandlerSpecs`, `EpicReconciliationServiceSpecs`, `InboxProjectionHandlerSpecs` (+ realtime-hint), `StagePopulationSnapshotServiceSpecs`, `IssueStageAttributionSpecs`, `IssueQuerierSpecs`, `IssueMetricsApiSpecs`, `BackfillIssueCompletedAtMigrationSpecs`; add a new spec for the rename migration (rewrite + idempotency + `Down`). Web: `canonical-event-types.test.ts`, `reverse-dns-outcome.test.ts`, `LiveTaskProvider.test.ts`, `live-task-cloud-event.test.tsx`.
6. **Deploy:** `mo update server` (code + migration on startup), then the web build. Single coordinated release per D5.
7. **Rollback:** `Down` the rename migration (reverts `IssueEvents.Type` to legacy ids) and revert the code commit. Because the backfill is reversible and no snapshot/enum changed, rollback restores the exact pre-change event-layer vocabulary. (If new post-rename rows were written between deploy and rollback, they would have canonical ids and need the same `Down` rewrite — the `Down` SQL handles only legacy-id rows, so a fully clean rollback of mixed-era data would require extending `Down`; accepted as out of scope for a no-compatibility local tool.)

Verification gates: `npm test` (server, C# warnings-as-errors), `npm run typecheck -w packages/web`, `npm run test:run -w packages/web`.

## Open Questions

- **`IssueEventSerializer.Type()` disposition:** it is dead code today. Keep it consistent with the rename (current plan), or delete it to remove the misleading "storage-facing = CLR name" signal entirely? Lean: **keep** (harmless, and the spec expects a storage-facing CLR name to exist).
- **Rollback of mixed-era rows:** the `Down` migration only rewrites legacy-id rows back; rows written post-rename (canonical ids) would survive a rollback un-rewritten. For a no-compatibility local tool this is acceptable, but should the `Down` be made symmetric in both directions (also map canonical→legacy) for a truly clean revert? Lean: **no** — over-engineering for the stated no-compatibility posture.
- **Timeline label wording:** "Work completed" → "Issue completed" reads slightly differently from the prior phrasing. Confirm the desired user-facing string (vs. e.g. "Work completed" retained). Lean: **"Issue completed"** to match the canonical event name and the cancellation counterpart "Issue cancelled".
