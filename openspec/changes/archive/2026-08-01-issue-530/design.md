## Context

Issue #530 adds `mo otel traces`, a typed recent-traces browser. The motivation and capability contract are in [`proposal.md`](proposal.md) and [`specs/otel-cli-traces/spec.md`](specs/otel-cli-traces/spec.md).

Current state, verified in code:

- **Server capability already exists and is unchanged.** `GET /otel/api/traces?limit=&service=` (`OtelQueryRoutes.cs:50`) delegates to `TraceQuerier.ListAsync` (`TraceQuerier.cs:81`), returning `TraceSummary[]` with keys `trace_id`, `service_name`, `start_time`, `end_time`, `span_count`. Bounding lives entirely on the Server: `DefaultListLimit=50`, `MaxListLimit=1000`, and `ClampLimit` treats `<= 0` as "use default" and clamps the upper end (`TraceQuerier.cs:282`).
- **The `otel` CLI group has two subcommands** (`MohistCliCommands.Otel.cs`): `query` and `status`, both built on the older manual `api.SendAsync` + `HttpRequestException`/`TaskCanceledException` catch path with bespoke rendering.
- **Two read-helper families exist, and they differ on the Server-unavailable diagnostic — this decides the template for `traces`.** `GetDataOrPrintErrorAsync` (`MohistCliApi.cs:308`) wraps `SendAsync` and, on a connection failure, writes the literal `ServerUnavailableMessage` ("Server is not running. Start with: mo service start server"); this is the contract asserted across the CLI (`CliOtelCommandSpecs` for `query`/`status`, sessions, agents, and the `MohistCliApiSendAsyncSpecs` family) and the pattern `Run list` follows (`Run.Reads.cs:62`): fetch, then `selection.Project(data, …)` + `RenderTableAsync(data, TableShape.X)`. `PrintResourceAsync` (`MohistCliApi.cs:106`) instead routes failures through `CliResponseReader` → `CliFailure("server-unavailable", ex.Message)` (`CliExecutionContract.cs:271`), rendered as `<raw message> (code=server-unavailable)` with no remediation hint registered — the pattern `activity list` follows. Because the spec requires the standard message and the `otel` group already asserts it, `traces` MUST use the `GetDataOrPrintErrorAsync` family.
- **Tables are dispatched by `TableShape` enum** (`MohistCliApi.cs:1020`) to `TableRenderer.Render`, which calls a per-shape method (e.g. `RenderActivityList`, `TableRenderer.Events.cs:81`) using shared `AsArray`/`StringOf`/`WriteTable` helpers.

Constraint: the OTel store is **global, not project-partitioned** — `/otel/api/traces` carries no project segment. So `traces` needs no `--project`, unlike `activity list`.

## Goals / Non-Goals

**Goals:**

- Ship a read-only `mo otel traces` that lists recent traces through the Server, with `--service`, `--limit`, and `--json`, matching the spec.
- Reuse existing CLI read infrastructure (`GetDataOrPrintErrorAsync`, `ResourceDescriptor`, `JsonSelection`, `TableShape`) so JSON/table behavior is consistent with the rest of the CLI for free, and the Server-unavailable diagnostic matches the `otel` group.
- Keep the Server untouched — no second limit/filter policy, no schema change.

**Non-Goals:**

- Single-trace detail, span trees, aggregation, or time-range filtering (free SQL via `otel query` remains the escape hatch).
- Unifying the `otel` group's two code paths, or refactoring `query`/`status`.
- Any change to OTel collection or storage.

## Decisions

### D1. Build `traces` on the `GetDataOrPrintErrorAsync` path (the `Run list` pattern), not `PrintResourceAsync`

`traces` runs the local `--json` discovery check first (no Server contact), then fetches via `api.GetDataOrPrintErrorAsync(path)`. That helper emits the standard `ServerUnavailableMessage` on a connection failure — matching `otel query`/`status` and the CLI-wide asserted contract — and returns `(exit, data)`. On success it does `selection.Project(data, Cardinality)` + `WriteSuccessAsync` for selected JSON, else `api.RenderTableAsync(data, TableShape.OtelTracesList)`. This keeps the `--json`/table reuse while satisfying the spec's standard-message requirement.

- *Alternative considered:* `PrintResourceAsync` (the `activity list` pattern), which bundles GET + projection + rendering in one call. Rejected because its failure path renders `<raw exception> (code=server-unavailable)` via `CliResponseReader`/`CliResultWriter` (`CliExecutionContract.cs:271`, `:190`) — not the standard `ServerUnavailableMessage`, and no `server-unavailable` hint is registered to supply remediation. Using it would violate the spec and diverge from the `otel` group's existing commands.
- *Note:* `query`/`status` use the manual `SendAsync` path for their own reasons (bespoke truncation/inspection rendering) and are not migrated here; all three `otel` commands nonetheless converge on the same `ServerUnavailableMessage` diagnostic.

### D2. Forward `--limit` raw; do not clamp locally

Use a nullable `Option<int?>("--limit")` (default unset). When unset, omit the query parameter so the Server applies its `DefaultListLimit` (50). When set, forward the value verbatim — the Server's `ClampLimit` is authoritative (clamps to `[1, 1000]`, `<= 0` → default). This is a hard spec requirement ("the CLI MUST NOT impose its own limit policy") and keeps the bound in one place.

- *Alternative considered:* local `1..MaxLimit` validation like `activity list` (`Activity.cs:53`). Rejected — it duplicates `TraceQuerier.ClampLimit`, and the two can drift; the spec explicitly forbids a second policy. Non-integer input is still rejected locally by `System.CommandLine`'s `int?` parser as a usage error, so garbage never reaches the Server.

### D3. `--service` is a nullable string, URL-encoded, exact match

`Option<string?>("--service")`. When provided (non-blank), append `&service=<Uri.EscapeDataString(value)>`; when blank/unset, omit it. The Server filters by exact `service_name` equality (`TraceQuerier.cs:94`). Pattern/prefix matching is deliberately out of scope (free SQL covers it).

### D4. Query-string assembly handles `?` vs `&`

Build the path as `/otel/api/traces`, then append `?limit={limit}` and/or `&service={esc}` only for provided params. Keep the `?`/`&` logic explicit and small rather than introducing a query-builder helper — there are only two optional params and no precedent for a shared builder.

### D5. One capability, one `ResourceDescriptor` and one `TableShape`

- `ResourceDescriptor(ResourceCardinality.Collection, ["trace_id", "service_name", "start_time", "end_time", "span_count"])` — the exact `TraceSummary` keys, so `--json` discovery/projection/rejection reuse the shared `JsonSelection` contract.
- Add `TableShape.OtelTracesList` and a `RenderOtelTracesList` method modeled on `RenderActivityList`: `AsArray`, empty → a clear "No traces found" line, compact headers (e.g. `trace_id`/`service`/`start`/`end`/`spans`), `trace_id` truncated via the existing `Truncate` helper so long hex IDs don't blow out column widths.

### D6. Leaf help positions `traces` against `query`

The `traces` command description names `--service`/`--limit` and states the split: typed browsing of recent traces here, free-SQL exploration via `mo otel query`. This satisfies the AC and matches the existing `otel --help` wording style ("through the Server").

## Risks / Trade-offs

- **[Two code paths inside the `otel` group]** (manual `SendAsync` for `query`/`status`, `GetDataOrPrintErrorAsync` for `traces`) → *Mitigation:* both emit the standard `ServerUnavailableMessage` on connection failure, so the user-visible diagnostic is identical across the group. The `PrintResourceAsync`-based commands elsewhere (e.g. `activity list`) emit a different `code=server-unavailable` form; `traces` deliberately avoids that path to stay consistent with its `otel` siblings. If the CLI later wants one failure form everywhere, prefer adding a global `server-unavailable` hint so `PrintResourceAsync`-based commands gain the remediation guidance too — a cross-cutting change, out of scope here.
- **[CLI forwards absurd `--limit` (huge/negative) to the Server]** → *Mitigation:* Server clamps; cost is one cheap request. Preferred over mirroring bounds in the CLI where they can drift.
- **[Fixed table widths vs. long trace IDs]** → *Mitigation:* reuse `Truncate` on `trace_id` like Activity truncates its columns; full values are always available via `--json`.
- **[`--service` is exact-match only]** → *Mitigation:* documented in leaf help; pattern matching stays a `query` concern.
- **[No project scoping]** — the OTel store is global, so `traces` intentionally has no `--project`. Not a risk, but a deliberate divergence from most other list commands; worth noting to avoid a reviewer expecting project resolution.

## Migration Plan

- **Deploy:** pure additive CLI change — new subcommand, new enum entry, new renderer method. No Server, persistence, dependency, or config change; no feature flag. Ships in the next CLI build.
- **Docs:** remove the `otel traces` gap line in `docs/cli-reference.md` (currently at the "实装差距" section) once the command lands, and confirm `traces` is listed in the `otel` command map.
- **Rollback:** revert the CLI commit. The `GET /otel/api/traces` endpoint predates this change and is consumed by nothing else newly, so rollback is local to the CLI.

## Open Questions

- Should the output hint when the Server clamped a requested `--limit` below the user's value (e.g. requested 5000, got 1000)? Not required by the ACs and would add noise; defer unless operators ask.
- Should `--service` grow prefix/glob support? Out of scope; revisit if exact match proves too coarse for real incidents, and route through the Server rather than client-side filtering.
- Unifying the CLI's two Server-unavailable forms (standard message vs `code=server-unavailable`) — e.g. registering a global `server-unavailable` hint so `PrintResourceAsync`-based commands also gain remediation guidance — is a cross-cutting improvement, intentionally out of scope for this `otel traces` addition.
