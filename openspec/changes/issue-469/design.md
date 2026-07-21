## Context

`POST /otel/api/query` (`packages/server/src/Mohist.Server/Api/OtelQueryRoutes.cs`) is the diagnostic free-SQL entry into `otel.db`. Today it admits a SELECT/WITH statement (`TraceQuerier.ValidateSelectOnly`), opens a physically read-only connection (`OtelDb.OpenReadOnlyConnection`), and materializes the entire result into `IReadOnlyList<Dictionary<string,object?>>` via `SqliteDataReader.ReadAsync` before returning it through `ApiResponse<T>`.

Three structural gaps drive this change (see proposal + specs):

- **No request body cap.** The handler does `StreamReader.ReadToEndAsync` into a `string` with only Kestrel's default 30 MB ceiling — unbounded for a SQL body.
- **No row / response-byte cap.** The whole result set is buffered; a wide or deep SELECT allocates without limit and the caller cannot tell a full result from a silently-capped one.
- **`CommandTimeout` cannot interrupt a running query.** Inspection of `Microsoft.Data.Sqlite` 11 confirms: `SqliteCommand.Cancel()` is an explicit no-op; `CommandTimeout` is consulted only inside the `while (IsBusy(sqlite3_step(...)))` loop, i.e. only on `SQLITE_BUSY` lock contention. A long-running aggregate or recursive CTE that is actively computing never returns `SQLITE_BUSY`, so `CommandTimeout` never fires. The cancellation token passed to `ExecuteReaderAsync`/`ReadAsync` is checked at method entry only — `ReadAsync` just wraps the synchronous `Read()`.

The only mechanism SQLite provides to stop a running query is `sqlite3_interrupt(db)`, checked at safe points during `sqlite3_step`. In this version `SqliteConnection.Handle` is `public virtual sqlite3?`, so we can call it directly through `SQLitePCL.raw` without reflection or a parallel connection stack.

Constraints carried forward:

- `mo otel query` (`packages/cli/.../MohistCliCommands.Otel.cs`) reads `otel.db` via its own read-only `SqliteOtelQueryExecutor` and must stay unaffected.
- The codebase bans `System.TimeProvider.System` and `new CancellationTokenSource(TimeSpan)`; all time must go through an injected `TimeProvider` (faked by `FakeTimeProvider` in tests). See `BannedApiAnalyzerTests`.
- Tests must not assert on wall-clock (`design/testing.md`).

## Goals / Non-Goals

**Goals:**

- Cap the request body before buffering, the response to ≤ 1000 rows and ≤ 4 MiB serialized JSON, and surface a structured truncation reason when either is hit.
- Make execution-budget exhaustion and client cancellation actually interrupt the running `sqlite3_step` and release the read-only connection — not merely end the HTTP wait.
- Keep all interruption behavior verifiable through injected time and explicit cancellation, with no wall-clock assertions.
- Preserve the existing read-only connection and SELECT/WITH-only admission (multi-statement and write attempts still rejected).

**Non-Goals:**

- No general SQL workbench, query queue, or workload management.
- No writes to `otel.db`; read-only invariant stays.
- No change to `mo otel query`'s local read-only path.
- No streaming/chunked transfer protocol; responses stay single-shot JSON, just bounded.

## Decisions

### Decision 1: Interrupt execution via `sqlite3_interrupt` on the public `SqliteConnection.Handle`

The bounded query path registers a cancellation callback on the execution token that calls `raw.sqlite3_interrupt(connection.Handle)` (guarding `null`). Because the interrupt is registered on the linked token, both client disconnect and execution-budget exhaustion flow through the same mechanism and actually break into `sqlite3_step`. The reader then throws `SqliteException` (`SQLITE_INTERRUPT`) or `OperationCanceledException`, and the existing `await using` on the connection/command/reader releases the connection immediately.

`raw.sqlite3_interrupt` is reached via `using static SQLitePCL.raw;`. `SQLitePCLRaw.core` is already a transitive dependency of `Microsoft.Data.Sqlite`, so its types normally flow to consumers; the build must confirm `SQLitePCL.raw` resolves at compile time and, if it does not, add a direct `<PackageReference Include="SQLitePCLRaw.core" />` using the version pinned in `Directory.Packages.props`. `SqliteConnection.Handle` is `public virtual sqlite3?` in the pinned `Microsoft.Data.Sqlite` 11 preview, so no reflection is involved; the build should assert this with a guard test (a trivial unit test that reads `connection.Handle` on an open in-memory connection) so a future library update that changes visibility fails loudly instead of silently disabling interruption.

`CommandTimeout` is retained at its current 5 s purely as defense-in-depth for `SQLITE_BUSY` lock-wait; it is not relied on as the long-query bound (see `otel-query-execution-budget` spec). The doc comments on `ExecuteRawQuery` are updated to describe the new four-layer safety net (admission, read-only connection, execution-budget interrupt, row/byte response budgets) and to stop citing the removed `design.md` Decision 5/8.

**Alternatives considered:**

- *SQLitePCLRaw-direct executor (bypass `Microsoft.Data.Sqlite` for the free-SQL path):* would own the raw handle and `sqlite3_step` loop natively. Rejected — it re-implements prepare/step/column extraction (~150+ lines), breaks the shared `InMemoryOtelDb` test fixture (which keys off the Microsoft.Data.Sqlite connection string), and duplicates the read-only open path. Now that `Handle` is public, it buys nothing.
- *Reflection on `SqliteConnection.Handle`:* unnecessary now that the property is public; would add fragility for no benefit.
- *Accept `ReadAsync(ct)` entry-check as "interruption":* rejected — it only stops between rows and cannot interrupt a single long `sqlite3_step` (huge aggregate, recursive CTE before first yield), which the spec explicitly requires.

### Decision 2: Execution budget through an injected `TimeProvider`, not `CommandTimeout`

The budget is a `CancellationTokenSource` created via `timeProvider.CreateCancellationTokenSource(budget)`, linked (`CreateLinkedTokenSource`) with the request-aborted token from the route handler. The linked token is what gets passed to the reader and what is registered for `sqlite3_interrupt`. `TimeProvider` is injected into `TraceQuerier` (and resolved through DI as `FakeTimeProvider` in spec tests), satisfying the banned-API rules and letting tests drive budget exhaustion by advancing the fake clock rather than sleeping.

The four OTel-query budgets (request body size, row count, byte size, execution budget) are `public const` static fields on `TraceQuerier`, mirroring the existing `TraceQuerier.MaxListLimit` pattern. They are deliberately NOT `OtelOptions` properties: `OtelOptions` is an `IOptions` class bound from `Mohist:Otel` config (`services.Configure<OtelOptions>(…)`), so any property there is config-overridable, which would let `MOHIST__Otel__*` weaken the body/row/byte/execution ceilings — an unacceptable regression vector for a safety bound on a high-risk issue. The proposed defaults are body size 64 KiB, row count 1000, byte size 4 MiB, execution budget 10 s (see Open Questions). `OtelOptions` is not extended by this change.

**Alternatives considered:**

- *`cts.CancelAfter(TimeSpan)`:* uses the system timer (not faking-friendly) and skirts the spirit of the banned `new CancellationTokenSource(TimeSpan)`; rejected for testability.
- *Reuse `CommandTimeout` as the budget:* explicitly forbidden by the issue and specs — it cannot interrupt running work.
- *Budgets as `OtelOptions` properties marked "not tunable":* rejected — `IOptions` properties are inherently config-bound, so the safety bound could be weakened by misconfiguration; `public const` is the only placement that cannot be overridden.

### Decision 3: Reject oversized request bodies before buffering via `Content-Length` + per-route request size limit

Admission checks `Request.ContentLength` against the body-size constant first and returns `413` (`query_request_too_large`) before reading the body. The route also carries a per-route Kestrel request-size limit (so a lying or absent `Content-Length` is still capped mid-stream rather than buffered to 30 MB). The body is then read as today and parsed. The constant lives alongside the other query budgets (propose 64 KiB — orders of magnitude above any realistic SELECT text, tight enough to bound memory).

The structured `413` is returned through the standard `ApiResponse` envelope via a new `ApiResults.PayloadTooLarge` (or equivalent) helper so the response shape matches the other admission errors.

**Alternatives considered:**

- *Global Kestrel `MaxRequestBodySize`:* affects every endpoint; rejected as too broad.
- *Read-then-measure:* violates "before full buffering"; rejected.

### Decision 4: Response budgets applied during the read loop, with a `QueryResult` wrapper carrying truncation

`ExecuteRawQuery` gains an overload (or a new `ExecuteBoundedQuery`) that returns a `QueryResult`:

```
QueryResult {
  Rows: IReadOnlyList<Dictionary<string,object?>>,
  Truncated: bool,
  TruncateReason: string?   // "row_limit" | "byte_limit" | null
}
```

The read loop stops when:

- `Rows.Count == 1000` with more rows remaining → `Truncated`, reason `row_limit`; or
- emitting the next row would push the serialized payload past 4 MiB → `Truncated`, reason `byte_limit`.

Per-row serialized size is accounted as the loop runs (UTF-8 byte count of string cells + fixed size for numbers/bytes/nulls), and for potentially-oversized single cells the column byte length is peeked (`SqliteDataReader.GetBytes`) before the value is materialized, so a `SELECT repeat('x', …)` cannot force an oversized allocation. The endpoint serializes `ApiResponse<QueryResult>`; the Web UI reads `data.rows`, `data.truncated`, `data.truncate_reason`.

This is a **contract change** for `/otel/api/query`: `data` moves from a bare array to `{ rows, truncated, truncate_reason }`. Consumers are the diagnostic UI and AI only (no external integrations), and the project is in active development with no version-compat constraint, so the evolution is acceptable. The existing `OtelQueryRoutesIntegrationSpecs` assertions on `data[0].total` move to `data.rows[0].total`.

**Alternatives considered:**

- *Header-only indicator (`X-Truncated`):* easy to miss and the spec requires a structured, programmatically-read reason; rejected as the sole channel.
- *Reuse `ApiResponse.Details` for truncation:* `Details` is currently error-only; overloading it for success metadata muddies the envelope contract across all endpoints.
- *Stream JSON via `Utf8JsonWriter` into the response body with live byte counting:* fully avoids materialization but couples the envelope writer to streaming and complicates the row-cap interaction; rejected for this size class (1000 rows × typical cells is small; only pathological single cells need the peek).
- *Keep `data` as array and add a sibling field:* would require modifying the shared `ApiResponse<T>` record, polluting every endpoint.

### Decision 5: CLI path stays structurally isolated

`mo otel query` continues to use `SqliteOtelQueryExecutor` against a directly-opened read-only `otel.db`. The row/byte/body budgets and the `QueryResult` wrapper are server-HTTP-only. No code is shared by force; the only shared contract remains the `otel.db` DDL column constants in `OtelDb.cs`.

### Decision 6: Deterministic interruption testing via a server-side executor seam

The execution-budget and client-cancel requirements must be verifiable without wall-clock (`design/testing.md`) and without depending on a real slow query. Real `Microsoft.Data.Sqlite` reads against the in-memory fixture complete before any fake-clock advance, so there is no in-flight reader to interrupt unless the test can hold execution at a controlled point. The plan therefore introduces a server-side `IOtelQueryExecutor` seam for the free-SQL path, mirroring the CLI's existing `IOtelQueryExecutor` in `MohistCliCommands.Otel.cs`: the `/query` route depends on `IOtelQueryExecutor.Execute(sql, linkedCt) → QueryResult`; `TraceQuerier` is the registered production implementation and owns the read-only connection, the `sqlite3_interrupt` registration on the linked token, and the bounded read loop; the spec test fixture (`OtlpRoutesWebApplicationFactory`) registers a `FakeOtelQueryExecutor` that blocks on a `TaskCompletionSource` until its cancellation token fires.

This yields two complementary, deterministic verification paths:

- *Route-handler contract (fake executor):* the fake blocks until cancelled, so the test advances `FakeTimeProvider` past the budget (or cancels the request-aborted token) and asserts the endpoint returns `query_execution_budget_exhausted` and never a partial row array — no real SQLite, no wall-clock.
- *Real interruption wiring (production executor vs. in-memory SQLite):* seed a large bounded row set via a recursive CTE (`WITH RECURSIVE …`), run the production executor under a `FakeTimeProvider` budget, advance the fake clock once the task is in-flight, and assert the reader terminated early (did not consume all rows; observed `SQLITE_INTERRUPT`/`OperationCanceledException`) and the connection was disposed. The assertion is on the outcome (loop terminated early, connection released), never on elapsed duration; no `Stopwatch`, no `Thread.Sleep`. The interrupt is checked per `sqlite3_step`, and the large bounded row set gives a wide deterministic window in which advancing the fake clock fires `sqlite3_interrupt` before the query could finish.

**Alternatives considered:**

- *Recursive CTE + wall-clock:* rejected — depending on real elapsed time to keep the query in-flight is exactly the banned flaky pattern (`design/testing.md`).
- *No seam, assert only on the route mapping with an already-cancelled token:* rejected — it never exercises the real `sqlite3_interrupt` registration and would not catch a wiring regression such as `Handle` going null or the callback being registered against the wrong token.

## Risks / Trade-offs

- **[Risk] `sqlite3_interrupt` is best-effort — SQLite checks it at safe internal points, so an already-in-flight atomic step may still complete briefly** → Mitigation: for multi-row producers the row/byte budgets bound the work regardless; for the genuine single-step-long case the interrupt is the only mechanism and is "as prompt as SQLite allows". Document this in code comments and the spec scenario stays about "interrupts the running reader / releases the connection", which the `await using` disposal guarantees.
- **[Risk] Response contract change breaks `/otel/api/query` consumers** → Mitigation: only diagnostic consumers exist; update Web UI caller and integration specs in the same change. No external API consumers.
- **[Risk] Per-row byte estimation under/over-counts vs. actual JSON** → Mitigation: peek actual column bytes for cells before materializing; stop conservatively (pre-check the next row's estimated addition against the budget rather than recovering after exceeding it). The 4 MiB ceiling is checked against accumulated bytes, never trustingly.
- **[Risk] `connection.Handle` is `null` in an unexpected connection state** → Mitigation: null-guard the interrupt registration; the linked-token cancellation still flows to `ReadAsync`'s entry check and to disposal as a fallback.
- **[Risk] In-memory test DB makes read-only mode a no-op (`InMemoryOtelDb`)** → Mitigation: pre-existing limitation, unrelated to this change; the read-only-engine backstop stays covered by `PostQuery_InsertBypassingKeywordCheck_RejectedByReadOnlyMode` against the file-backed path, and the keyword-layer admission is covered in-memory. Interruption is exercised deterministically through the `IOtelQueryExecutor` seam (Decision 6): the route-level contract uses the fake executor (no real SQLite needed), and the real `sqlite3_interrupt` wiring runs against the in-memory connection where `Handle` is non-null. The `Handle`-visibility guard test (Decision 1) catches the case where a library update silently disables interruption.
- **[Trade-off] Two response shapes (`/traces`, `/status` stay array; `/query` becomes `QueryResult`)** → accepted; the other two endpoints are already bounded and have no truncation semantics.

## Migration Plan

Server-only change; no database schema change, no runner/web contract migration.

1. Add the four OTel-query budget `public const` fields to `TraceQuerier` (mirroring `MaxListLimit`): body size (~64 KiB), row count (1000), byte size (4 MiB), execution budget (~10 s). Do NOT add these as `OtelOptions` properties. Inject `TimeProvider` into `TraceQuerier` (the DI registration pattern is already in place elsewhere).
2. Implement interruption + budgets in `TraceQuerier.ExecuteBoundedQuery` and wire `TimeProvider` through DI. Introduce the server-side `IOtelQueryExecutor` seam and register `TraceQuerier` as its production implementation.
3. Update `OtelQueryRoutes` `/query` handler: `Content-Length` pre-check + per-route size limit → `413`; on admission, build the linked token and call `IOtelQueryExecutor.Execute`; map budget exhaustion to `query_execution_budget_exhausted`. Client disconnect produces no response (the request channel is gone), so no `query_cancelled` body is returned to the disconnecting caller — the linked-token cancellation still flows to `sqlite3_interrupt` and releases the connection.
4. `/otel/api/query` has no current Web UI or CLI HTTP consumer, so no consumer updates are required; the response-shape change touches only integration tests.
5. Extend `OtelQueryRoutesIntegrationSpecs` and `TraceQuerierSpecs`: oversized body → 413; > 1000 rows → truncation; large single cell and many-moderate-rows → byte truncation; recursive CTE bounded; client-cancel and budget-exhaustion interrupt the reader and release the connection via the `FakeOtelQueryExecutor` seam and `FakeTimeProvider` — never wall-clock; plus a guard test asserting `SqliteConnection.Handle` is non-null on an open in-memory connection.

**Rollback:** single-commit revert; no persisted state. If a config gate is desired for staged rollout, `OtelOptions.Enabled` already gates the whole subsystem and can be used as an emergency switch.

## Open Questions

- **Execution budget default value.** Decision 2 fixes the placement as a `public const` on `TraceQuerier`; the proposed default is 10 s (2× the legacy 5 s `CommandTimeout`, comfortable for legitimate aggregates, tight enough to protect the process). Confirm against representative OTel queries during build.
- **Exact body-size constant.** Propose 64 KiB; confirm nothing in current diagnostic usage approaches it.
- **Truncation-reason string taxonomy.** Spec names `row_limit` / `byte_limit`; confirm the wire string stays the stable machine code (localization happens in any future UI consumer; none exists today).
