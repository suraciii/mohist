## Context

Epic has a working lifecycle (`Idle → Running ⇄ Paused → Done/Closed`) and a
`RecordEvent` audit pipeline, but four gaps block real planning use:

1. **Terminal is irreversible.** `Epic.Transitions.cs:154` (`EnsureNotTerminal`)
   guards every transition out of `Done`/`Closed`; there is no symmetric
   `Reopen`. A wrongly-closed Epic cannot recover.
2. **Membership is single-issue only.** `EpicGrain.LinkIssueAsync` /
   `UnlinkIssueAsync` and the `EpicIssueRequest` body take one issue; planning
   requires N round-trips.
3. **The list is not queryable.** `EpicQuerier.ListAsync(projectId)`
   (`EpicQuerier.cs:27`) hardcodes `WHERE ProjectId` and
   `ORDER BY Priority, UpdatedAt DESC`; the web list has no search/sort UI.
4. **Events are drained.** `EpicGrain.ApplyPendingEvents` (`EpicGrain.cs:601`)
   calls `ClearPendingEvents` — every recorded `EpicEvent` is discarded. There
   is no events table, no read endpoint, no timeline.

All four live in the Epic bounded context (server domain + grain + query + API +
web). Existing assets to mirror: the **issue-events store**
(`IssueEventRow` / `IssueEventPersistence` / `IssueEventSerializer` /
`EventStore.AppendAsync` source-prefix dispatch / `IEventStore.ListIssueEventsAsync`),
the **issue `Reopen`** (`Issue.Transitions.cs:214`, `IssueReopened` event), and
the **single-issue link invariant** (`EpicGrain.GetActiveMembershipOwnerAsync` —
an issue may be actively owned by at most one non-terminal epic).

### Constraints / stakeholders

- Pure additions: no breaking API/schema changes; existing single-issue
  link/unlink and the current default list ordering are preserved.
- Server-only behavior; **no CLI / runner changes** (per proposal Impact).
- Storage is SQLite via EF Core; new table ships as a migration.
- `TimeProvider` is already injected into `EpicGrain` — event timestamps derive
  from it (a deliberate divergence from the issue path, which uses
  `DateTimeOffset.UtcNow`; see D5).
- Project is in active development; no version-compat concerns.

## Goals / Non-Goals

**Goals:**

- **G1 (reopen):** Recover a `Done`/`Closed` epic to `Idle`, re-establishing the
  active memberships that terminalization released, honoring the cross-epic
  active-membership uniqueness invariant per issue.
- **G2 (batch membership):** Link/unlink an array of issue identifiers in one
  request with per-issue outcomes and partial-failure semantics.
- **G3 (list query):** Title substring search + sort by priority or updated-at
  (asc/desc), composable, default ordering unchanged.
- **G4 (activity timeline):** Persist every epic domain event to a dedicated
  store; expose a chronological read endpoint; render a timeline on the detail
  page covering status changes, reopen, issue link/unlink, priority changes,
  creation, and updates.

**Non-Goals:**

- `Done → Closed` transition (semantically invalid; both are terminal — see
  proposal).
- Hard delete of an epic (Close already covers "no longer needed").
- Epic automation rules (auto-start next issue on reopen, etc.) — separate
  feature.
- Backfilling historical events for epics mutated before this change (no event
  was ever persisted; there is nothing to backfill).
- Publishing epic events to the CloudEvent bus / SignalR. The spec requires
  persistence + a read path only; bus publishing is left to a future change
  (noted in Open Questions).

## Decisions

### D1 — Reopen is a first-class domain transition with a dedicated guard

**Decision.** Add `Epic.Reopen(DateTime? now)` to `Epic.Transitions.cs`,
symmetric to `Issue.Reopen`. It rejects any non-terminal status by throwing a
new `EpicNotTerminalException` (mirror of `EpicAlreadyTerminalException`), sets
`_status = Idle`, `Touch(now)`, and records **two** events: `EpicStatusChanged`
(prior terminal → `idle`, consistent with every other status transition) and a
dedicated `EpicReopened` (so the timeline can distinguish recovery from generic
churn, per spec). `EnsureNotTerminal` is untouched — it still blocks
Start/Pause/Resume/Done/Close from terminal states; `Reopen` is the *only* exit.

**Grain entry point `ReopenAsync`.** After the domain transition, re-establish
active memberships: for each `EpicIssueRow` of this epic, check
`GetActiveMembershipOwnerAsync`; if no other non-terminal epic actively owns the
issue, insert an `EpicActiveIssueRow`; otherwise skip (the issue was re-homed
during the terminal period — its link record stays, it just is not re-claimed).
This is per-issue best-effort: a re-homed issue never blocks reopen or the
re-claim of the remaining issues. `POST /{id}/reopen` returns the updated
`EpicDto`; 409 `EPIC_NOT_TERMINAL` on a non-terminal epic; 404 on missing.

**Alternatives.**
- *A1 — Reuse `SetStatusAsync("idle")`.* Rejected: `SetStatusAsync` routes
  through `EnsureNotTerminal` and would reject the terminal→idle move; reopening
  is semantically distinct from a status patch and deserves its own guard
  exception/error code.
- *A2 — Reopen to `Running` instead of `Idle`.* Rejected: breaks the
  Idle→Running gate (Start requires Idle) and the spec mandates `Idle`. A
  caller re-Starts explicitly after reopen if desired.

### D2 — Batch membership: per-issue outcomes, partial-failure semantics, grain-owned invariant

**Decision.** Add `IEpicGrain.LinkIssuesAsync(IReadOnlyList<string> identifiers,
string projectId)` and `UnlinkIssuesAsync(...)`, each returning a
`IReadOnlyList<BatchMembershipOutcome>`. The grain de-duplicates the input,
resolves each identifier to an issue (the API layer resolves numbers/ids before
calling the grain, passing resolved `{issueId, issueNumber}` pairs — same
resolution the single-issue route does today), and processes each issue through
the **existing single-issue invariant**: `GetActiveMembershipOwnerAsync` gates
the active-membership insert. Per-issue outcomes:
`linked` | `already-linked` | `conflict(owningEpicId,title)` | `not-found`
(link) and `unlinked` | `was-not-a-member` (unlink). A single batch never throws
on an individual conflict/not-found; the response is HTTP 200 carrying the
per-issue result list. Idempotency mirrors the single path (already-linked and
was-not-a-member are non-errors).

**HTTP contract.**
- `POST /{id}/issues:batch` body `{ issueIds: string[] }` → 200 `{ results: BatchMembershipOutcome[] }`.
- `POST /{id}/issues:batch-unlink` body `{ issueIds: string[] }` → 200 `{ results: [...] }`.

The existing single `POST /{id}/issues` and `DELETE /{id}/issues/{issueId}`
remain unchanged (spec mandates this). The `:batch` suffix avoids colliding with
the existing collection route and signals the non-standard semantics.

**Alternatives.**
- *B1 — One transaction, all-or-nothing.* Rejected: the spec explicitly requires
  partial-failure semantics (a conflict on one issue must not roll back the
  others).
- *B2 — N parallel single-issue calls from the client.* Rejected: that is the
  status quo this change removes (latency, non-atomic UI, no aggregate outcome).
- *B3 — `PATCH /{id}/issues` with add/remove arrays.* Rejected: link and unlink
  have different outcome shapes and the codebase already uses verb-style
  sub-resources (`/done`, `/close`, `/start`); two explicit batch endpoints are
  more discoverable and match the existing style.

### D3 — Epic list query: parameterized SQL, enum-bound ORDER BY

**Decision.** Extend `EpicQuerier.ListAsync` signature to
`ListAsync(string projectId, string? search, string? sortBy, string? sortDir)`.
The raw SQL gains an optional `AND LOWER(e."Title") LIKE LOWER('%' || @search || '%')`
clause (SQLite `LIKE` is case-insensitive for ASCII; the explicit `LOWER` makes
the case-insensitivity contract obvious and robust). `ORDER BY` is selected
from a small enum-to-fragment map (`(priority,asc)` → `e."Priority" ASC, e."UpdatedAt" DESC`,
`(updated,desc)` → `e."UpdatedAt" DESC, e."Priority" ASC`, etc.) — **never**
raw string interpolation — so the sort is injection-safe. When no params are
passed, the query and ordering are byte-identical to today (regression-safe).

The list route forwards `?search=&sort=priority|updated&dir=asc|desc`; unknown
values fall back to the default ordering (no 400). `useEpics` gains the same
params in its query key, so the web list's search input + sort control drive
the query directly.

**Alternatives.**
- *C1 — LINQ over EF Core instead of raw SQL.* Rejected: the list query is a
  hand-tuned join with a grouping post-processing step (`EpicQuerier.cs:74-94`);
  rewriting it to LINQ is a larger, riskier change unrelated to this feature.
  Adding two bound parameters to the existing raw SQL is minimal and safe.
- *C2 — Client-side filter/sort.* Rejected: does not scale and breaks the
  server's authority over the list semantics.

### D4 — Epic events: dedicated table + EventStore source-prefix dispatch (mirror issues)

**Decision.** Introduce an `EpicEventRow` table whose column set is the
CloudEvents 1.0.2 envelope, identical in shape to `IssueEventRow` (PK
`(Source, Id)`, `Id` is the per-source monotonic sequence). Add
`EpicEventPersistence` (`SourcePrefix = "/mohist/epics/"`,
`EpicSource(epicId)`). Extend `EventStore.AppendAsync` with a third branch
(after the issue-prefix and workflow-prefix branches) that routes
`/mohist/epics/`-prefixed sources to the `EpicEvents` DbSet and assigns the
per-source sequence id by the same `Max(Id)+1` pattern. Add
`IEventStore.ListEpicEventsAsync(epicId, limit)` and a read path in
`EpicQuerier` that maps `StoredCloudEvent` → a timeline DTO.

A new `EpicEventSerializer` (mirror of `IssueEventSerializer`) unwraps the C# 14
`EpicEvent` union and maps each variant to a `com.mohist.epic.*` CloudEvents
type string. The `EpicEvent` union gains the `EpicReopened` variant (D1); the
serializer, `EventCatalog`, and migration type-space all register it.

**Why mirror issues instead of a shared table.** Issues and workflows are
already separate tables "so [they] remain distinct bounded contexts at the
storage layer" (`IssueEventRow.cs:10-12`). Epics are a third bounded context;
the same separation applies. A shared `Events` table was tried and reverted
(migration `20260610130306_DropEventsTable`), confirming the per-context-table
convention.

**Alternatives.**
- *D-alt-1 — Single unified `Events` table with a `Source` discriminator.*
  Rejected: explicitly reverted historically; breaks the bounded-context
  separation and complicates per-context indexing.
- *D-alt-2 — JSON event list column on `EpicRow`.* Rejected: no per-event
  indexing, no shared read shape with issues, no CloudEvent bus compatibility
  path. The envelope-in-a-row pattern is the established one.

### D5 — Replace the no-op drain with post-commit persistence via IEventStore

**Decision.** `EpicGrain` takes an `IEventStore` dependency. The no-op
`ApplyPendingEvents(epic)` is removed. Each mutation method instead captures
the aggregate's `PendingEvents` into a local list, calls `ClearPendingEvents()`
on the aggregate, persists state with `db.SaveChangesAsync()`, and then —
**post-commit, best-effort** — appends each pending event as a CloudEvent via
`IEventStore.AppendAsync`, wrapped in try/catch with `_log.LogError` on failure
(the exact pattern `IssueGrain.PublishIssueEventsAsync` /
`IssueGrain.SaveIssueAsync:601-645` already uses). This applies uniformly to
Create, Update, Start/Pause/Resume/Done/Close, Reopen, and Link/Unlink (single
and batch) so every code path persists rather than drains.

**Envelope `time` comes from `Now()` (the injected `TimeProvider`)**, not
`DateTimeOffset.UtcNow`. This is a deliberate, spec-mandated divergence from the
issue path (which uses `UtcNow` at `IssueGrain.cs:632`); the epic grain already
holds `_timeProvider` and the spec requires event timestamps to match the state
transition timestamps under a fake `TimeProvider`.

**Why post-commit best-effort instead of same-transaction.** `EventStore` opens
its own `DbContext` and owns the per-source sequence-id assignment and
source-prefix dispatch; making it participate in the grain's transaction would
require either passing the grain's context into the store (breaking its
"isolated writer" role) or duplicating sequence-id + envelope logic in the
grain. Mirroring the issue domain keeps `EventStore` the single writer and
minimizes new code. The trade-off (a crash between `SaveChangesAsync` and
`AppendAsync` loses the events for that mutation) is accepted: the timeline is
informational, and the authoritative epic state is the `EpicRow` mutation that
already committed. This matches the issue domain's documented behavior.

**Alternatives.**
- *E1 — Same-transaction writes (grain adds `EpicEventRow` to its own
  `DbContext`).* Atomic state+events, but duplicates sequence-id/envelope logic
  in the grain and bypasses `EventStore`. Higher risk, more code. Rejected.
- *E2 — Keep draining, derive timeline from `EpicRow` deltas.* Rejected: the
  spec requires a persisted event history with type-specific payloads
  (old/new status, issue number, priority values) that the row does not carry.

### D6 — Web: reopen action, batch hooks, search/sort controls, timeline section

**Decision.** Web changes are additive and follow existing entity/page
conventions:
- `primaryLifecycleAction.ts`: return `{ kind: 'reopen-epic' }` for
  `Done`/`Closed` (today it returns `null`); non-terminal behavior unchanged.
- `entities/epic/api/client.ts`: add `reopenEpic`, `batchAddEpicIssues`,
  `batchRemoveEpicIssues`, `getEpicEvents`, and extend `getEpics` with
  `{ search?, sort?, dir? }` query params. `useEpicEvents(id)` is a new query
  keyed `['epics', projectId, id, 'events']`.
- `EpicDetailPage`: render a Reopen control when `primaryLifecycleAction`
  returns `reopen-epic`; add an Activity Timeline section below the existing
  content that maps the event stream to human-readable entries (status change
  with old/new, issue link/unlink with number, priority change with old/new,
  reopen, created). Empty state when no events.
- `EpicListPage`: add a search input and a sort control above the status
  groups; both feed the `useEpics` params. Status grouping
  (`groupActiveEpics`) continues to operate on the (filtered, sorted) result.

## Risks / Trade-offs

- **[Post-commit event loss]** A crash between epic state commit and event
  `AppendAsync` loses that mutation's events. -> Accepted (matches issue
  domain); timeline is informational, `EpicRow` is authoritative. If loss
  becomes a real concern, adopt same-transaction writes (E1) later.
- **[Per-source sequence race]** `EventStore.NextEpicIdAsync` uses `Max(Id)+1`
  on a separate context — two concurrent appends for the same epic could race.
  -> Mitigated in practice: the epic grain is the single writer for a given
  epic id (Orleans single-activation per grain key), so concurrent appends for
  one source are already serialized by the grain. Cross-epic concurrency is
  irrelevant (different sources). Same model as issues; no new risk.
- **[Reopen re-claim conflict is silent]** An issue re-homed to another
  non-terminal epic during the terminal period is skipped on reopen with no
  user-visible signal in the returned `EpicDto`. -> Accepted per spec
  (re-homed issues must not block reopen). Could surface skipped issues in a
  future reopen-report payload; out of scope here.
- **[Batch all-fail response code]** Returning 200 even when every item fails
  could surprise a caller expecting a top-level error. -> The spec mandates
  per-item outcomes and forbids a top-level error when ≥1 succeeds; returning
  200 uniformly (with per-item outcomes) is the simplest contract that honors
  the spec. Documented in the endpoint help text.
- **[SQLite LIKE unicode]** `LOWER(...) LIKE LOWER(...)` is ASCII-case-insensitive
  in SQLite; non-ASCII title characters fold inconsistently. -> Accepted
  (English titles are the norm); documented as a known limitation.
- **[New table doubles event-store surface]** A third event table adds storage
  overhead and a third migration. -> Chosen deliberately to preserve the
  bounded-context-at-storage convention (D4).

## Migration Plan

- **Schema.** One new EF Core migration `AddEpicEvents` creating the
  `EpicEvents` table (columns + PK `(Source, Id)` + index
  `IX_EpicEvents_Type_Source_Id`, mirroring `AddIssueEvents`). No data
  backfill (no epic event was ever persisted). No down-grade data loss
  (`Down` drops an empty-on-legacy table).
- **Code.** Ship as one change: domain (`Reopen`, `EpicReopened`,
  `EpicNotTerminalException`), grain (`ReopenAsync`, batch methods,
  `IEventStore`-backed persistence), query (search/sort, event read), API
  (reopen, batch, events, list params), serializer + catalog + DbContext
  DbSet, web. Order within the change is not load-bearing; all are additive.
- **Deploy.** Server-only; `mo update server` rebuilds and restarts the
  managed server. No runner/CLI coordination. The migration runs on startup.
  Existing single-issue and default-list consumers see no change.
- **Rollback.** Revert the commit and re-run the prior migration state. The
  `EpicEvents` table drops (empty for legacy epics; populated only by the new
  code path). No `EpicRow` data is affected. The no-op drain behavior is
  restored for any in-flight session.

## Open Questions

- **Should epic events also publish to the CloudEvent bus / SignalR?** The spec
  requires persistence + read path only. The issue domain publishes to both
  store and bus; mirroring that for epics is consistent but expands surface
  (handler registration, catalog, EventBridge wiring). Deferred — revisit when
  a reactive UI or cross-context subscriber needs epic events.
- **Batch all-fail: 200 vs 4xx?** Design returns 200 uniformly. If API
  consumers prefer a top-level error when *every* item fails, the route can
  downgrade to 422 in that case without breaking the partial-success contract.
  Pending consumer feedback.
- **Reopen target status.** Design reopens to `Idle` (spec-mandated). Whether
  to optionally auto-`Start` on reopen (some users expect "reopen = resume
  work") is a product decision left for a follow-up; the spec's acceptance
  criterion is `Idle` only.
- **Timeline pagination.** `ListEpicEventsAsync(limit=200)` mirrors the issue
  default. A long-lived epic could exceed this; pagination/cursor support is
  not in scope but the `limit` query param is forwarded so the web can raise it
  if needed.
