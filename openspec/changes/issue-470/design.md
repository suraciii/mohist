## Context

The proposal and the three capability specs define a low-cost operational surface for runtime amplification and OTel degradation. Today the Server has a trace-only outbound OpenTelemetry pipeline in `Infrastructure/Hosting/MohistOpenTelemetryRegistration.cs`, a built-in OTLP receiver and SQLite store in `Otel/`, and `GET /otel/api/status` implemented by `TraceQuerier.GetStatusAsync`. That status performs exact `COUNT(*)` queries over both Trace and Span tables, catches every database error, and substitutes zero counts. `mo otel status` renders only collector online/offline, file size, Trace count and Span count.

The existing agent reads are project-scoped. `GET /api/projects/{projectRef}/agent/status` calls `RunnerStatusService` and `WorkflowActivityQuerier`; the latter currently loads every project Session before filtering active work. `GET /api/projects/{projectRef}/agent/activity` loads bounded Session candidates but also reads transcript parts and calls workflow status once per distinct workflow. Issues #467 and #468 deliberately depend on this change: they will reduce those costs while preserving the counters established here. Issue #470 explicitly names the currently removed unscoped paths, so this change restores them only as project-selector compatibility aliases, never as global aggregations.

Issue #470 is also the instrumentation prerequisite for #471 (bounded OTLP admission and writes) and #437 (72-hour / 1-GiB retention). This change owns the counters, status model and reporting seams. Those follow-ups own admission, batching, deletion and storage protection policy and will publish their outcomes through the seams defined here.

Constraints:

- Metrics are operational signals, not Workflow, Session or issue facts. They cannot influence scheduling or business outcomes.
- The local status path must work without an external metrics service and cannot query history-sized data.
- `Mohist:Otel:Enabled` remains disabled by default until #472 completes the full resource-budget gate. Disabled stops collector/export/storage work and Meter measurements, but not the bounded process sampler needed by the status contract.
- Tests use `TimeProvider`, in-memory stores and operation counts; they do not wait five real minutes or access the host filesystem.
- OTel ingestion, query, status and maintenance must not observe and export themselves back into the same built-in collector.

Stakeholders are operators using `mo otel status`, API clients reading runtime status and agent activity, and follow-up implementations that need one stable place to report accepted, saved, rejected, dropped and storage-protection outcomes.

## Goals / Non-Goals

**Goals:**

- Emit a fixed, low-cardinality `System.Diagnostics.Metrics.Meter` catalog and maintain a bounded in-process snapshot from the same observations.
- Attribute stable HTTP routes with request count, latency, database calls and downstream calls without requiring full Trace reconstruction.
- Report `off`, `healthy` or `degraded` with explicit storage, ingestion, process and route diagnostics at fixed read cost.
- Make ingestion and storage failures visible and log only actual healthy/degraded transitions.
- Add per-response amplification counts to the two project-scoped agent reads without changing their existing product payload semantics.
- Provide narrow publication seams that #437, #467, #468 and #471 can reuse without depending on the status DTO or CLI renderer.

**Non-Goals:**

- Implement #437 retention, #471 OTLP request/write limits, #467 status-query optimization or #468 persisted activity summaries in this change.
- Enable OTel by default, add Prometheus/Grafana, add a metrics Web page or persist route history.
- Export Mohist's local metrics to the built-in OTLP receiver. The current outbound endpoint is trace-oriented and defaults to that same process.
- Replace Trace or structured logs with metrics, instrument arbitrary user code or guarantee that observability data is never lost.
- Add any global/all-project agent aggregation or change the five-second Activity-page polling policy. The two explicit unscoped compatibility aliases are in scope only with a required single-project selector.

## Decisions

### D1. One singleton owns observations and status state

Add a singleton `RuntimeObservability` in `Mohist.Server.Otel`. It owns the `Meter`, cumulative process-lifetime ingestion counters, the bounded route aggregator, the latest sampled process/storage values and degradation-source state. Its public methods are narrow operational facts rather than status DTO setters:

- `CompleteRequest(route, method, statusCode, duration, databaseCalls, downstreamCalls)`
- `RecordAgentPath(path, candidates, processed, transcriptRecords)`
- `RecordIngest(IngestOutcome outcome)`
- `PublishStorage(sample)` and `SetDegradation(source, reason?)`
- `GetSnapshot()`

All mutation is thread-safe. `GetSnapshot` copies immutable values under the state lock and performs no database, filesystem, HTTP or grain call. The status route maps this internal snapshot to its wire DTO; the CLI knows only the wire DTO. Follow-up issues call the fact-oriented methods and do not mutate counters or DTO fields directly.

`IngestOutcome` contains non-negative `Received`, `Saved`, `Rejected` and `Dropped` Span-attempt counts plus an optional bounded reason code. It is produced once at the ingest boundary, including on a typed write failure, so callers cannot independently publish contradictory increments. `Received` counts parsed Span attempts; duplicate upserts count as saved; rejected means an admitted Span intentionally refused with a non-retryable OTLP partial-success response; dropped means malformed data or another loss for which the response will not request a retry. A rolled-back write that returns a retryable failure increments received and activates `storage_write`, but does not increment dropped, so the four counters intentionally need not satisfy a conservation equation. Issue #470 implements and tests `RecordIngest` as the production publication/read contract; it does not invent a rejection policy. #437/#471 are the first production publishers of non-zero `Rejected`, and their tests must drive that value through this same contract into status.

`RuntimeObservability` is registered even when OTel is disabled so `/otel/api/status` can return `off`. Process-resource sampling is always active because the issue requires current CPU, working set and GC heap pressure in every state. Meter measurements, cross-request route aggregation, ingestion, export and storage probing are activated only when `Mohist:Otel:Enabled` is true. The short-lived request work scope still activates for the two canonical project-scoped agent reads while OTel is off so their response-local counters remain truthful; those observations are discarded after response assembly and never reach the Meter or route ring in the off state. The state is process-local and is never added to `MohistDbContext` or Orleans storage.

Alternative considered: let `TraceQuerier` assemble status by querying each subsystem on every request. Rejected because it repeats the current history-dependent design, makes partial failures look like missing data and couples a cheap read to storage availability.

Alternative considered: separate singleton counters, route summary, degradation tracker and status assembler behind several interfaces. Rejected because they share one consistency boundary and no independent consumer needs those mutation models; splitting them would scatter the definition of a status snapshot.

### D2. Lock one explicit Meter catalog and do not add a local metrics exporter

Create a single `Meter` named `Mohist.Server.Runtime`. Synchronous request and ingestion instruments are updated by the same methods that update local state. Observable gauges read only the latest cached sampler values; callbacks never touch `Process`, SQLite or the filesystem. When OTel is off, all observable callbacks return no measurements even though the process sampler continues refreshing the status-only cache. This follows the OpenTelemetry .NET observable-instrument model, where each collection callback publishes the current attribute sets and stale sets disappear when no longer returned.

The initial catalog is:

| Instrument | Kind / unit | Attributes |
|---|---|---|
| `mohist.server.http.request.count` | Counter / `{request}` | `http.route`, `http.request.method`, `http.response.status_code` |
| `mohist.server.http.request.duration` | Histogram / `ms` | same HTTP attributes |
| `mohist.server.http.request.database_calls` | Histogram / `{call}` | same HTTP attributes |
| `mohist.server.http.request.downstream_calls` | Histogram / `{call}` | same HTTP attributes |
| `mohist.server.path.candidates` | Histogram / `{item}` | `mohist.path` |
| `mohist.server.path.processed` | Histogram / `{item}` | `mohist.path` |
| `mohist.server.path.transcript_records` | Histogram / `{record}` | `mohist.path` |
| `mohist.otel.spans.received` | Counter / `{span}` | none |
| `mohist.otel.spans.saved` | Counter / `{span}` | none |
| `mohist.otel.spans.rejected` | Counter / `{span}` | none |
| `mohist.otel.spans.dropped` | Counter / `{span}` | none |
| `mohist.otel.storage.usage` | ObservableGauge / `By` | none |
| `mohist.otel.storage.budget` | ObservableGauge / `By` | none |
| `mohist.otel.storage.growth` | ObservableGauge / `By/s` | none |
| `mohist.process.cpu.utilization` | ObservableGauge / `1` | none |
| `mohist.process.memory.working_set` | ObservableGauge / `By` | none |
| `mohist.process.runtime.dotnet.gc.heap` | ObservableGauge / `By` | none |

`http.route` is the matched route template, never a concrete URL. Request methods normalize to `GET`, `POST`, `PUT`, `PATCH`, `DELETE`, `HEAD`, `OPTIONS` or `OTHER`; response codes outside `100..599` normalize to `0`. Unmatched requests use `unmatched`; path-level metrics use only `agent.status` or `agent.activity`. Project, issue, workflow, session, Trace, Span and raw URL values are forbidden. A `MeterListener` unit test locks the exact instrument names, kinds, units, attribute keys and allowed bounded values.

No `WithMetrics(...AddOtlpExporter...)` registration is added. A standard in-process `MeterListener` can consume the catalog, while status uses the local state. Exporting to an external metrics backend needs a future endpoint/configuration contract distinct from the current trace endpoint. This prevents the default `http://localhost:4318/otel` endpoint from sending local metrics into its own receiver.

Alternative considered: rely only on ASP.NET Core, EF Core and HttpClient OTel metrics. Rejected because they do not provide the shared local snapshot or agent candidate/transcript counts, and their evolving labels are not Mohist's locked contract.

Alternative considered: configure the existing OTLP exporter for metrics. Rejected because the configured endpoint defaults to the built-in receiver, which does not accept metrics and must not receive self-observation; endpoint-role detection based on loopback URLs would be brittle.

### D3. Count request work through one ambient operational scope

Add `RuntimeRequestMetricsMiddleware` after routing and before endpoint execution. It creates an internal `RequestWorkScope` in `AsyncLocal` when either (a) OTel is enabled and the matched endpoint is not under `/otel/v1` or `/otel/api`, or (b) the endpoint is a canonical or compatibility agent status/activity route. It records a monotonic start timestamp through `TimeProvider` and always clears the scope in `finally`. The scope exposes an immutable `Snapshot()` during endpoint execution so an agent handler can build its response after all awaited work completes. On completion, the middleware resolves `RouteEndpoint.RoutePattern.RawText`, method and response status and calls `RuntimeObservability.CompleteRequest` exactly once only when OTel is enabled; off-state agent scopes are discarded.

Three narrow adapters increment the active scope and otherwise no-op:

- an EF Core `DbCommandInterceptor` increments `databaseCalls` once for each command execution against the business `MohistDbContext`;
- an Orleans outgoing grain-call filter increments `downstreamCalls` once per caller-side grain invocation begun while the HTTP execution context is active;
- an Orleans incoming grain-call filter saves and clears any ambient `RequestWorkScope` for the full grain turn, then restores it in `finally`, making the caller-side boundary explicit even in the co-hosted silo;
- an `IHttpMessageHandlerBuilderFilter` inserts the counting handler immediately inside the primary handler and increments `downstreamCalls` once per physical factory-created HTTP send, including each retry attempt.

The scope uses atomic integer increments so parallel fan-out inside one request is safe. `Snapshot()` copies the current counters without closing the scope; later awaited calls can still increment until the handler takes its final snapshot. It carries no project or domain identity and cannot affect call results. "Downstream" is intentionally caller-side, not transitive: the incoming Orleans filter ensures grain-to-grain calls and grain-side database work are not attributed back to the originating HTTP request. Direct `otel.db` SQLite operations do not pass through the EF interceptor. OTel routes do not create a scope, so their query, ingest and status work is excluded. Storage sampling/maintenance runs outside HTTP scope and therefore cannot recursively contribute route work.

Alternative considered: derive database and downstream counts by reading completed child Spans. Rejected because Activity creation depends on tracing configuration and sampling, status would lag request completion, and attaching a second Activity listener risks double-counting existing instrumentation.

Alternative considered: manually increment every route and service call. Rejected for general request metrics because new database/HTTP/grain calls would silently escape accounting. Explicit candidate/transcript counts remain at the agent services because no infrastructure observer can infer their domain-neutral work units.

### D4. Keep a one-second-resolution five-minute ring over a capped route set

The route aggregator uses 301 rotating one-second buckets driven by injected `TimeProvider`. Each bucket stores its epoch second plus request count, total and maximum duration, total database calls and total downstream calls per stable route. A bucket accepts at most 256 route templates; additional templates fold into one `other` entry. `unmatched` is already one bounded route identity. Snapshot includes buckets whose timestamp intersects `[now - 5 minutes, now]`; the boundary bucket gives the window at most one second of over-retention and never drops a request before it is five minutes old. Rotation clears expired buckets rather than retaining individual requests.

`GetSnapshot` merges the 301 buckets and computes average duration and per-request call ratios. Every observed stable route qualifies; "anomalous" means the threshold-free highest-ranked diagnostic view. Define `amplification = databaseCallsPerRequest + downstreamCallsPerRequest`, with one call of either kind weighted equally. Entries sort by amplification descending, average duration descending, then route name ordinal ascending (`other` participates by its literal name). It returns the first 10 entries with:

- `route`
- `request_count`
- `average_duration_ms`
- `max_duration_ms`
- `database_calls_per_request`
- `downstream_calls_per_request`

The maximum retained aggregate count is fixed (`301 * 257`), response count is fixed at 10 and Server restart starts with empty buckets. Tests advance `FakeTimeProvider` across second and five-minute boundaries and assert expiration, the one-second boundary tolerance and memory bounds.

Alternative considered: retain every request for an exact sliding five-minute window. Rejected because memory would grow with traffic.

Alternative considered: persist minute aggregates in SQLite. Rejected because the issue explicitly defines restart-local diagnostics and persistence would make observation a new storage workload and feedback source.

### D5. Sample process values in every state and storage only when enabled

One `OtelDiagnosticsSampler` hosted service samples process resources immediately in every configured state and then every 10 seconds using injected `TimeProvider`. When OTel is enabled, the same serialized iteration also samples storage immediately and on those ticks. It replaces the one-shot `OtelStatusInitializer` registration delivered with the baseline status; the two hosted services never coexist. The sampler's enabled-state immediate storage call is the sole startup storage probe after replacement. It runs one serial loop rather than timer callbacks, so storage samples cannot overlap; ticks that arrive while a storage sample runs coalesce into the next loop iteration. Shutdown prevents another sample and awaits the current fixed-operation synchronous storage probe rather than abandoning it. The guarantee is bounded operation count, not a wall-clock bound over host filesystem syscalls. It depends on two replaceable readers:

- `IProcessResourceReader` returns process total CPU time, working set and `GC.GetGCMemoryInfo().HeapSizeBytes`;
- `IOtelStorageProbe` performs a fixed-operation readiness probe and returns the combined size of `otel.db`, `otel.db-wal` and `otel.db-shm` plus the fixed 1-GiB budget. Its production implementation composes `IOtelReadinessConnectionFactory` (production forwards to `OtelDb`; tests use named shared-memory SQLite or a throwing fake) and the existing fakeable `IFileSystem` for metadata lengths.

CPU utilization is the delta in process CPU time divided by elapsed injected time and processor count, clamped to `[0,1]`. The immediate process sample makes working set and GC heap available in every state; CPU utilization is `null` until the second valid process sample establishes a delta. Storage growth is the byte delta between the oldest and newest enabled-state samples in a fixed 60-second, seven-slot ring divided by sample time. Until two valid samples exist, growth is `null`, not false zero. Observable gauges omit every value while off; status still reads the process cache directly. Storage fields are `null` while off because no storage metadata call runs.

The production storage probe first opens one dedicated SQLite connection from `IOtelReadinessConnectionFactory`, configured with a one-second busy timeout, initializes a missing new database with the existing fixed schema commands when necessary, and executes one `PRAGMA schema_version` metadata read. Unlike a constant expression, this touches the database header and read-lock path without scanning application rows. It then asks `IFileSystem` for exactly three metadata lengths (database, WAL, SHM), so the returned usage includes files created by initialization. It never uses `Task.Run`, abandons a worker, counts rows, or enumerates Trace/Span history. The engine busy timeout bounds SQLite lock wait; arbitrary filesystem syscalls are neither claimed cancellable nor assigned a wall-clock deadline. Read/probe failure invalidates the cached storage sample and activates a sticky `storage_read` degradation; a later successful probe clears that source. Write paths report `storage_write` failure and clear it only after a later successful write. #437 will replace/extend the same probe with its persisted watermarks and publish protection state through `SetDegradation`; it does not replace status assembly. The production SQL adapter is tested against named shared-memory SQLite; corrupt/locked/open failures use the connection-factory fake, and metadata operation order/count uses the repository `FakeFileSystem`, so no test touches host storage.

Alternative considered: sample process and file metadata inside `GET /otel/api/status`. Rejected because each status poll would become observation work, CPU needs a prior sample, and failures would couple response latency to the host filesystem.

Alternative considered: use OpenTelemetry runtime instrumentation as the sole process source. Rejected because status must work without an SDK reader/exporter and tests need deterministic process values.

### D6. Model degradation as independent sources, not one mutable flag

`RuntimeObservability` keeps a bounded map keyed by a fixed enum: `collector`, `storage_read`, `storage_write` and `ingest_protection`. An enabled instance starts with `collector_unverified` and `storage_unverified` active; successful Kestrel start clears the collector source, and the first successful storage probe clears the storage source. `collector_online` is projected from that same collector source, not maintained as a second flag. Status is derived as follows:

- configured disabled: `off`;
- configured enabled with no active source: `healthy`;
- configured enabled with any active source: `degraded`.

Collector bind failure remains active until restart with a successful bind. Storage failures remain active until the corresponding successful probe/write. Rejection or drop observations activate `ingest_protection` for five minutes since the latest event; the injected clock expires it when the next observation, sample or status snapshot evaluates state. This makes "currently rejecting or dropping" finite without a wall-clock timer per event. The latest degradation record retains a stable code, a bounded 256-character message and timestamp for the process lifetime, including after recovery.

The state machine compares the derived previous and next states after every update. It emits one structured log only for `healthy -> degraded` and `degraded -> healthy`, with `PreviousState`, `NewState`, `ReasonCode` and `Reason`. Repeated updates within the same state update counters/reason but do not log another transition. `off` startup and shutdown are not degradation transitions.

The OTLP-port fallback in `Program.cs` must stop rewriting `Mohist:Otel:Enabled=false`. After failed `StartAsync`, it calls `StopAsync` on the failed app and always awaits `DisposeAsync`. Alternate-host construction begins only when both operations succeed. A `StopAsync` or `DisposeAsync` failure is logged and propagated after disposal is attempted, because the process cannot prove the first Orleans silo terminated and must not risk starting a second. The alternate host omits only the OTLP Kestrel listener, keeps the user's configured enabled intent, initializes the diagnostics source as `collector_bind_failed`, and still maps the main-port status route. A fallback integration test proves there is one silo and sampler, no OTLP listener, and a `degraded` status with `collector_online=false`.

Alternative considered: a single boolean set by the latest subsystem. Rejected because a successful storage read could incorrectly clear an unrelated write or bind failure.

Alternative considered: keep any rejection/drop degraded until restart. Rejected because transient protection would never recover without a separate manual reset, contradicting transition recovery.

### D7. Replace the status contract as one release with an explicit skew rule

Move status assembly out of `TraceQuerier`; it remains responsible only for trace listing and bounded SQL query execution. `GET /otel/api/status` maps `RuntimeObservability.GetSnapshot()` to this snake-case payload inside the existing `ApiResponse` envelope:

```json
{
  "status": "healthy",
  "collector_online": true,
  "since": "2026-01-01T00:00:00Z",
  "storage": {
    "usage_bytes": 4096,
    "budget_bytes": 1073741824,
    "growth_bytes_per_second": 12.5,
    "growth_window_seconds": 60
  },
  "telemetry": {
    "received_spans": 42,
    "saved_spans": 40,
    "rejected_spans": 1,
    "dropped_spans": 1
  },
  "process": {
    "cpu_utilization": 0.12,
    "working_set_bytes": 134217728,
    "gc_heap_bytes": 33554432
  },
  "latest_degradation": {
    "code": "storage_write_failed",
    "message": "OTel storage write failed",
    "at": "2026-01-01T00:01:00Z"
  },
  "routes": []
}
```

Unavailable sampled numbers are JSON `null`; failures are never represented by fabricated zeros. `trace_count` and `span_count` are removed. The status route stays mapped when OTel is disabled and returns the same fixed shape with `status=off`, zero process-lifetime telemetry counters, empty routes, current working-set/GC-heap values, CPU utilization after a second process sample, and unavailable storage fields.

`mo otel status` continues calling `/otel/api/status`, exits successfully for all three reported states, and renders State, Collector, Storage, Telemetry, Process, Latest degradation and Routes sections. Server unreachable and invalid envelope behavior stay unchanged. The new CLI validates the required `status` field and fails clearly against an old Server payload. Deployment updates the CLI before the Server; after the Server changes, an old CLI may display only zero legacy counts and is explicitly unsupported under the repository's no-compatibility policy. `mo update` installs both from the same release, minimizing that skew window without claiming an atomic process replacement.

`/api/health` is untouched. No OTel state is injected into `HealthRoutes`, so an OTel failure cannot change its status code or `status=ok` payload.

Alternative considered: preserve old counts and add the new fields. Rejected because exact historical counts require the forbidden table scans; retaining fields with process-local meanings would silently change their semantics.

Alternative considered: return HTTP 503 when OTel is degraded. Rejected because observability degradation is not core-service unavailability and callers need a successful bounded diagnostic response.

### D8. Add an amplification object to each active agent response

Add one shared `AgentPathAmplificationDto` with camelCase wire fields `candidates`, `processed`, `transcriptRecords`, `databaseCalls` and `downstreamCalls`. `AgentStatusResponse` and `ActivityDto` each gain an `amplification` property; all existing fields, ordering and response limits remain unchanged. Keep `/api/projects/{projectRef}/agent/status|activity` canonical. Restore `GET /api/agent/status|activity` as thin compatibility aliases: resolve `projectId` query first, then `X-Mohist-Project`, return 400 `No active project` when absent, and delegate to the same scoped handlers. The aliases never enumerate or aggregate all projects.

The agent route begins a named path measurement within the active request scope and finalizes it after all assemblers complete:

- status candidates are Session records selected for active-agent classification; processed is the active-agent records returned; transcript records are zero in the current status path;
- activity candidates are Session records returned by the bounded Session query before reconciliation; processed is the cards returned after reconciliation; transcript records are the transcript parts loaded by `LoadLatestEventsAsync` and `TranscriptReductions` for this request;
- database and downstream calls come from the same `RequestWorkScope`, so every count has one request boundary.

The explicit candidate/processed/transcript increments live at `WorkflowActivityQuerier` and `AgentActivityFeedAssembler`, beside the operations that define them. The route reads the final immutable scope snapshot and adds the DTO only after downstream work is complete. This response-local scope exists even when OTel is off. When enabled, the handler also calls `RecordAgentPath` with `mohist.path=agent.status` or `agent.activity`, and the general HTTP histograms receive database/downstream totals once when middleware completes; when off, neither publication occurs.

#467 can move status candidate selection into SQL while keeping the meanings above; #468 can replace transcript-part loads with persisted summaries and correctly drive `transcriptRecords` to zero. This issue's tests use short and history-heavy datasets to prove that the response-local counters expose any amplification and remain fixed-size; #467/#468 then use those same operation counts, not elapsed time, to prove the amplification is removed.

Alternative considered: expose global process counters on each agent response. Rejected because callers could not derive one response's amplification and concurrent requests would contaminate the values.

Alternative considered: put only computed ratios on the wire. Rejected because zero denominators become ambiguous and raw bounded counts are easier to test, compare and evolve.

## Risks / Trade-offs

- `[AsyncLocal crosses an in-process Orleans boundary or is lost by detached work] ->` Clear/restore it in an incoming grain-call filter and count only work awaited as part of producing the HTTP response. An in-process Orleans chain test proves only caller-side sends count; detached work is not response amplification.
- `[Infrastructure adapters miss direct database or network calls] ->` The catalog explicitly defines database calls as `MohistDbContext` commands and downstream calls as Orleans plus factory-created HTTP calls. Direct `otel.db` work is intentionally excluded; new direct adapters require a contract-test update.
- `[Automatic EF/HTTP/Orleans tracing and operational counters can appear to duplicate signals] ->` Existing instrumentation continues producing Spans; the new adapters produce only integer request counters and never start Activities, so each signal has one role and one write point.
- `[Time aggregation has a boundary approximation] ->` Use one-second buckets and retain the intersecting boundary bucket, so an observation is never dropped before five minutes and over-retention is less than one second; lock that tolerance with fake time.
- `[Synchronous host metadata cannot be forcibly cancelled] ->` Bound the probe by fixed operation count and SQLite's one-second busy timeout, serialize probes on one loop, never abandon work on a detached worker, and state explicitly that shutdown may wait for the current host metadata syscall.
- `[A 256-route cap can fold legitimate routes into other] ->` Mohist's registered route templates are compile-time bounded and expected below the cap; a contract test enumerates endpoint templates, and overflow remains explicit as `other` rather than increasing memory.
- `[Transient rejection keeps status degraded for up to five minutes after pressure ends] ->` This is deliberate hysteresis aligned with the diagnostic window; explicit storage/collector recovery remains immediate.
- `[Sampler failures can leave stale resource values] ->` Mark the sample unavailable and activate a degradation source; never present the prior value as current or substitute zero.
- `[Metric names become a compatibility surface] ->` Lock names, kinds, units and label keys in one test and route all creation through `RuntimeObservability`; changing the catalog requires an explicit spec change.
- `[Unscoped compatibility aliases could become an accidental global view] ->` Require an explicit query/header project selector, resolve exactly one project through `ProjectRefResolver`, and delegate to the canonical handler; missing selection is 400 and no all-project fallback exists.
- `[The status API is breaking] ->` Ship one release, update CLI before Server, make the new CLI reject old payloads, remove old count assertions and state that old-CLI/new-Server skew is unsupported. No persisted client state requires migration.
- `[Follow-up issues need more detailed rejection/storage reasons] ->` Keep reason codes as a bounded enum and counters as fact-oriented methods; #437/#471 can add enum members without changing status structure or introducing identity labels.

## Migration Plan

1. Add the independently testable `RuntimeObservability` publication core: Meter catalog, bounded state, ingestion outcomes and degradation transitions. Keep export/aggregation gated by the disabled-by-default option.
2. Add the fixed status API/CLI shape, normal startup/off behavior and the production fixed-operation one-shot storage probe over the publication core.
3. Replace the one-shot initializer with the continuous process sampler and enabled-only storage sampling, reusing the same production probe; never register both hosted services and never publish gauge measurements while off.
4. Add request scope/adapters and Meter emission as an independently tested accounting module. Keep the response-local scope active for canonical and compatibility agent reads even when OTel is off; suppress it on incoming Orleans turns.
5. Add bounded route aggregation and status/CLI ranked rows over the accounting module.
6. Alter OTLP bind fallback to stop and dispose the failed host before rebuilding while preserving configured enabled intent and publishing `collector_bind_failed`; abort fallback on either shutdown failure.
7. Add agent candidate/processed/transcript accounting, compatibility aliases and additive `amplification` response objects. Preserve current query behavior and make its amplification visible for #467/#468 to optimize later.
8. Run focused Server unit/spec tests and CLI specs, then the repository `npm test`. Verify metric contract, fake-time bucket rotation, transition logs, status no-scan behavior, self-feedback exclusion and `/api/health` independence.

There is no database migration and no persisted diagnostic state. Rollback reverses deployment order: roll back the Server first while the new CLI clearly rejects the legacy payload, then roll back the CLI. This avoids the misleading old-CLI/new-Server combination. Process-local counters and route windows disappear on restart by design. Because OTel remains default-off until #472, rollout can also disable `Mohist:Otel:Enabled` to stop collection, storage probing, Meter measurements and route retention while retaining an `off` status response with bounded process pressure. Later #437/#471 deployments must retain the publication method semantics, but rolling back this issue before those dependents requires rolling back the dependents as well.

## Open Questions

No blocking questions remain for this change.

- External metric export is intentionally undecided. A future change must define a separate exporter endpoint/configuration and prove it cannot target the same built-in receiver before wiring `Mohist.Server.Runtime` into an OTel `MeterProvider`.
- #437 may add storage watermark and maintenance fields internally, but this status contract remains budget/usage/growth plus degradation reason unless that issue proposes a user-visible extension.
