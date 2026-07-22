# Self-Review — Issue 469 (OTel HTTP query cost bounds), pass 3

Third reviewer pass over `proposal.md`, `specs/`, `design.md`, `tasks.json`
against the issue body and the live codebase, after the pass-1 and pass-2 fixes.

## Verdict

All pass-1 (P1–P6) and pass-2 (F1, N1, N2) findings are verified resolved in the
artifacts. The plan is internally coherent, every acceptance criterion is
covered, the technical thesis is correct and well-evidenced, the testability
seam makes the hardest requirement (no-wall-clock interruption) deterministically
verifiable, the safety constants cannot be weakened by config, and the task
graph is a valid DAG with test-bearing acceptance criteria on every task. Only
cosmetic staleness remains, none of which blocks the build.

**Promise: PASS**

## Prior findings — resolution confirmed

- **P1 (interruption test seam):** Decision 6 specifies `IOtelQueryExecutor` +
  `FakeOtelQueryExecutor` for the route contract, and a deterministic
  real-wiring test (recursive CTE + `FakeTimeProvider` → `sqlite3_interrupt`,
  asserting reader-terminated-early + connection-disposed by outcome, not
  duration). The recipe is sound: `sqlite3_interrupt` is checked per
  `sqlite3_step` and its pending-flag persists, so advancing the fake clock
  before or during the read loop deterministically stops the reader without any
  `Stopwatch`/`Thread.Sleep`.
- **P2 (paths):** Source paths use `Otel/`; verified no `src/.../Telemetry/`
  references remain.
- **P3 (admission scenario):** Reframed as the defense-in-depth read-only
  backstop; the misleading ATTACH-bypass premise is gone.
- **P4 (safety-constant placement):** `public const` on `TraceQuerier`, never
  `OtelOptions`; the deferred knobs-vs-constants question is removed.
- **P5 / P6:** Client-disconnect returns no body; `SQLitePCLRaw.raw` resolution
  + a `Handle`-visibility guard test are required.
- **F1 (ExecuteRawQuery fate):** Decision 4 now states `ExecuteBoundedQuery`
  **replaces** (not overloads) `ExecuteRawQuery`; the three `ExecuteRawQuery_*`
  unit tests migrate; T-002 carries an acceptance criterion mandating this with
  no old/new coexistence.
- **N1 / N2:** Proposal Web-UI line tightened; T-001 acceptance added to rename
  the misnamed `PostQuery_InsertBypassingKeywordCheck…` test.

## Coverage (unchanged, re-verified)

- **AC1** (body cap before buffering) → admission spec + Decision 3 + T-001.
- **AC2** (≤1000 rows, ≤4 MiB, caller knows truncation + reason) → response-bound
  spec + Decision 4 + T-003.
- **AC3** (long query + client cancel interrupt SQLite + release connection) →
  execution-budget spec + Decisions 1/2/6 + T-002.
- **AC4** (read-only + single SELECT/WITH, no multi-statement bypass) →
  admission spec + Decision 5 + T-001 (preserved behavior).
- **AC5** (large rows / single big value / recursive CTE / cancel / normal
  aggregate, no wall-clock) → distributed across T-001/T-002/T-003 acceptance;
  normal-aggregate coverage preserved via the migrated `AggregateCount` test.
- **Non-goals** (no workbench/queue, no writes, `mo otel query` untouched) →
  honored; CLI isolation asserted in T-003.

Spec/header integrity: 3 spec files, 11 requirements, 23 `#### Scenario:`
blocks, no malformed headings. `tasks.json`: valid JSON, 3-task DAG
(T-001 → T-002 → T-003), every task acceptance-bearing with `passes=false`.

## Non-blocking observations (cosmetic; safe to defer)

- **O1:** `design.md` Risk line 123 still says "update Web UI caller and
  integration specs in the same change," but no Web UI caller exists (the
  proposal/migration correctly say so). The mitigation text could drop the Web
  UI reference. Build agent will not be misled — migration step 4 is
  authoritative.
- **O2:** `proposal.md` line 26 says the response change is "additive or a
  contract change … decided in design"; the design has since decided it is a
  contract change (`data` → `{rows, truncated, truncate_reason}`). Phrasing is
  mildly stale but points the reader to the design, where the answer lives.

Neither observation affects build correctness, testability, or safety, and a
build agent following the design + tasks will implement correctly.

<promise>PASS</promise>
