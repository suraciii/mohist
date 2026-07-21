# Self-Review — Issue 469 (OTel HTTP query cost bounds)

Reviewer pass over `proposal.md`, `specs/`, `design.md`, `tasks.json` against the
issue body (AC + Non-Goals + Fix Shape) and the live codebase.

## Verdict

The plan is well-structured and the capability/spec/task split is clean and
traceable. The core technical thesis — that `SqliteCommand.Cancel()` is a no-op
and `CommandTimeout` only covers `SQLITE_BUSY`, so real interruption requires
`sqlite3_interrupt` on the public `SqliteConnection.Handle` — is correct and
well-evidenced. However, there are build-readiness problems that should be fixed
before autonomous build, most importantly the testing approach for execution
interruption (a hard, wall-clock-free constraint in this repo) and the placement
of the safety constants.

**Promise: FAIL**

## What is solid

- Capability boundaries map 1:1 to the three acceptance areas and to three
  self-contained spec files; every requirement has `#### Scenario:` blocks with
  WHEN/THEN, and scenarios use normative SHALL.
- All five issue ACs are covered: body cap (AC1 → admission), 1000-row/4 MiB +
  reason (AC2 → response-bound), interrupt + release (AC3 → execution-budget),
  read-only + SELECT/WITH + no multi-statement (AC4 → admission), test matrix
  without wall-clock (AC5 → across specs + tasks).
- Non-goals honored: no workbench/queue, read-only preserved, `mo otel query`
  explicitly left alone (Decision 5, plus a CLI-unaffected acceptance criterion
  in T-003).
- The Web-UI line in the proposal was correctly scoped down in design/tasks:
  verified no current Web UI or CLI HTTP consumer of `/otel/api/query` exists
  (the CLI reads `otel.db` directly), so dropping Web UI work is right.
- `tasks.json` is valid JSON; the dependency graph is an acyclic linear chain
  (`T-001` → `T-002` → `T-003`), deps point only to strictly-lower-priority
  tasks, every task has test-bearing acceptance criteria and `passes=false`.

## Findings (must/may fix)

### P1 — Execution-interruption has no specified test seam (blocker for AC5)

The execution-budget spec requires the interruption be "observable at the reader
level", and the issue's AC5 + `design/testing.md` forbid wall-clock assertions.
The design (Decision 2) says this is drivable by advancing `FakeTimeProvider`.
That is insufficient as specified: with `Microsoft.Data.Sqlite` against the
in-memory fixture, a query completes before the test advances the fake clock, so
there is no in-flight reader to interrupt; and using a recursive CTE that takes
real time to stay in-flight is itself a wall-clock dependency. The CLI side has
an `IOtelQueryExecutor` seam for exactly this, but the plan introduces no
equivalent seam for the server query path and does not say how a test holds a
query in-flight and then fires the budget/cancel token.

- **Risk:** the build either produces a flaky wall-clock test or a test that
  never actually exercises mid-query interruption, undercutting the highest-risk
  capability and violating a repo hard-constraint.
- **Fix:** the design must specify the test seam — e.g. an injectable
  query-execution abstraction (or a controllable `OtelDb` whose reader blocks on
  a `TaskCompletionSource` until cancelled) — and the T-002 acceptance criteria
  must reference it so the build agent knows how to make interruption observable
  without wall-clock.

### P2 — Proposal cites a non-existent `Telemetry/` directory (accuracy)

`proposal.md` Impact lists
`packages/server/src/Mohist.Server/Telemetry/TraceQuerier.cs` and
`Telemetry/OtelDb.cs`. There is no `Telemetry/` directory: the real files are
`packages/server/src/Mohist.Server/Otel/TraceQuerier.cs` and
`Otel/OtelDb.cs` (the design Context and `tasks.json` use the correct `Otel/`
path, so this is a proposal-only inconsistency).

- **Fix:** correct the path in `proposal.md` Impact to `Otel/`.

### P3 — Admission spec scenario uses an example that does not match its premise

`otel-http-query-admission` "Write attempts that bypass the keyword layer are
rejected by the read-only engine" uses `ATTACH DATABASE …` as the example. But
`ATTACH` is rejected at the **keyword** layer (`ValidateSelectOnly` rejects any
head keyword ≠ SELECT/WITH), so it never reaches the engine — the existing test
`PostQuery_InsertBypassingKeywordCheck_RejectedByReadOnlyMode` is itself misnamed
in this same way, and its comment admits ATTACH is caught at the keyword layer.
As written the scenario's example contradicts its "keyword layer does not reject
on its head" premise, and a SELECT-headed statement that SQLite still treats as a
write/schema change is essentially non-constructible in SQLite.

- **Fix:** reframe the scenario as defense-in-depth verification (engine rejects
  writes that the keyword layer was not designed to catch) and drop the
  misleading ATTACH example, or replace it with an accurate description; keep
  the read-only-engine assertion but don't tie it to a bypass that can't happen.

### P4 — Safety-constant placement contradicts "not user-tunable" (safety)

Decision 2 says budgets live on `OtelOptions` as "read-only-at-runtime
constants… not exposed as tunable user knobs," but `OtelOptions` is an `IOptions`
class bound from `Mohist:Otel` config (`services.Configure<OtelOptions>(…)` in
`MohistServiceRegistration.cs`), so any property added there is inherently
config-overridable. For a safety bound on a high-risk/P1 issue, leaving open the
possibility that `MOHIST__Otel__*` weakens the body/row/byte/execution ceilings
is a real regression vector. The Open Questions section defers this
("knobs or hard constants?") rather than resolving it.

- **Fix:** decide now — the budgets should be `public const` static fields
  (mirroring `TraceQuerier.MaxListLimit`), not `OtelOptions` properties, so they
  cannot be weakened by config. Update Decision 2 / Open Questions / tasks to
  reflect that, and drop the "extend OtelOptions with the budget group" wording.

### P5 — `query_cancelled` mapping is moot for client disconnect (minor)

The design lists `query_cancelled` as a code to map, but when the client aborts
the HTTP request there is no response channel to send a body to. Harmless, but
slightly misleading: only `query_execution_budget_exhausted` can actually be
returned to a live caller.

- **Fix:** note that client-cancel produces no response (the request is gone);
  keep the interruption/connection-release behavior, drop the implication that a
  `query_cancelled` body is returned to the disconnecting client.

### P6 — `raw.sqlite3_interrupt` compile-time availability unverified (minor)

Decision 1 calls `raw.sqlite3_interrupt` via `SQLitePCL.raw`. No code in
`packages/server` references `SQLitePCL` directly today; it is only a transitive
dependency of `Microsoft.Data.Sqlite`. Transitive `PackageReference` flow usually
surfaces the types, but this is unverified for this codebase, and neither the
design nor the tasks mention adding a direct `PackageReference` to
`SQLitePCLRaw.core` if the compiler cannot resolve `SQLitePCL.raw`.

- **Fix:** add a one-line note in Decision 1 / T-002 that the build must confirm
  `SQLitePCL.raw` resolves (and add a direct `<PackageReference
  Include="SQLitePCLRaw.core" />` using the central version if not).

## Minor notes (non-blocking, for the build agent)

- Two `OtelOptions` types coexist (`Mohist.Server.Otel.OtelOptions` for the
  collector/query side, `Mohist.Server.Infrastructure.Config.OtelOptions` for the
  outbound exporter), both bound from `Mohist:Otel`. `tasks.json` already targets
  the correct one (`Otel/OtelOptions.cs`, the collector type). No change needed,
  but the build agent should edit that exact file.
- `T-002 dependsOn T-001` is a sequencing dependency (shared handler/`OtelOptions`
  edits), not an output-consumption dependency; harmless, but `T-002` does not
  strictly consume `T-001`'s output.

<promise>FAIL</promise>
