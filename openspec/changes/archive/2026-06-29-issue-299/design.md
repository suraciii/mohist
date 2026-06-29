# Design — Issue persisted completion time (`completedAt`)

## Context

The dashboard's completion signals — factory-status "shipped today" and the Digest "recently completed" list — are derived from an issue's `updatedAt`. `updatedAt` is bumped by *any* edit (comment, title, label), so a long-done issue gets re-counted as "shipped today" and crowds genuinely recent completions off the list. See `proposal.md` for motivation.

Current state of the affected surfaces (verified in code):

- The `Issue` entity (`packages/server/src/Mohist.Server/Issue/Domain/Issue.cs`) persists `CreatedAt` (init-only), `UpdatedAt`, and `ArchivedAt` — but **no completion time**. Lifecycle events (`IssueWorkCompleted`, `IssueClosed` in `Issue/Domain/Events/IssueEvent.cs`) carry **no timestamp payload**; the only durable timestamp is `DateTimeOffset.UtcNow` stamped on the CloudEvent envelope at publish (`IssueGrain.PublishIssueEventsAsync`) and stored in the `IssueEvents.Time` column.
- Issues are **snapshot-state** (whole entity serialized to JSON in `Issues.State` via `IssueStore`), **not** event-sourced for rehydration. `IssueGrain.OnActivateAsync` loads a single snapshot. There is no state `Version`/`SchemaVersion` field; state evolution is done via EF computed-column migrations and/or on-read JSON rewrite in `IssueStore.Deserialize`.
- Read models (`IssueInfo`, `IssueReadModel` in `Issue/Services/`) expose `createdAt`/`updatedAt`/`archivedAt` as ISO-8601 strings. The projection lives in `IssueQuerier.ToInfo`. There is **no separate archived-detail endpoint/DTO** — archived and non-archived detail share the exact same path, so a field added to the entity+projection automatically reaches both.
- Web `Issue` type (`packages/web/src/entities/issue/model/issue.ts`) has no `completedAt`. Consumers reading `updatedAt` for a "completed" semantic: `factory-status` `shippedToday` (`widgets/factory-status/model/factory-status.ts`), Digest completed sort+display (`entities/issue/lib/recent-digest.ts`, `widgets/dashboard-digest/ui/DashboardDigestWidget.tsx`), and the ArchivedPage "Completed" label (`pages/archived/ui/ArchivedPage.tsx`). Digest *failed* and *archived* buckets are out of scope (failed has no in-scope timestamp; archived already uses `archivedAt`).
- A backfill precedent exists: `IssueQuerier.GetCompletionBucketsAsync` already derives completion from `IssueEvents` filtering on `Type IN ('com.mohist.issue.work-completed','com.mohist.issue.closed')`, taking the latest time per issue — exactly the derivation we need. The raw-SQL EF-migration pattern (`20260625000000_EpicIdleRename.cs`) is the established one-time backfill shape.

Constraints: the project is pre-1.0 and actively developed — no version compatibility required. No external systems may be used in tests (fakes only). `TreatWarningsAsErrors` is the C# lint.

## Goals / Non-Goals

**Goals:**
- Persist a single completion-time source of truth on the issue entity, symmetric with `createdAt`/`archivedAt`.
- Write it on every terminal transition (`done` via `Complete`, `cancelled` via `Close`); leave it set on reopen; overwrite on re-completion.
- One-time, idempotent backfill for issues already terminal, derived from the durable `IssueEvents` log.
- Expose `completedAt` on list, detail, and archived-detail read models (single projection point).
- Switch the factory-status "shipped today" count and the Digest "recently completed" ordering/display to `completedAt`, so post-completion edits no longer move the needle.

**Non-Goals (per proposal):**
- No change to completion-trend / throughput data sources (already event-based).
- No lead-time / cycle-time derived fields.
- Digest "recently failed" ordering stays on `updatedAt` (no in-scope persisted timestamp for failure).
- No new server-side query/filter on `completedAt` (dashboard derivations are client-side over the full list) — so no new computed/indexed column is required for this change.

## Decisions

### D1 — Persist `CompletedAt` as a nullable field in the issue JSON snapshot (no new DB column)

Add `DateTime? _completedAt` backing field + `CompletedAt` property with an `init` setter (mirroring `ArchivedAt`: `init` so the JSON deserializer can populate it on load; mutated only by terminal transitions). It rides inside the existing `Issues.State` JSON blob written by `IssueStore`. No real column, no computed column — no server-side query needs it (Goals/Non-Goals). This matches how `createdAt`/`archivedAt` are stored today and keeps the schema diff to zero.

- *Alternative considered:* add a real/computed column (like `IsArchived`) so SQL could filter "completed today." Rejected: no server-side consumer needs it; the dashboard computes over the client-loaded list. Adding a column would be speculative surface area.

### D2 — Write `completedAt` on terminal transitions from the transition `now`; reopen leaves it; re-completion overwrites

In `Issue/Domain/Issue.Transitions.cs`:
- `Complete(...)`: capture `var completedAt = now ?? DateTime.UtcNow;` (same idiom as `Archive`), set `_completedAt = completedAt`, then `Touch(completedAt)`. Capturing once guarantees `completedAt` and `updatedAt` share the same instant (avoids two `UtcNow` reads drifting by ticks) and matches the established `Archive` pattern.
- `Close(...)`: same — set `_completedAt` on entering `cancelled`.
- `Reopen(...)`: **unchanged** — it does not touch `_completedAt`, so the prior terminal moment survives until the next terminal transition overwrites it (satisfies "reopen preserves; re-complete overwrites to latest").
- `Archive`/`Unarchive`: unchanged; archive is only reachable from `Done` (where `completedAt` is already set) and does not alter it.

- *Why `now` and not the event envelope `Time`:* the domain events carry no timestamp; the envelope `time` is assigned at publish (`PublishIssueEventsAsync`), which runs *after* the transition and snapshot save in the same grain turn. So at transition time the only available "moment of completion" is `now`. The live write's `now` and the subsequently-published event `Time` are the same grain turn and thus the same instant to the resolution that matters. The backfill (D3) derives from the durable event `Time` as the only surviving record; for a given transition these agree to within the grain turn. The spec's "match what the live write would have produced" is honored semantically.
- *Alternative considered:* stamp the timestamp onto the event record itself so live and backfill are byte-identical. Rejected: larger blast radius (changes event recording/publish for all lifecycle events) for negligible gain; the snapshot field is the source of truth for reads, not the event.

### D3 — One-time backfill via a raw-SQL EF migration over `IssueEvents`, idempotent on `completedAt IS NULL`, taking `MAX(Time)`

New migration under `Infrastructure/Data/Migrations/` (architecture rule: migrations live there). `Up` runs two `UPDATE ... json_set` statements joining `Issues` to `IssueEvents`:

```sql
-- done issues
UPDATE Issues
SET State = json_set(State, '$.completedAt', (
    SELECT MAX(e.Time) FROM IssueEvents e
    WHERE e.Source = '/mohist/issues/' || Issues.IssueId
      AND e.Type = 'com.mohist.issue.work-completed'
))
WHERE json_extract(State, '$.completedAt') IS NULL
  AND COALESCE(json_extract(State,'$.status'), json_extract(State,'$.Status')) = 'done';

-- cancelled issues
UPDATE Issues
SET State = json_set(State, '$.completedAt', (
    SELECT MAX(e.Time) FROM IssueEvents e
    WHERE e.Source = '/mohist/issues/' || Issues.IssueId
      AND e.Type = 'com.mohist.issue.closed'
))
WHERE json_extract(State, '$.completedAt') IS NULL
  AND COALESCE(json_extract(State,'$.status'), json_extract(State,'$.Status')) = 'cancelled';
```

Why each choice:
- `MAX(Time)` — for an issue completed→reopened→completed again, the current terminal state is the *most recent* transition; live writes overwrite, so latest matches (consistent with `IssueQuerier.GetCompletionBucketsAsync` keeping the latest work-completed time).
- `WHERE completedAt IS NULL` — idempotent: re-running is a no-op; also prevents clobbering any live-written value during a partial deploy.
- `Source = '/mohist/issues/' || IssueId` — the exact source keying used by `IssueEventPersistence` and `IssueQuerier`.
- `IssueEvents.Time` is stored as TEXT (ISO-8601) under EF Core's SQLite `DateTimeOffset` mapping; `json_set` writes it verbatim, so the value round-trips through `System.Text.Json` into `DateTime?` (STJ is lenient across ISO-8601 variants). This reuses the exact derivation `GetCompletionBucketsAsync` already performs in C#.

`Down` no-ops (field is additive; the `completedAt` key can be left in place — removing it would lose live-written values). Migration is covered by an in-memory SQLite spec mirroring `EpicIdleRenameMigrationSpecs` (idempotency + correctness for done/cancelled/recompleted cases).

- *Alternative considered:* a programmatic sweep service (load each issue via `IssueStore.Deserialize` → derive via `IEventStore.ListIssueEventsAsync` → re-`Serialize`). Rejected as primary: it needs a trigger and idempotency marker, and the SQL migration is the documented one-time-backfill pattern in this repo. The programmatic path remains a fallback if format/edge cases surface (see Risks).
- *Alternative considered:* lazy on-read upgrade in `IssueStore.Deserialize`. Rejected as a *primary*: that static JSON transform has no DB/event-store access, so it could only approximate `completedAt` from `updatedAt` — not the event time. The migration is authoritative.

### D4 — Expose `completedAt` through the single read-model projection

Add `string? CompletedAt` to both `IssueInfo` and `IssueReadModel`. In `IssueQuerier.ToInfo`, set `CompletedAt = issue.CompletedAt?.ToString("o")` (next to `ArchivedAt`). `ToReadModel` copies it through. Because archived detail uses the identical endpoint/DTO/path as regular detail, the archived-detail requirement is satisfied by the same one-line change — no archived-specific code.

### D5 — Web: strict `completedAt` for the completed bucket; failed/archived untouched

- `Issue` type: add `completedAt?: string`.
- `factory-status` `shippedToday`: `status === 'done' && issue.completedAt && isTodayLocal(issue.completedAt)` (null guard keeps the count safe if a done issue momentarily lacks the field).
- `recent-digest` completed bucket: sort by `completedAt` desc; `DashboardDigestWidget` completed `timestampFor` → `issue.completedAt` (with `?? issue.updatedAt` as a defensive display fallback only). Failed bucket sort+display unchanged (`updatedAt`); archived bucket unchanged (`archivedAt`).
- `ArchivedPage` "Completed" label: read `issue.completedAt ?? issue.updatedAt` (fallback preserves display for any un-backfilled row).

`completedAt` is the strict sort/count key per spec; `updatedAt` fallbacks are display-only safety nets that do not affect ordering or the "shipped today" count (the factory-status count is gated on `completedAt` being present).

## Risks / Trade-offs

- **[Terminal issue with no completion event in `IssueEvents`]** — e.g. an issue that went terminal before the event log existed (`20260610021455_AddIssueEvents`), or events lost. The D3 SQL leaves `completedAt` NULL for such rows (subquery returns NULL) → they drop out of "recently completed"/"shipped today" until they're re-touched live. *Mitigation:* the set is expected to be empty or tiny (event log predates broad usage); verify with a post-migration count query (`done`/`cancelled` rows where `completedAt` IS NULL) and, if non-empty, decide per-row (manual touch or accept). Listed as an open question.
- **[Event `Time` format vs STJ DateTime parse]** — `IssueEvents.Time` is TEXT (EF SQLite DateTimeOffset mapping); `json_set` writes it verbatim. If the stored form (e.g. `YYYY-MM-DD HH:MM:SS+00:00`) ever fails STJ's `DateTime?` parse, the issue row would fail to deserialize on next load. *Mitigation:* STJ accepts the ISO-8601 variants EF emits; the migration spec round-trips a real persisted `Time` value through `IssueStore.Deserialize` to prove it parses. If a mismatch is found, switch that issue's backfill to the programmatic fallback (D3 alternative) which uses `JSON.Serialize` for guaranteed format parity.
- **[Backfill `now` vs event `Time` skew]** — live writes use transition `now`; backfill uses event `Time` (publish moment). These differ by at most one grain turn and are observationally indistinguishable at second resolution. *Mitigation:* accepted; both express "when the issue reached its terminal state." No consumer depends on sub-second precision.
- **[Backfill not byte-idempotent on re-run after a live write]** — gated by `completedAt IS NULL`, so a live-written value is never overwritten; a second backfill run changes nothing. *Mitigation:* the `WHERE` clause is the idempotency contract; the migration spec asserts a second run is a no-op.
- **[No optimistic concurrency on `Issues`]** — pre-existing; the snapshot save has no ETag (unlike `WorkflowRunRow`). Adding `completedAt` does not change this. *Mitigation:* out of scope; single-writer grain semantics already protect issue state.

## Migration Plan

1. **Code first (backward-compatible):** add `CompletedAt` to entity, transitions (D2), DTOs + projection (D4), and the backfill migration (D3). Deploying the code *before* the backfill runs is safe: new terminal writes populate the field; pre-existing terminal issues read as `null` (handled by the null guards in D5). No downtime.
2. **Backfill:** the EF migration runs as part of the normal deploy (`Database.MigrateAsync` at startup) — `Up` populates `completedAt` for all already-terminal issues from `IssueEvents`. Idempotent.
3. **Web:** add the type field + derivation switches (D5). The web can deploy independently; until the server exposes `completedAt`, the field is `undefined` and the `updatedAt` display fallbacks keep the UI correct.
4. **Verify:** post-deploy query — count `done`/`cancelled` rows with `completedAt IS NULL`; expect 0 (or a documented edge set). Spot-check that editing a completed issue no longer moves it in the Digest / increments "shipped today".
5. **Rollback:** revert the code. The persisted `completedAt` values are additive and ignored by old code; the `Down` migration intentionally does not strip them (no data loss). Re-running forward re-backfills only nulls.

## Open Questions

- **Terminal-but-no-event rows:** how many (if any) issues are in a terminal state with no matching `IssueEvents` row? The post-deploy count query answers this; if non-empty, do we accept `null` or add a best-effort `updatedAt` fallback for that specific set only (keeping the *live* derivation strictly event-based)?
- **Display fallback policy:** should the Digest completed `timestampFor` and ArchivedPage "Completed" label use `completedAt ?? updatedAt` (defensive, chosen in D5) or strict `completedAt` (blank if missing)? Chose defensive to avoid blank timestamps during the deploy window; confirm this matches product intent.
