## Why

Browsing the most recent traces is the highest-frequency observability action during a workflow or Agent incident, yet today the only way to do it is `mo otel query "<SQL>"`, which forces the operator to know the `otel.db` table and column names before they can see "what did this service just do, and how long did it take". That cognitive cost is disproportionate to a glance. The Server already owns a typed recent-traces list (`GET /otel/api/traces`); surfacing it as a dedicated `mo otel traces` action removes the schema tax for the common case while leaving `query` for complex exploration.

## What Changes

- Add a read-only `mo otel traces` subcommand that lists the most recent traces by submitting to the Server's `GET /otel/api/traces` capability, ordered most-recent first.
- Add `--service <name>` to filter by service and `--limit <n>` to control the row count; the Server's existing clamping (default 50, hard cap 1000) remains authoritative.
- Support the shared `--json` field-selection contract: bare `--json` lists the selectable fields and exits without contacting the Server; `--json <fields>` projects only those fields. Default output is a compact human-readable table.
- Make Server unavailability a non-zero exit with the actionable Server-unavailable diagnostic on stderr, consistent with `otel query`/`otel status`.
- Add leaf-level help that states the division of labor with `otel query` (typed browsing vs. free-SQL exploration).
- No aggregation, time-range filtering, single-trace detail, span-tree view, or any change to OTel collection and storage.

## Capabilities

- `otel-cli-traces`: The CLI's typed recent-traces list command — Server-routed read with service/limit filters, the shared `--json` field-selection contract (discovery, projection, rejection), compact table presentation, Server-unavailability diagnostics, and leaf help positioning it against `otel query`.

## Impact

- **CLI** (`packages/cli/Mohist.Cli/MohistCliCommands.Otel.cs`): add the `traces` subcommand, its `--service`/`--limit` options, a `Collection` `ResourceDescriptor` over the trace-summary fields (`trace_id`, `service_name`, `start_time`, `end_time`, `span_count`), and table rendering. CLI specs (`CliOtelCommandSpecs.cs`) and the canonical command-surface tests gain the new action.
- **Server API** (`GET /otel/api/traces`, `TraceQuerier.ListAsync`): consumed unchanged; no second query policy is introduced.
- **Documentation** (`docs/cli-reference.md`): remove the recorded `otel traces` implementation gap once the CLI ships the typed list.
- **Dependencies and persistence**: none — no new package, no schema or storage change; the command never opens local storage.
