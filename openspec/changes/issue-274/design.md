## Context

The Epics list page (`EpicListPage`) is the primary entry point for issue-lifecycle work, but `EpicQuerier.ListAsync` (`EpicQuerier.cs:21`) suffers N+1 amplification: it fetches N epics, then for each epic calls `ToWithProgressAsync` → `GetLinkedIssuesAsync` (`EpicQuerier.cs:90`), which at line 98 re-runs the heavy `_issuesQuery.ListAsync(projectId, all: true)` once per epic.

`IssueQuerier.ListAsync` (`IssueQuerier.cs:77`) is the most expensive call in the read path: per invocation it deserializes every `Issue.State` JSON and — twice per workflow-run id — the large `WorkflowRuns.State` JSON (`IssueQuerier.cs:495` and `521`), plus loads Comments, Attachments, agent config, and `IssuePrerequisites`. Called N times from the list path, it returns identical project-wide data each iteration. A 10-epic board fires 100+ redundant SQL statements and N×full-project JSON deserializations.

Key data-flow facts that make this solvable cheaply:

- Progress only depends on stored `Status`. `EpicProgress.Build` (`EpicProgress.cs:12`) reads `Status`, `Health`, `Priority`, `Number`, `Id`, `Title`, `CanStart`, `StartBlocker` from `LinkedIssueDto`. It never touches workflow-run state directly.
- `Status` / `ProjectId` / `Number` / `IsArchived` are **already** stored computed columns derived from `State` JSON via `COALESCE(json_extract(State,'$.camel'), json_extract(State,'$.Pascal'))` (`MohistDbContext.cs:232-241`, migration `20260625052135_AddIssueIsArchivedComputedColumn`). They auto-sync; no write path, no consistency maintenance.
- The workflow projection (`MohistDefaultWorkflowProjection.cs:9`) only writes `IssueStatus` (`backlog`/`in_progress`/`done`/`cancelled`) into `Status` — it never changes `Priority`, `Title`, `IsDraft`, or `PrerequisiteNumbers`. Those live only inside `State` JSON today.
- `CanStart`/`StartBlocker` are currently *collapsed* inside `IssueQuerier.EnrichAsync` (`IssueQuerier.cs:760`) from `IsDraft` + `PrerequisiteNumbers` + prerequisite issues' statuses. The list path must reconstruct these from derived columns instead.
- The frontend (`EpicListPage.tsx:60`) only reads `progress.activeIssues[0]`; it never reads `blockedIssues`. So the blocked/active Health split is wasted work on the list path.
- `EpicIssueRow` already carries `IssueId` + `IssueNumber` + `ProjectId`, indexed, so the join `Epics` ⋈ `EpicIssues` ⋈ `Issues` needs no schema change beyond the new derived columns.

Stakeholders: epic-board users (perf), maintainers of the read path, and anything reading `EpicWithProgressDto`.

## Goals / Non-Goals

**Goals:**
- Collapse the epic list read path to a **single SQL statement** that is constant in the number of epics (spec: "single aggregate SQL", "constant query count").
- Eliminate `WorkflowRuns.State`, Comments, Attachments, and agent-config loading from the list path (0 large JSON deserializations, 0 enrichment-table reads).
- Preserve exact `deliveredCount`, `totalIssueCount`, `readyToMarkDone`, `nextIssue`, and `CanStart` semantics by reusing the unchanged `EpicProgress.Build` pure function.
- Add derived columns with zero write-path / zero consistency maintenance (stored computed columns only).
- Keep `EpicWithProgressDto` shape, the HTTP API, and the web client unchanged (frontend unaware).

**Non-Goals:**
- Precise `blocked` vs `active` Health on the **list** path (approximated to `active`). Precise Health remains on the epic detail path (`EpicDetailDto`, single epic, unchanged).
- Changing `IssueQuerier.ListAsync` itself. Other callers are unaffected.
- Changing the epic detail path, `EpicProgress.Build`, `EpicWithProgressDto`, or any frontend code.
- Backfill / data migration of existing rows (stored computed columns populate on read; SQLite recomputes lazily).
- Indexing the new derived columns (the list query filters `Epics.ProjectId` and joins on `IssueId`/`EpicId`, none of which use the new columns as predicates).

## Decisions

### Decision 1 — Add four stored computed columns, mirroring the existing pattern

Add to `Issues`: `Title` (TEXT), `Priority` (TEXT), `IsDraft` (INTEGER/bool), `PrerequisiteNumbersJson` (TEXT), each as a **stored** computed column with the canonical `COALESCE(json_extract(State,'$.camel'), json_extract(State,'$.Pascal'))` form, registered in `MohistDbContext.OnModelCreating` with `.HasComputedColumnSql(..., stored: true)` exactly like `ProjectId`/`Number`/`WorkflowRunId` (`MohistDbContext.cs:232-239`).

`PrerequisiteNumbersJson` stores the raw JSON array (`$.prerequisiteNumbers`); parsing to `int[]` happens in application memory (SQLite has no native array type; System.Text.Json `JsonSerializer` handles it cheaply on small arrays).

**Rationale.** Stored computed columns are the established, proven mechanism in this codebase (4 precedents). They auto-track `State`, so there is no grain write site to update and no eventual-consistency window. They are readable from plain SQL, which is what enables Decision 2.

**Alternatives considered.**
- *Denormalized mirrored columns updated on write* (e.g., set `Title` in `IssueGrain`): rejected — adds write-path surface, drifts from the single-source-of-truth `State`, and duplicates the 4 existing columns' design philosophy for no benefit.
- *Virtual (non-stored) computed columns*: rejected — virtual columns recompute on every read; the list query joins them, and SQLite virtual-computed-column joins are not indexable. Stored columns compute once per write. (Note: the existing `Status`/`IsArchived` columns are virtual today; that's acceptable for those because they're indexed-and-virtual by accident of the historic migration — for join/select columns we choose stored explicitly.)
- *A separate read-model / projection table*: rejected — far heavier, needs a projector, rebuilds, and consistency tracking; overkill for four scalar fields already present in `State`.

### Decision 2 — Rewrite `EpicQuerier.ListAsync` as one aggregate SQL + in-memory grouping

Replace the N+1 loop (`EpicQuerier.cs:21-32`) with one parameterized SQL that joins `Epics` ⋈ `EpicIssues` ⨝ `Issues`, selecting only the columns the progress computation needs:

```
e."Id", e."Number", e."Title", e."Description", e."Priority", e."Status",
e."CreatedAt", e."UpdatedAt", e."PauseReason",
li."IssueId", i."Number", i."Status", i."Title", i."Priority",
i."IsDraft", i."PrerequisiteNumbersJson"
FROM "Epics" e
LEFT JOIN "EpicIssues" li ON li."EpicId" = e."Id"
LEFT JOIN "Issues"   i  ON i."IssueId"  = li."IssueId"
WHERE e."ProjectId" = @pid
ORDER BY e."Priority", e."UpdatedAt" DESC, li."CreatedAt"
```

Rows are grouped in memory by epic (a single pass over the result set), then the unchanged `EpicProgress.Build` is invoked per epic.

**Rationale.** One round-trip, O(rows) work, no per-epic enrichment. Meets the spec's "constant query count regardless of epic count" and "grouping happens in memory" requirements directly. `LEFT JOIN` naturally yields empty epics (spec: "empty epic → zero counts").

**Alternatives considered.**
- *Two split queries (epics, then linked issues in one IN-clause query)*: slightly simpler mapping but doubles round-trips and loses single-statement simplicity; rejected for the marginal mapping convenience.
- *EF Core LINQ with `Include`/`Split`*: EF's split-query would still issue multiple statements and pull full entities (including `State` blobs); rejected — raw SQL via `db.Database.SqlQueryRaw` projects exactly the columns needed and guarantees one statement.

### Decision 3 — Reconstruct `LinkedIssueDto` from flat columns; do NOT call `IssueQuerier`

Because `EpicProgress.Build` consumes `LinkedIssueDto` (not domain `Issue`), and `CanStart`/`StartBlocker` are normally computed inside `IssueQuerier.EnrichAsync` (`IssueQuerier.cs:760`), the new list path builds `LinkedIssueDto` instances directly from the joined columns. For each linked issue:

- `Status`, `Number`, `Title`, `Priority`, `Id` ← joined `Issues` columns.
- `Health` ← **`active`** whenever `Status == "in_progress"` (the list approximation; see Decision 5).
- `CanStart` / `StartBlocker` ← computed inline from `IsDraft` + parsed `PrerequisiteNumbersJson` + the stored `Status` of the referenced prerequisite issues. Prerequisite issues are resolved from the *same* result set (all the project's issues are present in the join), so no extra SQL is needed.

`cancelled` linked issues are excluded from the next-issue candidate set and from delivery counting, matching `EpicProgress.cs:14-15` / `IsCompleted`.

**Rationale.** This is the only way to feed the unchanged pure function while skipping enrichment. Building the DTO from flat columns is a handful of lines and keeps `EpicProgress.Build` byte-for-byte identical (spec: "pure function reused unchanged").

**Alternatives considered.**
- *Store `CanStart` itself as a computed column*: rejected — `CanStart` is relational (depends on *other* issues' statuses), so it cannot be a pure `json_extract` expression; it would require triggers/projectors, reintroducing the write-path cost we're eliminating.
- *Cache `IssueQuerier.ListAsync`'s result per request*: rejected — still deserializes N×(State + WorkflowRuns.State) once, and caching across requests introduces invalidation. Does not hit the "0 JSON deserializations" goal.

### Decision 4 — Reuse `EpicProgress.Build` unmodified

The pure function (`EpicProgress.cs:12`) stays exactly as-is. Its input contract (`LinkedIssueDto` with `Status`/`Health`/`Priority`/`CanStart`/`StartBlocker`/`Number`/`Id`/`Title`) is satisfied by the reconstructed DTOs from Decision 3. Output DTO (`EpicProgressDto`) and therefore `EpicWithProgressDto` are unchanged.

**Rationale.** Single source of truth for the ordering/readiness rules that govern `epic-lifecycle` ("Next-issue selection ordering"). Changing it would risk diverging list vs detail semantics.

### Decision 5 — Approximate Health as `active` on the list path; keep exact Health on the detail path

On the list path, every `in_progress` linked issue is reported under `activeIssues` and `blockedIssues` is left empty. The epic **detail** endpoint (`GetAsync`/`GetByNumberAsync` → `ToDetailAsync`, `EpicQuerier.cs:49-62, 80-85`) continues to compute precise blocked/active Health via the unchanged full-enrichment path (single epic, no perf concern).

**Rationale.** The frontend reads only `activeIssues[0]` (`EpicListPage.tsx:60`); the blocked/active distinction adds N×(workflow-run-state deserialize) for zero list-page value. `EpicWithProgressDto` is unchanged, so the web client is unaware. Meets spec scenarios "blocked reported as active on list" and "detail keeps precise Health".

**Alternatives considered.**
- *Add a `Blocked` computed column to keep exact Health on the list*: rejected — `blocked` is derived from the workflow-run projection (`MohistDefaultWorkflowProjection.cs:47`), not from `Issue.State`, so it cannot be a `json_extract` of `Issues.State`. It would require a projector writing back to `Issues`, a much larger change. Tracked as an explicit non-goal.

## Risks / Trade-offs

- `[Risk] A just-blocked linked issue renders as "In progress: #N" on the list page until refresh.` → **Mitigation**: progress bar, `readyToMarkDone`, `nextIssue`, and `CanStart` remain exact; only the blocked/active split is affected, which the frontend does not currently consume. Detail page stays precise. Documented in the spec as a scenario.
- `[Risk] Stored computed columns add CPU cost on every Issues.State write (SQLite recomputes 4 more expressions).` → **Mitigation**: the 4 new expressions are trivial scalar `json_extract`s; the table already maintains 4 computed columns (`ProjectId`/`Number`/`Status`/`IsArchived`) without incident. Writes are low-frequency (issue state transitions). Precedent is strong.
- `[Risk] Legacy rows with missing or differently-cased JSON keys break the query.` → **Mitigation**: every new column uses `COALESCE(camel, Pascal)`; missing keys yield SQL `NULL`, mapped to C# `null`/`false` defaults. Spec scenario "Missing or legacy keys yield null safely" covers this; add tests.
- `[Risk] Prerequisite cross-resolution across epics — a prerequisite issue lives in the project but is not linked to any epic.` → **Mitigation**: the join pulls all `Issues` linked via `EpicIssues`; a prerequisite not linked to *this* epic may still be linked to another epic in the same project (covered by the same result set). If a prerequisite number is not present in the joined set, treat `CanStart=false` with `StartBlocker=prerequisite-not-done` (safe default, matches detail path's unknown-prerequisite behavior). Verify with a test.
- `[Risk] Raw SQL drifts from EF model if `Issues`/`Epics` columns are renamed later.` → **Mitigation**: keep the SQL string co-located with the `EpicQuerier` and add a mapping test; the column names are PascalCase-quoted and stable (same convention since the table's creation).
- `[Trade-off] "Constant query count" is 1 statement, but the statement returns a row per (epic × linked-issue); very large boards still transfer more bytes.` → **Acceptable**: this is the minimal information the endpoint must return; any design pays this cost. No pagination is in scope (non-goal).

## Migration Plan

**Deploy (forward):**
1. Add EF Core migration `AddEpicListDerivedColumns` (timestamp after `20260625052135_AddIssueIsArchivedComputedColumn`) that adds the four stored computed columns to `Issues` in the `COALESCE(camel, Pascal)` form, mirroring `AddIssueIsArchivedComputedColumn`. Regenerate `MohistDbContextModelSnapshot.cs`. SQLite populates the columns on read; no backfill step.
2. Register the new properties on `IssueRow` + `OnModelCreating` (`.HasComputedColumnSql(..., stored: true)`).
3. Rewrite `EpicQuerier.ListAsync` to issue the single aggregate SQL and build `LinkedIssueDto`s in memory; add a private `BuildLinkedIssuesFromRows(...)` helper. Leave `GetAsync`/`GetByNumberAsync`/`ToDetailAsync` untouched.
4. Ship server; web requires no change (`EpicWithProgressDto` identical).

**Rollback:**
1. Revert the server deploy — old `ListAsync` (N+1) still works against the new schema (extra columns are simply unused by old code).
2. Revert the migration (`dotnet ef migrations remove` or a down-migration dropping the four columns). Safe because no code path depends on the columns outside the rewritten `ListAsync`.

The migration is additive (new columns only) and the DTO/API contract is unchanged, so forward and backward rolls are low-risk.

## Open Questions

- **Prerequisite-number resolution scope.** Should a prerequisite issue that exists in the project but is *not* linked to *any* epic in the result set be resolved by a second tiny lookup query, or always treated as not-done (safe default)? Leaning toward safe-default + test; confirming against `IssueQuerier.EnrichAsync` behavior in implementation.
- **`completed` vs `done` in the stored `Status` column.** `EpicProgress.IsCompleted` matches both `"done"` and `"completed"`, but the workflow projection only ever writes `"done"` to `Issues.Status`. Confirm no code path writes `"completed"` to the stored column; if none exists, no action (the `||` branch is defensive). Verified during implementation.
- **Index necessity.** No index on the new columns is planned. Confirm via the query plan (EXPLAIN QUERY PLAN) that the join uses the existing `IX_EpicIssues_ProjectId_IssueNumber` / `IX_Issues_*` indexes and does not scan. If a scan appears, revisit (likely still acceptable for project-scale row counts).
