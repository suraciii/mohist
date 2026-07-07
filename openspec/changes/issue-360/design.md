## Context

The three event truth tables (`WorkflowRunEvents`, `IssueEvents`, `EpicEvents`) persist immutable CloudEvent envelopes but carry no notion of "has this row been pushed to its handlers." A handler that exhausts retries has nowhere to park its message. This change is the storage foundation for the durable dispatcher in epic #36 — it adds delivery progress, a poison-message isolation zone, and the ports that read/write them. It changes **no user-visible behavior**; the dispatcher itself is a later issue.

`design/eventbus-v2.md` is the target design this lands. It was written before `EpicEvents` existed (#94) and lists only two tables; this change treats all three tables as first-class peers (delivery column + partial index + undelivered query uniformly), correcting that drift.

A second, independent driver is merged in from the closed #298 (backlog cleanup): a `(Type, Time)` composite index on `WorkflowRunEvents` + `IssueEvents`. The existing `(Type, Source, Id)` index does not serve the dashboard's time-window scans — `IssueMetricsQuerier.ScanIssueEventsByProjectSourceAsync` (`IssueMetricsQuerier.cs:1123-1163`) currently materializes all candidate rows and filters in memory (LINQ-to-Objects). The index is schema-only here; predicate pushdown into SQL is deferred to a #361 follow-on because dimension materialization depends on #361 first converging the producers' payload shapes.

### Current state (verified by code reading)

- **Rows are append-only** and self-describing CloudEvent envelopes: `Id` (per-source sequence), `Source`, `EventId`, `Type`, `Time`, `SpecVersion`, `Subject`, `DataContentType`, `Data` (JSON), `ExtensionsJson`. Composite key `(Source, Id)` (`MohistDbContext.cs:89-125, 357-431`).
- **`IEventStore`** (`IEventStore.cs:12-18`) exposes `AppendAsync` + three `List*Async`. Impl (`EventStore.cs`) dispatches by `Source` prefix into the right table; append uses `MAX(Id)+1` per source.
- **No delivery tracking exists** — grep for `DispatchedAt`, `IDeadLetter`, `DeadLetter` returns nothing.
- **No partial index exists yet** — grep for `HasFilter` returns nothing. This will be the codebase's first `HasFilter` usage. EF Core 10.0.8 + Microsoft.Data.Sqlite 11 support both `HasFilter` (verbatim SQL emitted into `CREATE INDEX ... WHERE`) and `SqlQueryRaw<T>` (already used at `EpicQuerier.cs:71`).
- **Store naming convention**: interface `IXxxStore` + class `XxxStore` (e.g. `InboxStore`, `IssueStore`). The dead-letter port follows this.
- **Test fakes that implement `IEventStore`** and will break compilation: `Support/NoopEventStore.cs`, `Support/RecordingEventStore.cs`, and a private nested `NoopEventStore` inside `Support/InboxProjectionTestSupport.cs:257-266`.

### Constraints / stakeholders

- Pure server storage layer — no web / CLI / runner contract change, no HTTP change, no new external dependency.
- Risk **medium** is driven entirely by one schema migration touching three populated tables + a new table + partial indexes.
- Spec先行: proposal + two specs are the source of truth. `design/eventbus-v2.md` is the target architecture.

## Goals / Non-Goals

**Goals:**
- **G1 — Delivery column + partial index on all three event tables.** Nullable `DispatchedAt`; `NULL` = undelivered. Partial index scoped to `DispatchedAt IS NULL`, keyed `(Source, Id)` so the undelivered query is index-only and its cost is bounded by the undelivered backlog, not the cumulative delivered set.
- **G2 — Unified undelivered query across all three tables**, ordered to preserve per-stream FIFO (`Source, Id`), in a single port operation — not three caller-stitched queries.
- **G3 — Per-row mark-delivered** by composite key `(Source, Id)`, timestamp supplied by the caller. Explicitly **not** a global cursor / offset / high-water-mark table (see D2).
- **G4 — `DeadLetters` table + port** for poison-message isolation: full envelope snapshot, failing handler, terminal error, attempt count. Append-only; write + query only.
- **G5 — `(Type, Time)` composite index on `WorkflowRunEvents` + `IssueEvents`** (from #298), additive, schema-only, excludes `EpicEvents`.
- **G6 — Existing append + list behavior unchanged.** `DispatchedAt` defaults NULL on append; `List*Async` do not filter/reorder on it.

**Non-Goals:**
- The dispatcher itself (single-instance Orleans grain + reminder + Polly + fan-out) — later issue per `eventbus-v2.md` landing step 3.
- Transactional event append (state + event row in one EF transaction) — `eventbus-v2.md` step 2, tracked separately (referenced as #361 in the issue body).
- Predicate pushdown of `IssueMetricsQuerier` into SQL / dimension generated columns (`IssueId`/`ProjectId`/`WorkflowRunId`/`Stage` via `json_extract`) — a #361 follow-on; blocked on producer payload convergence. **Not** in this change.
- Automated dead-letter replay / requeue / delete — explicitly reserved.
- Per-stream cursor table — rejected (D2).

## Decisions

### D1 — Separate `IDeadLetterStore`, not methods on `IEventStore`

**Decision:** dead-letter storage gets its own port `IDeadLetterStore` (`DeadLetterStore` impl), sibling to `IEventStore`, both under `Infrastructure/Events/` (port) and `Infrastructure/Data/Events/` (impl).

**Rationale:**
- `IEventStore` is the **event truth** port: append-only CloudEvent envelopes that are business facts. `DeadLetters` is an **isolation zone** for diagnosis — different lifecycle (write-once, query-for-ops), different consumer (the future dispatcher on retry-exhaustion, plus operator tooling), different invariants (snapshot immutability, no replay).
- Keeping them separate preserves the option to evolve DLQ semantics (TTL, replay, alerting) without churning the event-truth port. Mixing them couples two unrelated change axes.
- Aligns with the existing per-aggregate store convention (`InboxStore`, `IssueStore`, `WorkflowRunStore`) — one responsibility per store.

**Alternatives considered:**
- *Methods on `IEventStore`* (issue body explicitly leaves this open). Rejected: the three operations share a storage layer but not a domain concept. Forcing them onto one interface makes `IEventStore` mean "event truth + failure isolation" — two concerns. Every `IEventStore` fake (3 today) would also have to grow DLQ surface.

**New `IEventStore` members (G1–G3):**

```csharp
Task MarkDispatchedAsync(string source, long id, DateTimeOffset dispatchedAt, CancellationToken ct = default);
Task<IReadOnlyList<UndeliveredEvent>> ListUndeliveredAsync(int limit = 100, CancellationToken ct = default);
```

- `MarkDispatchedAsync` is parameterized by the delivery timestamp (per spec `event-delivery-progress:35-43`) — the caller (future dispatcher) supplies it from its `TimeProvider`. `EventStore` does **not** take a `TimeProvider` dependency for this op, mirroring the existing `AppendAsync` which also uses the envelope's own `Time` rather than generating one. Tests pass fixed timestamps (testing.md: "fixture timestamps must be fixed constants").
- `UndeliveredEvent` is a new record carrying the full envelope (`Source`, `Id`, `EventId`, `Type`, `Time`, `Subject`, `Data`, `DataContentType`, `SpecVersion`, `ExtensionsJson`) plus an `EventOrigin` discriminator (`WorkflowRun` / `Issue` / `Epic`). The composite key `(Source, Id)` is what the caller passes back to `MarkDispatchedAsync`. `DispatchedAt` is intentionally **not** projected — it is NULL by construction in this query.

### D2 — Per-row `DispatchedAt`, no global cursor (reaffirmed)

**Decision:** delivery progress is a per-row nullable timestamp. No `DeliveryOffsets` / cursor / high-water-mark table is introduced.

**Rationale:** a global cursor collapses at-least-once to at-most-once under the canonical crash (deliver → advance cursor → crash before handler completes → restart sees cursor past N → no re-offer). Per-row `DispatchedAt` is both the simplest correct marker and the one `eventbus-v2.md:74-77` already converged on. This change only lands the column + index; the dispatcher's exact mark-after-deliver ordering is the later issue.

**Alternatives considered:**
- *`(DispatchedAt, Source, Id)` composite non-partial index.* Rejected: indexes the entire delivered backlog (unbounded growth), wasting space and write cost on rows the query never reads.
- *Per-stream cursor table.* Rejected for the at-least-once reason above.

### D3 — Partial index via EF Core `HasFilter`

**Decision:** on each of the three event tables, configure:

```csharp
entity.HasIndex(e => new { e.Source, e.Id })
      .HasFilter("\"DispatchedAt\" IS NULL")
      .HasDatabaseName("IX_<Table>_Undelivered");
```

EF Core emits the `HasFilter` string verbatim into `CREATE INDEX ... WHERE "DispatchedAt" IS NULL`. SQLite has natively supported partial indexes since 3.8.0 (2013); EF Core has supported `HasFilter` since early versions. This is the codebase's first partial index — the generated migration SQL must be eyeballed in review for correct identifier quoting (double-quoted `"DispatchedAt"`, matching the column name EF Core emits).

**Rationale:** the partial index covers **only** undelivered rows. The delivered backlog pays zero index cost. The unified undelivered query becomes three index-only seeks (one per table) merged via UNION ALL.

**Alternatives considered:**
- *Unfiltered `(DispatchedAt, Source, Id)`.* Indexes the delivered backlog unnecessarily (see D2).
- *Filtered index keyed on `Id` only.* Breaks per-stream FIFO ordering — the query must `ORDER BY Source, Id`, so `Source` must lead the index key.

### D4 — Unified undelivered query via `SqlQueryRaw<T>` UNION ALL

**Decision:** `ListUndeliveredAsync` issues a single raw SQL UNION ALL across the three tables, ordered by `Source, Id`, via `db.Database.SqlQueryRaw<UndeliveredEvent>(...)`. This mirrors the established pattern at `EpicQuerier.cs:71`.

```sql
SELECT 'WorkflowRun' AS Origin, Source, Id, EventId, Type, Time, Subject,
       DataContentType, SpecVersion, Data, ExtensionsJson
FROM WorkflowRunEvents WHERE DispatchedAt IS NULL
UNION ALL
SELECT 'Issue' AS Origin, ...
FROM IssueEvents WHERE DispatchedAt IS NULL
UNION ALL
SELECT 'Epic' AS Origin, ...
FROM EpicEvents WHERE DispatchedAt IS NULL
ORDER BY Source, Id
LIMIT @limit;
```

**Rationale:**
- One round-trip; the partial index on each table makes each branch an index-only seek. Cost scales with the undelivered backlog (capped by `LIMIT`), not with total stream count — directly matching `eventbus-v2.md:119-136`.
- `Source` is globally unique across the three tables (`/mohist/workflow-runs/{id}` vs `/mohist/issues/{id}` vs `/mohist/epics/{id}`), so `ORDER BY Source, Id` preserves per-stream FIFO without an origin tiebreaker.
- Raw SQL is justified here because EF Core LINQ cannot express a cross-table UNION ALL against three separate DbSets in one query. `SqlQueryRaw<T>` is already the codebase's escape hatch for exactly this (`EpicQuerier.cs:71`).

**Alternatives considered:**
- *Three LINQ queries + in-memory merge.* Simpler to write, but three round-trips and a client-side sort that re-implements the SQL `ORDER BY`. The dispatcher's hot path should not pay this.
- *`FromSqlRaw` on a single DbSet.* Not viable — `FromSqlRaw` is DbSet-scoped and cannot UNION across tables.

### D5 — `DeadLetters` schema: full envelope snapshot, surrogate key

**Decision:** new `DeadLetters` table. Columns:

| Column | Type | Notes |
|---|---|---|
| `DeadLetterId` | `long` (PK, `ValueGeneratedOnAdd`) | Surrogate; mirrors `TaskLogEntryRow.Id`. |
| `Origin` | `TEXT` (`WorkflowRun` / `Issue` / `Epic`) | Which live table the poison message came from. |
| `Source` | `TEXT(256)` | Envelope snapshot — composite key part 1. |
| `Id` | `long` | Envelope snapshot — composite key part 2. |
| `EventId` / `Type` / `Time` / `Subject` / `DataContentType` / `SpecVersion` / `Data` (JSON) / `ExtensionsJson` (JSON) | mirrored from the event row | The snapshot must reconstruct the CloudEvent **without** consulting live tables (spec `dead-letter-store:11-27`). |
| `FailingHandler` | `TEXT(512)` | The handler identifier that exhausted retries. |
| `ErrorMessage` | `TEXT` | Terminal error message. |
| `ErrorStack` | `TEXT` nullable | Terminal stack trace, when available. |
| `AttemptCount` | `INTEGER` | Rettries consumed at the point of dead-lettering. |
| `DeadLetteredAt` | `DateTimeOffset` | When the DLQ row was written. |

Indexes: `(DeadLetteredAt)` for chronological query; `(FailingHandler, DeadLetteredAt)` for per-handler ops inspection. No unique constraint — the same logical failure may legitimately produce multiple rows (spec `dead-letter-store:38-42`: a second write appends, never overwrites).

**Rationale:**
- **Snapshot, not FK** to the live row: the live row's `DispatchedAt` gets set when the dispatcher gives up (so it leaves the undelivered query). A FK would force operators to keep the live row around forever to read its DLQ context, defeating isolation. The snapshot is immutable and self-sufficient.
- `Origin` + `(Source, Id)` are kept as plain columns (not a FK) because the DLQ records the **state at failure time**; the live row is not needed and may eventually be pruned.
- `ErrorMessage` / `ErrorStack` as separate columns (not a JSON blob) so query-by-message-text is a SQL `LIKE`, not a `json_extract`.

**`IDeadLetterStore` surface (G4):**

```csharp
public interface IDeadLetterStore
{
    Task WriteAsync(DeadLetterRecord record, CancellationToken ct = default);
    Task<IReadOnlyList<DeadLetterRecord>> ListAsync(int limit = 100, CancellationToken ct = default);
}
```

`DeadLetterRecord` is an immutable record carrying the snapshot fields. The port exposes **only** write + list (spec `dead-letter-store:28-48`): no replay, no requeue, no delete.

**Alternatives considered:**
- *FK to live event row instead of snapshot.* Rejected (above).
- *Single `ErrorJson` column.* Rejected — degrades operator query ergonomics.
- *Methods on `IEventStore`.* Rejected (D1).

### D6 — `(Type, Time)` index from #298, bundled in the same migration

**Decision:** add `IX_WorkflowRunEvents_Type_Time` and `IX_IssueEvents_Type_Time` in the same migration as the delivery-column work. **Exclude `EpicEvents`** (no `(Type, Time)` dashboard consumer — spec `event-delivery-progress:96-114`).

**Rationale:** both changes are additive schema on the same two populated tables; running two migrations back-to-back on the same tables is wasteful and risks a confusing migration history. The index is schema-only — no read path is altered to consume it; `IssueMetricsQuerier` predicate pushdown is a #361 follow-on (blocked on producer payload convergence, see Context). Bundling it now is free; deferring it would force a second schema pass later.

**Alternatives considered:**
- *Separate migration / separate issue.* Rejected — two schema passes on the same two tables for no benefit.

### D7 — Single additive migration, no backfill

**Decision:** one migration `YYYYMMDDHHMMSS_AddEventDeliveryProgressAndDeadLetters` containing: 3× `AddColumn` (`DispatchedAt`, nullable, no default), 3× `CreateIndex` (partial undelivered), 2× `CreateIndex` (`(Type, Time)`), 1× `CreateTable` (`DeadLetters` + its 2 indexes). No data backfill — `NULL` is the correct initial value for every existing row (they are all, by definition, "not yet delivered" until the future dispatcher picks them up).

**Down:** drop indexes → drop `DispatchedAt` columns → drop `DeadLetters` table. SQLite `ALTER TABLE DROP COLUMN` is supported (3.35.0+, 2021) and the codebase already drops columns in migrations (e.g. `20260615065058_DropAgentSessionDeadColumns`).

**Rationale:** nullable-add-column is an instant, online schema change on SQLite (no rewrite). The partial indexes are created over an initially-empty predicate set (all rows have `DispatchedAt IS NULL` at first), so the index build is cheap but non-empty — it indexes the current backlog, which is the correct starting state for a future dispatcher.

**Alternatives considered:**
- *Backfill existing rows to a sentinel timestamp (e.g. epoch).* Rejected — would silently mark every historical event "delivered" and hide them from the future dispatcher. The honest initial state is "undelivered."

## Risks / Trade-offs

- **[First partial index in the codebase] → generated SQL must be eyeballed.** EF Core `HasFilter` emits the filter string verbatim. Risk: wrong identifier quoting silently produces a non-partial index (SQLite would accept `WHERE DispatchedAt IS NULL` unquoted as a column ref in many contexts, but the codebase convention is double-quoted identifiers). Mitigation: pin the filter to `"\"DispatchedAt\" IS NULL"` and assert in a unit test that the migration-produced schema contains a partial index (query `sqlite_master` for the `WHERE` clause).
- **[Three-table UNION ALL raw SQL] → schema drift if columns diverge.** The three event row classes are deliberately mirrored today, but a future column added to one and not the others would silently break the UNION ALL (column-count mismatch). Mitigation: the unit test for `ListUndeliveredAsync` exercises all three origins; any drift fails there.
- **[Snapshot columns duplicate the envelope shape] → two places to update when CloudEvent evolves.** Adding a CloudEvent attribute now requires touching both the live row classes and `DeadLetterRow`. Mitigation: accepted — the DLQ is a snapshot, not a view; duplication is the cost of immutability. Documented in `DeadLetterRow.cs` via a `// mirror of WorkflowRunEventRow envelope columns` comment header pointing at the source of truth.
- **[`MAX(Id)+1` append races remain]** — `EventStore.AppendAsync` still computes the per-source next id via `MAX(Id)+1` in a separate DbContext. `eventbus-v2.md:215` flags this for a DB-autoincrement fix. This change does **not** touch it: the issue is out of scope (non-goal: "do not change event truth table existing columns") and Orleans single-writer grain already serializes per-source writes. Noted as a follow-on, not a risk introduced here.
- **[Dead-letter snapshot size]** — a poison CloudEvent with a large `Data` payload is copied in full into `DeadLetters`. At personal-developer scale this is fine; at high volume it could bloat the DLQ. Mitigation: none in this change — the DLQ is operator-inspected, not auto-traversed; a future TTL/prune is a non-goal-of-this-issue but a cheap follow-on.

## Migration Plan

1. **Code first, schema via `dotnet ef migrations add`.** Implement the three row classes (`DispatchedAt`), `DeadLetterRow`, `MohistDbContext` configuration (columns + indexes + partial indexes + `DeadLetters` DbSet), the port additions on `IEventStore`, `IDeadLetterStore` + `DeadLetterStore` impl, and the `EventStore` method impls.
2. **Scaffold the migration** with `dotnet ef migrations add AddEventDeliveryProgressAndDeadLetters` under `Infrastructure/Data/Migrations/` following the `YYYYMMDDHHMMSS_PascalCaseDescription.cs` convention. Inspect the generated `Up` for the three partial-index `WHERE` clauses and the UNION-friendly column orderings.
3. **Fix compilation breaks** in the three `IEventStore` fakes (`Support/NoopEventStore.cs`, `Support/RecordingEventStore.cs`, nested `InboxProjectionTestSupport.NoopEventStore`) — add no-op `MarkDispatchedAsync` / empty `ListUndeliveredAsync`. Add a `NoopDeadLetterStore` to `Support/`.
4. **Add tests** (see below), then `npm test` (server). `TreatWarningsAsErrors` is the C# lint gate.
5. **Deploy:** the migration runs on server startup (`db.Database.Migrate()`). Additive nullable columns + new indexes + new table — no lock contention concern at single-developer scale.

**Rollback:** `dotnet ef migrations script <new> <prev>` produces the Down SQL; or `dotnet ef database update <prev>`. The Down path drops indexes → drops `DispatchedAt` columns → drops `DeadLetters`. No data loss except the (empty-at-this-point) DLQ rows. Existing event rows are untouched by rollback since `DispatchedAt` is additive.

### Tests

- **Spec (extend `EventStoreSpecs.cs`, `MohistDbFixture`):**
  - Append leaves `DispatchedAt` NULL; list still returns the row.
  - `MarkDispatchedAsync(source, id, T)` sets only that row; other rows (same table + cross-table) untouched.
  - Re-marking an already-delivered row is harmless (idempotent).
  - `ListUndeliveredAsync` returns rows from all three tables; delivered rows are excluded; ordering is `Source, Id` (per-stream FIFO) — assert with two undelivered rows in the same `Source` at ascending `Id`.
- **Unit (`DeadLetterStoreTests.cs`, in-memory shared-cache SQLite per testing.md):**
  - Write then list returns the record; envelope snapshot fields round-trip.
  - Second write for the same logical failure appends a new row (count = 2), does not overwrite.
  - `IDeadLetterStore` exposes only `WriteAsync` + `ListAsync` (assert no replay/requeue/delete on the interface — compile-time guarantee).
- **Migration unit test:** after `Migrate()`, query `sqlite_master` for each event table's indexes and assert one entry's SQL contains `WHERE "DispatchedAt" IS NULL` (guards the D3 risk).

All timestamps in tests are fixed constants (testing.md: fixture timestamps must be fixed); `MarkDispatchedAsync` receives the timestamp as a parameter so no `FakeTimeProvider` is needed for the EventStore itself.

## Open Questions

1. **`(Type, Time)` index column order for the #361 follow-on.** Should it be `(Type, Time)` or `(Type, Time, Source)` as a covering index for `IssueMetricsQuerier`'s project-source predicate? Deferred to the #361 follow-on — this change only adds the index the dashboard will eventually use; the exact covering shape is a profiling-driven decision once predicate pushdown lands.
2. **`DeadLetterRecord` DTO vs reusing `StoredCloudEvent`.** The DLQ snapshot is envelope + diagnostic fields (handler/error/attempts) — leaning toward a dedicated `DeadLetterRecord` to avoid overloading `StoredCloudEvent`. Settled in implementation; not a structural risk either way.
3. **Where `MarkDispatchedAsync`'s timestamp originates at runtime.** This change defines the port as caller-supplied; the future dispatcher will pass its injected `TimeProvider.GetUtcNow()`. Confirmed consistent with `eventbus-v2.md:214` (TimeProvider injection is a dispatcher concern, not a storage concern).
