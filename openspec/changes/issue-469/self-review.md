# Self-Review — Issue 469 (OTel HTTP query cost bounds), pass 2

Second reviewer pass over `proposal.md`, `specs/`, `design.md`, `tasks.json`
against the issue body and the live codebase, after the pass-1 fixes were
applied.

## Verdict

Every finding from pass 1 (P1–P6) has been genuinely resolved in the artifacts.
The plan is materially stronger: the test seam is specified, safety constants
are non-weakenable, paths are accurate, the misleading admission scenario is
fixed, and the SQLitePCLRaw/`Handle` risks are addressed. One NEW must-fix gap
fell out of the fix itself: the design leaves the fate of the now-superseded
`ExecuteRawQuery` ambiguous, and neither the design nor the tasks address its
three existing unit tests — which will collide with the repo's
"禁止新旧并存" (no old/new coexistence) testing convention once the route is
switched to the new seam.

**Promise: FAIL** (single must-fix, small scope)

## Pass-1 findings — verification of resolution

- **P1 (interruption test seam):** RESOLVED. New Decision 6 specifies a
  server-side `IOtelQueryExecutor` seam + `FakeOtelQueryExecutor` for the
  route contract, and a deterministic real-wiring test (recursive CTE +
  `FakeTimeProvider` advance → `sqlite3_interrupt`, asserting outcome not
  duration). The recipe is sound: `sqlite3_interrupt` is checked per
  `sqlite3_step`, and a large bounded CTE guarantees the query is still stepping
  when the fake clock fires, so no `Stopwatch`/`Thread.Sleep` is needed.
- **P2 (paths):** RESOLVED. `proposal.md` Impact now uses `Otel/`; verified no
  `src/.../Telemetry/` source paths remain in any plan artifact. (The
  `tests/.../Specs/Telemetry/` directory reference is the real test folder, not
  the bug.)
- **P3 (admission scenario):** RESOLVED. The scenario is reframed as the
  defense-in-depth read-only backstop; the misleading "ATTACH bypasses the
  keyword layer" premise is gone.
- **P4 (safety-constant placement):** RESOLVED. Decision 2 mandates `public
  const` on `TraceQuerier` (mirroring `MaxListLimit`), explicitly NOT
  `OtelOptions` properties; the deferred "knobs vs constants" open question is
  removed. Tasks updated accordingly.
- **P5 (`query_cancelled`):** RESOLVED. Migration step 3 states client
  disconnect returns no response body.
- **P6 (SQLitePCLRaw):** RESOLVED. Decision 1 + T-002 require confirming
  `SQLitePCL.raw` resolves and add a `Handle`-visibility guard test.

Issue AC coverage remains complete (body cap AC1; row/byte + reason AC2;
interrupt + release AC3; read-only + SELECT/WITH + no multi-statement AC4;
large-rows / single-big-value / recursive-CTE / cancel / normal-aggregate with
no wall-clock AC5), and the non-goals (no workbench/queue, no writes, CLI
untouched) are honored. `tasks.json` is valid JSON with a sound DAG and every
task carries test-bearing acceptance criteria.

## Must-fix finding

### F1 — `ExecuteRawQuery` fate is ambiguous and its 3 unit tests are unaccounted for (no-coexistence violation)

`TraceQuerier.ExecuteRawQuery` has exactly one production caller — the
`/query` route handler — and three unit tests in `TraceQuerierSpecs.cs`
(`ExecuteRawQuery_SelectAllRows_ReturnsDictionaries`,
`ExecuteRawQuery_AggregateCount_ReturnsSingleRow`,
`ExecuteRawQuery_NullCell_BecomesNullInDictionary`). After T-002 switches the
route to `IOtelQueryExecutor.Execute → ExecuteBoundedQuery`, that sole caller
is gone.

The design (Decision 4, line 76) says `ExecuteRawQuery` "gains an overload (or
a new `ExecuteBoundedQuery`)" — the "(or …)" hedge is the defect:

- If the build reads "overload" and keeps both methods, that is old/new
  coexistence, directly violating `AGENTS.md`'s testing principle
  ("迁移/回归完成后删旧文件，禁止新旧并存").
- If the build reads "replace" and removes `ExecuteRawQuery`, the three unit
  tests stop compiling and their migration is nowhere specified.

Neither the design nor any task names `ExecuteRawQuery` or its three tests, so
the build agent has no instruction on either branch.

**Fix (small):** in the design, change Decision 4 to state unambiguously that
`ExecuteBoundedQuery` **replaces** `ExecuteRawQuery` (not an overload), and add
a T-002 acceptance criterion (or note) that the three
`ExecuteRawQuery_*` unit tests are migrated onto `ExecuteBoundedQuery`
(returning `QueryResult`, asserting `Rows`/non-truncated) so no dead method and
no orphaned test remain.

## Non-blocking notes (for the build agent / a future polish pass)

- **N1 (minor staleness):** `proposal.md` Impact line 24 still lists "Web UI:
  any consumer … must surface truncation … concrete placement decided in
  design." The design correctly resolved that no current consumer exists, so
  this is conditional-only and not wrong, but it reads as outstanding work.
  Optional: tighten to note no consumer exists today.
- **N2 (minor test naming):** the existing test
  `PostQuery_InsertBypassingKeywordCheck_RejectedByReadOnlyMode` is misnamed
  (its own comment admits `ATTACH` is caught at the keyword layer, not the
  engine). T-001's test-hardening (asserting stable codes) is the natural place
  to rename/clarify it to match the reframed admission scenario, but this is
  cosmetic and the build agent can handle it without plan changes.
- **N3 (non-issue, confirmed):** `QueryResult` always emits `truncated: false`
  for non-truncated responses; the response-bound spec's "SHALL NOT carry a
  truncation indicator" is satisfied by `truncated: false` (it does not present
  the result as truncated). No action needed.

<promise>FAIL</promise>
