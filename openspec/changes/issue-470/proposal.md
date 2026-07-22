## Why

Mohist can currently report that its collector is online and count stored traces, but it cannot show which request paths are multiplying database or downstream work, whether observability storage is approaching its budget, or whether telemetry is being rejected or lost. Operators need bounded, trustworthy signals while the Server is still responsive, rather than discovering the cause only after CPU, memory, or disk is exhausted.

## What Changes

- Add low-cardinality runtime metrics for stable HTTP routes, database and downstream calls, process resource pressure, telemetry receipt and persistence outcomes, and observability storage usage and growth; project, issue, workflow, session, and raw-URL identities are excluded from metric labels.
- Maintain a bounded five-minute in-memory diagnostic window at one-second resolution, with at most 10 anomalous stable-route summaries ranked by workload amplification and latency; the summary resets on Server restart and does not become business data.
- Expand the OTel status API and `mo otel status` to report exactly one of `off`, `healthy`, or `degraded`, together with storage usage and budget, growth, received, saved, rejected and dropped telemetry, the latest degradation reason, process pressure, and bounded route diagnostics.
- Report an unreadable OTel database, failed writes, active rejection, or data loss as `degraded` instead of presenting zero counts as healthy; emit one structured log on each transition into degradation and recovery, without repeating logs while the state is unchanged.
- Expose candidate, actually processed, transcript/database, and downstream-call counts on the agent status and activity read paths so polling and feed amplification can be diagnosed directly.
- Exclude OTel ingestion, query, status, and storage-maintenance work from export back into the same built-in collector, preventing self-observation feedback loops.
- Keep status memory and response size bounded and independent of telemetry history; status reads no longer perform full Trace or Span table counts, and observability degradation does not make the core `/api/health` readiness surface unavailable.
- **BREAKING**: `/otel/api/status` and the human-readable `mo otel status` output replace exact historical `trace_count` and `span_count` reporting with bounded runtime receipt, persistence, rejection, and loss counters; consumers relying on the old status shape must adopt the tri-state status contract.

## Capabilities

- `runtime-observability-metrics`: Standard `Meter` signals and matching bounded local summaries cover request volume and latency, database and downstream amplification, process pressure, telemetry outcomes, and storage pressure using only stable low-cardinality labels, while excluding the built-in observability pipeline from feeding itself.
- `otel-runtime-status`: The status API and `mo otel status` expose `off`, `healthy`, or `degraded` with bounded storage, ingestion, process, route, and degradation details; failures are explicit, transitions are logged once, status cost does not grow with history, and core health remains independent.
- `agent-path-amplification`: Agent status and activity reads expose candidate, processed, transcript/database, and downstream-call counts so repeated high-frequency paths reveal their amplification ratios without turning those runtime signals into workflow or session facts.

## Impact

- **Server observability** (`packages/server/src/Mohist.Server/Otel/`, `packages/server/src/Mohist.Server/Infrastructure/Hosting/`): adds metric emission, bounded in-memory diagnostic state, process and storage pressure snapshots, degradation transitions, and broader self-observation filtering. The existing trace-only OpenTelemetry registration gains metrics behavior without making observability part of domain decisions.
- **OTel status API** (`packages/server/src/Mohist.Server/Api/OtelQueryRoutes.cs`, `packages/server/src/Mohist.Server/Otel/TraceQuerier.cs`): changes the `/otel/api/status` response contract and removes history-sized `COUNT(*)` reads from status generation.
- **CLI** (`packages/cli/Mohist.Cli/MohistCliCommands.Otel.cs`): updates `mo otel status` rendering for the tri-state status and bounded diagnostics; `mo otel query` is unchanged.
- **Agent read paths** (`packages/server/src/Mohist.Server/Api/AgentRoutes.cs` and their status/activity assemblers and queriers): add amplification counts to the existing status and activity responses and instrument their database, transcript, and downstream work.
- **Health and logging** (`packages/server/src/Mohist.Server/Api/HealthRoutes.cs` and structured Server logs): health remains a core-service readiness signal, while observability state changes are reported separately and only on transitions.
- **Dependencies and persistence**: uses the existing .NET/OpenTelemetry stack and OTel storage metadata; no new external metrics service, dashboard, business-database history, or local-metric export into the built-in collector is introduced.
