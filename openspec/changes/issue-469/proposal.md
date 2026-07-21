## Why

`POST /otel/api/query` materializes the full SELECT result into memory and returns it in one shot, with no cap on request body size, row count, or serialized response bytes. The only configured bound — `CommandTimeout = 5s` — limits SQLite lock-wait, not running query work, so it cannot actually interrupt a long-running query. A single diagnostic query can therefore monopolize CPU, memory, and response bandwidth, and the caller has no signal when the result is silently capped or oversized.

## What Changes

- Reject oversized query request bodies before they are fully buffered, returning a structured `413` with a stable error code instead of allocating unbounded memory.
- Bound HTTP query responses to at most 1000 rows and at most 4 MiB serialized JSON; when either is reached, return a structured truncation indicator and reason rather than partial JSON or a silently empty result.
- Bound query execution by a mechanism that actually interrupts SQLite work — not `CommandTimeout` — so long queries and client cancellation stop execution and release the read-only connection, not only the HTTP wait.
- Preserve the existing physical read-only connection and the SELECT/WITH-only, single-statement validation; multi-statement and write attempts continue to be rejected.
- **No change** to `mo otel query`'s local read-only CLI path, which reads `otel.db` directly and is not subject to the HTTP response budgets.

## Capabilities

- `otel-http-query-admission`: The HTTP query endpoint admits only single SELECT/WITH statements on a read-only connection, refuses oversized request bodies before they are fully buffered with a structured `413` and stable error code, and continues to reject multi-statement and write attempts at both the keyword and SQLite engine layers.
- `otel-query-execution-budget`: Query execution is bounded by a mechanism that actually interrupts SQLite work; long queries and client cancellation stop execution and release the read-only connection rather than only ending the HTTP wait.
- `otel-http-query-response-bound`: HTTP query responses are bounded to at most 1000 rows and at most 4 MiB serialized JSON; when a limit is reached the response carries a structured truncation indicator and reason that the caller can act on, never partial JSON or a silently empty result.

## Impact

- **Server query API** (`packages/server/src/Mohist.Server/Api/OtelQueryRoutes.cs`, `packages/server/src/Mohist.Server/Telemetry/TraceQuerier.cs`): add request body limit, response row/byte budgets with a truncation indicator, and an execution-cancellation boundary that interrupts SQLite via cancellation rather than `CommandTimeout`; preserve read-only connection and `ValidateSelectOnly`.
- **Read-only connection factory** (`packages/server/src/Mohist.Server/Telemetry/OtelDb.cs`): contract unchanged; reused for the bounded execution path.
- **CLI** (`packages/cli/Mohist.Cli/MohistCliCommands.Otel.cs`): no behavioral change; `mo otel query` continues to read `otel.db` directly through its own read-only connection.
- **Web UI**: any consumer of `/otel/api/query` must surface truncation when the indicator is present; concrete placement decided in design.
- **Tests** (`packages/server/tests/Mohist.Server.SpecTests/Specs/Telemetry/`): extend coverage to large row counts, single large cell values, recursive CTE amplification, oversized request body, and cancellation-driven interruption; assertions use operation counts and explicit cancellation, not wall-clock timing.
- **Dependencies / persistence**: no new dependency, no schema change. The `/otel/api/query` response gains an optional truncation indicator; whether this is additive or a contract change for existing consumers is decided in design.
