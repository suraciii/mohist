# Self-Review — Issue 529

## Proposal

The proposal correctly identifies the problem (`mo otel query` bypasses the Server query safety net by reading local `otel.db`), proposes routing through `POST /otel/api/query`, adding `--json` field selection, and removing `--db`/local SQLite. The single capability `otel-cli-query` is the right boundary — this is one CLI command's behavior change. Impact section accurately identifies CLI, Server API, docs, and dependencies.

**P3 (observation):** The proposal does not mark `--db` removal with **BREAKING** as the template suggests. Acceptable given AGENTS.md states "无需考虑版本兼容", but noting for completeness.

## Specs

Five requirements cover the full behavior surface: Server-routed execution, `--db` rejection, truncation surfacing, JSON field selection, and error/unavailability handling. All use `#### Scenario` formatting, SHALL/MUST language, and are self-contained with no delta headers or cross-references. Every requirement has at least one scenario.

The spec's "SHALL include `rows`, `truncated`, and `truncate_reason`" uses floor language, so the design's addition of `columns` as a fourth selectable field is spec-compliant.

No gaps found.

## Design

Six decisions, each with rationale and alternatives where applicable. Verified against the codebase:

- **Decision 1** (add `columns` to `QueryResult`): `TraceQuerier.cs:165-170` collects `fieldNames` before the read loop; the three return sites at lines 178, 204, 215 all need the field added. Claim is accurate.
- **Decision 2** (POST through envelope): `ExtractEnvelope` and `SendAsync` are the existing path used by `mo otel status`. Claim is accurate.
- **Decision 3** (reuse table renderer): `RenderTableAsync` at line 300 handles column/row rendering. Claim is accurate.
- **Decision 4** (`--json` via `JsonSelectionOption`): The `JsonSelectionOption(descriptor)` pattern is used across the CLI. Claim is accurate.
- **Decision 5** (remove SQLite stack): `MohistCliCommands.cs:38` passes `provider.GetService<IOtelQueryExecutor>() ?? new SqliteOtelQueryExecutor()`; `RunAsync` at line 174 has `IOtelQueryExecutor? queryExecutor = null`. Both are removal targets. Claim is accurate.
- **Decision 6** (test migration): `FakeOtelQueryExecutor` exists at `tests/Support/FakeOtelQueryExecutor.cs`; `RecordingHttpHandler` is the established HTTP fake pattern. Claim is accurate.

Risks and migration plan are appropriate. No open questions.

## Tasks

Three tasks in a linear DAG (T-001 → T-002 → T-003):

- **T-001** (Server `columns` addition): correct split — different package from T-002, independently testable, T-002 consumes its output. All five acceptance criteria are verifiable.
- **T-002** (CLI rewrite + `--json` + removal + test migration): correctly merges the tightly-coupled CLI changes into one task. Acceptance criteria cover all spec requirements.

**P2 (minor):** T-002 acceptance criteria includes `npm run typecheck` alongside `dotnet build packages/cli`. The CLI is a C# (.NET) project — `npm run typecheck` applies to TypeScript packages (web, runner), not the CLI. The correct verification is `dotnet build packages/cli` alone (which enforces `TreatWarningsAsErrors`). The implementing agent will naturally use the correct C# build command, so this is non-blocking.

- **T-003** (docs update): correct dependency on T-002. Acceptance criteria are verifiable.

## Cross-artifact consistency

| Proposal claim | Spec requirement | Design decision | Task |
|---|---|---|---|
| Route through Server | "executes SQL through the Server query surface" | Decision 2 | T-002 |
| Render truncation | "Truncation is surfaced to the caller" | Decision 3 | T-002 |
| Add `--json` | "supports JSON field selection" | Decision 4 | T-002 |
| Remove `--db` | "A local database path is not accepted" | Decision 5 | T-002 |
| (implicit: empty-result headers) | "render the column headers and rows" | Decision 1 (`columns` field) | T-001 |
| Close docs gap | (behavioral spec) | Migration step 5 | T-003 |

No orphaned requirements, no untasked decisions, no spec–design contradictions.

## Verdict

The plan is ready to build. The P2 `npm run typecheck` wording in T-002 is a non-blocking acceptance-criteria imprecision that the implementing agent will correct naturally when working in the C# project.

<promise>PASS</promise>
