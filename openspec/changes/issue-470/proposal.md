## Why

Mohist can report that its collector is online, but operators cannot see which request paths are multiplying work or whether observability itself is approaching failure. Mohist needs bounded, trustworthy diagnostics while the Server is still responsive, before CPU, memory, or disk pressure becomes an outage.

## What Changes

- Add low-cardinality runtime metrics for stable HTTP routes, database and downstream calls, process pressure, telemetry outcomes, and observability storage pressure. Project, issue, workflow, session, trace, span, and raw-URL identities are excluded from metric labels.
- Maintain a bounded, process-local five-minute diagnostic summary that ranks at most 10 stable routes by request amplification and latency. The summary resets on Server restart and is never persisted as business state.
- Expand the OTel status API and `mo otel status` to report exactly `off`, `healthy`, or `degraded`, with storage usage and budget, growth, received, saved, rejected and dropped telemetry, process pressure, recent route amplification, and the latest degradation reason.
- Report unavailable storage, failed writes, telemetry rejection or loss, and unavailable resource sampling explicitly instead of presenting fabricated zero values as healthy. Log degradation and recovery only when the state changes.
- Add per-request candidate, processed, transcript/database, and downstream-call counts to agent status and activity responses. The literal `/api/agent/status` and `/api/agent/activity` paths remain project-scoped compatibility surfaces and never aggregate all projects.
- Prevent OTel ingestion, query, status, and storage-maintenance work from feeding equivalent signals back into the built-in collector.
- Keep status cost and response size independent of telemetry history, and keep OTel degradation independent of core `/api/health` readiness.
- **BREAKING**: `/otel/api/status` and `mo otel status` replace exact historical `trace_count` and `span_count` reporting with bounded runtime outcome and pressure diagnostics.

## Capabilities

- `runtime-observability-metrics`: Standard low-cardinality runtime signals and a matching bounded local summary for request amplification, process pressure, telemetry outcomes, and storage pressure, including self-observation exclusion.
- `otel-runtime-status`: Tri-state OTel status through the API and CLI, with bounded storage, ingestion, process, route, and degradation diagnostics that remain independent of telemetry history and core health.
- `agent-path-amplification`: Project-scoped agent status and activity responses expose the work used to assemble each response without turning runtime diagnostics into Workflow or Session facts.

## Impact

- **Server observability** (`packages/server/src/Mohist.Server/Otel/`, `packages/server/src/Mohist.Server/Infrastructure/Hosting/`): adds the runtime metric catalog, bounded diagnostic state, resource and storage pressure reporting, outcome accounting, transition logging, and self-observation exclusion.
- **OTel status API** (`packages/server/src/Mohist.Server/Api/OtelQueryRoutes.cs`, `packages/server/src/Mohist.Server/Otel/TraceQuerier.cs`): replaces the current count-based `/otel/api/status` payload with the tri-state bounded status contract.
- **CLI** (`packages/cli/Mohist.Cli/MohistCliCommands.Otel.cs`): updates `mo otel status` for the new status contract; `mo otel query` is unchanged.
- **Agent read paths** (`packages/server/src/Mohist.Server/Api/AgentRoutes.cs` and AgentOps assemblers/queriers): add response-local amplification counts and project-scoped compatibility paths.
- **Health and persistence**: `/api/health` remains independent; diagnostic summaries remain in memory and require no business-database migration or external metrics service.
