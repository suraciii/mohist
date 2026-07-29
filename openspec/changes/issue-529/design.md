## Context

`mo otel query <sql>` currently opens the local `otel.db` directly through a CLI-internal `SqliteOtelQueryExecutor` (`packages/cli/Mohist.Cli/MohistCliCommands.Otel.cs:62`). It resolves a database path via `--db` or `MOHIST_DB_PATH`/`$HOME/.mohist`, opens a read-only `SqliteConnection`, and materializes the full result set. The Server already provides a bounded, safety-netted query surface at `POST /otel/api/query` (`OtelQueryRoutes.cs:65`) with SELECT-only admission, physical read-only enforcement, execution-budget interruption, and row/byte response caps with a truncation indicator. The CLI bypasses all of this, and silently reads local data even when it targets a remote Server. `design/cli.md:118` already records the decision to route `query` through Server.

The existing CLI `--json` field-selection infrastructure (`JsonSelectionOption`, `ResourceDescriptor`, `JsonSelection.Parse`) is used by every other resource leaf command but not by `mo otel query`.

## Goals / Non-Goals

**Goals:**

- Route `mo otel query` through `POST /otel/api/query`, consuming the Server's existing query contract without re-implementing admission, budget, or truncation logic in the CLI.
- Render the Server's result (including truncation state and reason) for both human-readable and `--json` output.
- Add `--json` field selection consistent with the rest of the CLI.
- Remove the CLI's local SQLite query dependency, the `--db` option, and all local-path resolution.

**Non-Goals:**

- Do not change the Server's query safety policy (admission rules, budget constants, truncation logic).
- Do not change `mo otel status` (already HTTP-based).
- Do not add new query features (filters, pagination, saved queries).
- Do not remove direct DB inspection as a developer escape hatch — it remains a non-CLI path (e.g. `sqlite3 otel.db`).

## Decisions

### 1. Add `columns` to the Server `QueryResult` response

The current `QueryResult` (`TraceQuerier.cs:411`) carries `rows` (array of column→value dictionaries), `truncated`, and `truncate_reason`. Column names are only recoverable from the first row's keys. For empty results (0 rows) or byte-limit-truncated results that dropped every row, the CLI cannot determine the column headers.

`TraceQuerier.ExecuteBoundedQuery` already collects `fieldNames` from the reader at `TraceQuerier.cs:166-170` before the read loop. Adding a `columns` field to `QueryResult` is a one-property additive change: include `fieldNames` in every `QueryResult` return (including both truncation paths and the normal path).

**Alternative considered:** CLI infers columns from `rows[0].Keys` only. Rejected because it loses headers for empty results, which the current CLI renders and users expect.

### 2. CLI sends POST and reads the standard envelope

The query command sends `POST /otel/api/query` with body `{ sql }` via `api.SendAsync`, then reads the response through the existing `MohistCliApi.ExtractEnvelope` — the same path `mo otel status` uses (`MohistCliCommands.Otel.cs:255-262`). On `success: false`, surface `error` and `code` to stderr with a non-zero exit. On `HttpRequestException`, surface the standard `ServerUnavailableMessage`.

The Server's stable error codes (`query_not_select`, `query_sqlite_error`, `query_execution_budget_exhausted`, `query_request_too_large`, `query_missing_sql`, `query_malformed`) pass through without CLI-side interpretation.

### 3. Human-readable rendering reuses the existing table renderer

The current `RenderTableAsync` (`MohistCliCommands.Otel.cs:300`) renders column headers + separator + rows. After the change, columns come from the `columns` array in the Server response; rows come from the `rows` array (each row is a dict keyed by column name). When `truncated` is true, append a notice line naming `truncate_reason` (e.g. `(truncated: row_limit)`). For zero rows, render the header + `(0 rows)` sentinel — matching the current behavior.

### 4. `--json` field selection via a static descriptor

Declare a `ResourceDescriptor(Single, ["columns", "rows", "truncated", "truncate_reason"])` and wire it through `JsonSelectionOption(descriptor)`. Bare `--json` resolves to discovery (lists the four field names, exits 0, no Server contact). `--json <fields>` projects the single result object after the POST returns, emitting only the selected keys. Invalid fields are rejected locally with exit 2 and no remote request — identical to every other resource leaf command.

### 5. Remove the CLI's local SQLite query stack

Remove from `packages/cli/Mohist.Cli/MohistCliCommands.Otel.cs`:
- `IOtelQueryExecutor`, `SqliteOtelQueryExecutor`, `OtelQueryResult`, `OtelQueryException`
- `ResolveDatabasePath`, `ResolveDefaultDataDirectory`, `MainDbPathEnvironmentVariable`, `DefaultDatabaseFileName`, `DataDirectoryName`
- The `--db` option from `BuildQuery`

Remove from `MohistCliCommands.cs`:
- The `queryExecutor` parameter in `RunAsync` (`MohistCliCommands.cs:174`)
- The `provider.GetService<IOtelQueryExecutor>() ?? new SqliteOtelQueryExecutor()` resolution at `MohistCliCommands.cs:38`; simplify `OtelCommands.Build(api)`

The `IEnvironmentVariableProvider` parameter to `OtelCommands.Build` is no longer needed (it was only used for database path resolution) and is removed.

**Note:** `MOHIST_DB_PATH` remains a Server-side environment variable (`OtelOptions.cs:19`, `MohistServiceRegistration.cs:256`); only the CLI's copy of the constant is removed.

### 6. Test migration to HTTP fakes

The CLI test file `CliOtelCommandSpecs.cs` currently uses `FakeOtelQueryExecutor` (returning fixed results or throwing). After the change, all query tests use `RecordingHttpHandler` to fake `POST /otel/api/query` responses — the same pattern `OtelStatus_*` tests already use. The `FakeOtelQueryExecutor` support file (`tests/Support/FakeOtelQueryExecutor.cs`) is deleted.

The Server-side `OtelQueryRoutesIntegrationSpecs.cs` gets a `columns` assertion added to `PostQuery_SelectCount_ReturnsQueryResultEnvelope` and the truncation specs.

## Risks / Trade-offs

- **[Server now required for query]** → `mo otel query` fails when Server is down. This is the intended behavior per `design/cli.md:118`; the error message points to `mo service start server`. Direct `sqlite3 otel.db` remains a developer path.
- **[`columns` addition changes the Server response shape]** → Additive only; existing tests that check `data.rows` / `data.truncated` are unaffected. No external consumer exists (project is in active development).
- **[`--db` removal is breaking]** → No version-compat constraint per AGENTS.md. The `--db` flag was a local-dev escape hatch; `sqlite3` covers the same need.
- **[Test churn]** → `FakeOtelQueryExecutor`-based tests are rewritten as HTTP-fake tests. Mechanical migration; coverage is equivalent or better because HTTP fakes exercise the full request-response path.

## Migration Plan

No data migration. No persistence-schema change.

1. Add `columns` to `QueryResult` and populate it in `TraceQuerier.ExecuteBoundedQuery` (all return paths).
2. Rewrite `OtelCommands.BuildQuery` / `RunQueryAsync` to POST through `api.SendAsync`, read the envelope, and render. Add `--json` option.
3. Remove the CLI SQLite executor stack, `--db`, path resolution, and the `queryExecutor` / `environment` parameters.
4. Rewrite CLI query tests with `RecordingHttpHandler`; delete `FakeOtelQueryExecutor`.
5. Update `docs/cli-reference.md` to remove the implementation-gap note about `otel query` reading local storage.

Rollback: `git revert`. No persistence or external-system impact.

## Open Questions

None. The Server query contract, CLI `--json` infrastructure, and error-envelope handling all exist today; this change wires them together and removes the bypass.
