## Context

Mohist currently has a trace-only outbound OpenTelemetry pipeline, a built-in OTLP receiver backed by `otel.db`, and `GET /otel/api/status` implemented by `TraceQuerier.GetStatusAsync`. Status opens SQLite, executes exact Trace/Span counts, catches storage errors, and substitutes zeros. `mo otel status` renders collector state, file size, `trace_count`, and `span_count`.

Agent status and activity are currently project-scoped routes. Status loads project Sessions before active-agent filtering. Activity loads bounded Session candidates, transcript parts, and workflow status per distinct workflow. This change exposes those operation counts; #467 and #468 later reduce them without changing their meanings. #437 and #471 consume the telemetry outcome and protection publication contract defined here but remain responsible for retention and admission policy.

Constraints:

- Runtime signals are operational data, not Workflow, Session, or issue facts.
- Status cost must be independent of telemetry history and external metric systems.
- `Mohist:Otel:Enabled` remains default-off until the full resource gate is delivered. Off disables collection, storage probing, route retention, and Meter measurements, but process diagnostics and response-local agent counts remain available.
- OTel ingest, query, status, and maintenance cannot feed equivalent observations back into the built-in collector.
- Tests use `TimeProvider`, named shared-memory SQLite, fake filesystems, operation counters, and awaitable signals. They do not use wall-clock waits, host files, real sockets, or external services.

Stakeholders are operators using `mo otel status`, API consumers reading OTel and agent status, and follow-up storage/admission work that needs one stable publication boundary.

## Goals / Non-Goals

**Goals:**

- Emit the fixed metric catalog from the specs and maintain a matching bounded local snapshot.
- Attribute stable HTTP routes with request latency, business-database calls, and caller-side downstream calls.
- Expose fixed-cost tri-state OTel status with explicit resource, storage, telemetry, route, and degradation data.
- Add truthful per-response amplification counts to canonical and compatibility agent routes, including while OTel is off.
- Isolate collector, process, storage-read, storage-write, and protection failures so one producer cannot clear another.

**Non-Goals:**

- Implement retention (#437), admission/write limits (#471), status-query optimization (#467), or persisted activity summaries (#468).
- Enable OTel by default, add a metric exporter, dashboard, Prometheus, or Grafana.
- Persist route summaries or use operational signals in business decisions.
- Add global agent aggregation or change Activity-page polling.

## Decisions

### D1. RuntimeObservability is the single bounded publication authority

Add one singleton `RuntimeObservability` in `Mohist.Server.Otel`. It owns the `Meter`, process-lifetime telemetry counters, route ring, cached process/storage samples, degradation sources, and immutable status snapshots. `Program` captures one `RuntimeEpoch` before building the primary host and passes the same `Since` timestamp into a fallback host, so `since` denotes the current Server process run rather than a DI-container instance.

Its public API accepts source-owned facts:

- `CompleteRequest(...)` and `RecordAgentPath(...)`
- `RecordIngest(IngestOutcome outcome)`
- `PublishProcess(ProcessSampleResult result)`
- `PublishStorage(StorageProbeResult result)`
- `PublishCollector(CollectorResult result)`
- `GetSnapshot()`

The source map is private. Process publication alone owns `process_read`, storage publication owns `storage_read`, ingest outcomes own `storage_write` and `ingest_protection`, and collector publication owns `collector`. Success clears only the caller's source. `PublishProcess(failure)` atomically sets CPU utilization, working set, and GC heap to null and discards the prior CPU baseline; the first later success establishes a new baseline with null utilization. `PublishStorage(failure)` atomically sets usage, growth, and growth-window values to null and clears the growth sample ring while retaining the configured budget; the first later success starts a new growth baseline. Mutations and snapshots share one lock; transition logging occurs after releasing it.

`GetSnapshot()` evaluates five-minute protection expiry using injected time, applies any resulting state transition, copies immutable state, and emits a pending transition log after the lock. It performs no database, filesystem, HTTP, or grain call. A status read can therefore trigger protection recovery without making status history-dependent.

Before a write transaction, `TraceIngester` makes one complete classification pass over the materialized request and creates an immutable `PreparedIngestBatch`. Every identifiable Span attempt is provisionally classified as parsed-for-write, malformed dropped, protection rejected, or another non-retryable drop. An `IngestOutcomeBuilder` with a private-result constructor combines that classification with exactly one write result: `not_attempted`, `committed`, or `rolled_back` with bounded reason text. It derives the response category, all four counters, and degradation changes, so callers cannot provide contradictory aggregates.

The write result has batch-level precedence. When the accepted subset commits, or when no accepted subset exists and no write is attempted, the provisional non-retryable classifications become final: committed parsed Spans are saved and rejected/dropped attempts produce HTTP 200 partial success. When an accepted subset rolls back, the whole request instead returns retryable HTTP 503. That attempt records every successfully parsed Span as received, records zero saved/rejected/dropped, activates only `storage_write`, and neither activates nor refreshes `ingest_protection`; provisional malformed/rejected/drop classifications do not become non-retryable loss while the exporter is being asked to retry the whole request. Repeated rollback retries can therefore add received attempts but cannot repeatedly add rejected/dropped counters. Only `committed` from a transaction that actually entered the production write path clears `storage_write`; an empty, wholly rejected, wholly dropped, or parse-failed request is `not_attempted` and cannot establish write readiness. The same atomic `RecordIngest` call refreshes protection only when final rejected or dropped counts are non-zero.

`OtlpTraceResponseWriter` is the sole OTLP/HTTP response encoder. It selects JSON for `application/json` requests and protobuf for `application/x-protobuf` or the retained `application/protobuf` alias; `Accept` does not override the request encoding. Full success is HTTP 200 with an empty standard `ExportTraceServiceResponse`: JSON `{}` with `application/json`, or the valid zero-byte default protobuf message with `application/x-protobuf`. Rejected or dropped attempts are HTTP 200 with `partial_success`: JSON uses the OTLP protobuf-JSON fields `partialSuccess.rejectedSpans` as a decimal string and `errorMessage`, while protobuf uses `ExportTraceServiceResponse.partial_success` field 1 containing `rejected_spans` field 1 and `error_message` field 2. The rejected count is `rejected + dropped`, and the bounded message describes both categories when both occur.

Whole-body decode failures never enter ingestion. Malformed JSON returns HTTP 400 plus a JSON `google.rpc.Status` with code 3 (`INVALID_ARGUMENT`); malformed or truncated protobuf, including `InvalidProtocolBufferException`, returns the same HTTP/code semantics encoded as protobuf `google.rpc.Status` with `application/x-protobuf`. A retryable rolled-back write returns HTTP 503 plus code 14 (`UNAVAILABLE`) in the request encoding, including when the same batch also has provisional rejected or dropped attempts. Status messages are bounded to 256 characters and have no `details`; unsupported or missing media types return HTTP 415 with JSON `google.rpc.Status` because no supported request encoding was selected. Route contract tests assert status, exact content type and JSON shape, and decode protobuf fields for full success, partial success, malformed input, retryable failure, and mixed classification plus rollback.

Alternative considered: separate counter, route, sample, and degradation services. Rejected because they share one snapshot and transition boundary and would expose cross-service ordering to every publisher.

Alternative considered: publish independent aggregate integers from the ingester. Rejected because early write failure and partial classification could create impossible counter combinations.

Alternative considered: return JSON for every request as the current route does. Rejected because OTLP/HTTP requires a protobuf `ExportTraceServiceResponse` or `google.rpc.Status` for protobuf requests, and a partial-success HTTP code without the matching wire message is not actionable to exporters.

### D2. The standard Meter has no built-in exporter

Create `Meter("Mohist.Server.Runtime")` and the exact instruments, units, and attribute sets declared by the runtime-metrics spec. The same fact methods update Meter instruments and local state. Request methods, status codes, routes, and agent paths are normalized before publication; identity-bearing values never reach labels.

Observable callbacks read cached samples only. They never call `Process`, SQLite, or the filesystem. While OTel is off, callbacks return no measurements even though process sampling continues for status. No `AddOtlpExporter` metrics registration is added: the existing endpoint defaults to the same built-in receiver and is trace-oriented. A `MeterListener` contract test locks the full catalog and bounded label values.

Self-observation filtering keeps the existing ASP.NET path filter for all `/otel` requests and the exporter-URI HttpClient filter as defense in depth. Add one `OtelSuppressionMiddleware` immediately inside routing for `/otel/v1` and `/otel/api`; it enters `OpenTelemetry.SuppressInstrumentationScope.Begin()` around the awaited endpoint delegate, suppressing child EF, Orleans, and HttpClient instrumentation after the ASP.NET filter has excluded the server Activity. The sampler enters the same scope around each awaited storage probe and any future callback registered through the OTel maintenance executor. Process reads and RuntimeObservability publication happen outside that scope because they do not create equivalent child observations. The scope is lexical, disposed in `finally`, and flows with the awaited execution context; it is separate from `RequestWorkScope` and never changes operation results.

Alternative considered: use only ASP.NET Core, EF Core, and HttpClient built-in metrics. Rejected because their labels are not Mohist's compatibility contract and they do not provide the local route or agent summary.

Alternative considered: export metrics through the current OTLP endpoint. Rejected because that creates a self-targeting configuration and the built-in receiver does not own a metrics pipeline.

### D3. One request-local scope counts work at infrastructure boundaries

Install `RuntimeRequestMetricsMiddleware` after routing and inject `TimeProvider`. It creates a scope when `(OTel enabled AND endpoint is outside /otel/v1 and /otel/api) OR endpoint is a canonical/compatibility agent status/activity route`. Immediately before invoking the endpoint delegate it captures `startedTimestamp = TimeProvider.GetTimestamp()`. In `finally`, including when the delegate throws or request cancellation surfaces, it captures a second timestamp and computes duration with `TimeProvider.GetElapsedTime(startedTimestamp, completedTimestamp).TotalMilliseconds`; no wall-clock API or stopwatch is used. The same `finally` clears the ambient slot and calls `CloseAndSnapshot()` exactly once, so increments linearized before close are included and later increments no-op. When an exceptional or cancelled delegate has not produced a stable HTTP status, normalization uses status `0`; recording never handles or replaces the original exception. `CompleteRequest` runs once only when OTel is enabled. Production composition registers `TimeProvider.System`, and middleware tests replace it with `FakeTimeProvider`.

`RequestWorkScope` has lock-protected additive database/downstream counters plus optional agent-path state. `SetAgentPath` succeeds once with `agent.status` or `agent.activity`; `AddCandidates`, `AddProcessed`, and `AddTranscriptRecords` accept non-negative deltas. `Snapshot()` is non-terminal so an agent handler can read all five values after awaited assembly; `CloseAndSnapshot()` is terminal. When enabled, the final path values are also published once through `RecordAgentPath`.

Four adapters update the active scope and otherwise no-op:

- an EF Core `DbCommandInterceptor` counts each `MohistDbContext` command;
- an Orleans outgoing filter counts each caller-side grain invocation;
- an Orleans incoming filter saves and clears the scope for the complete grain turn, then restores it, preventing transitive grain and grain-database attribution;
- a counting `DelegatingHandler` counts each physical factory-created HTTP send, including retries.

The HTTP builder filter is registered outermost, invokes the remaining filter chain first, then appends the counting handler last in `AdditionalHandlers`, immediately outside `PrimaryHandler`. A composition test with a retry handler locks that ordering. Production installs the EF interceptor in `MohistServiceRegistration`. `MohistIntegrationFixture` and `GrainTestConfig`, the two roots used for HTTP-to-Orleans accounting specs, mirror it; unrelated custom factories remain unchanged and interceptor unit tests construct options explicitly.

Introduce `IBackgroundTaskLauncher` for the two current fire-and-forget sites, `SystemUpdateService` and `BackgroundHermesIssueNotificationDispatcher`. Production temporarily clears only the request-scope ambient slot while queuing `Task.Run`, then restores the caller slot. Its fake captures callbacks and exposes awaitable started/completed signals. Tests separately prove scope suppression, post-close immutability, and both services' use of the launcher.

Alternative considered: reconstruct counts and duration from child Spans. Rejected because sampling can omit Activities, completion lags the response, and a second listener would duplicate existing tracing.

Alternative considered: add counters manually to every service. Rejected because new EF, grain, or HTTP calls would silently escape accounting.

### D4. Route diagnostics use a half-open bounded ring

Use 301 rotating buckets driven by `TimeProvider`. Bucket second `s` represents `[s, s + 1 second)`. Each bucket retains 256 named routes plus `other`; overflow observations contribute to `other`. Every aggregate stores request count, total/max duration, database calls, and downstream calls.

At snapshot time `now`, include a bucket only when `bucketEnd > now - 5 minutes` and `bucketStart <= now`. Equality at the old boundary is excluded. This keeps observations for at least five minutes and less than five minutes plus one second. The maximum retained route aggregates are `301 * 257`.

Merge included buckets, compute average duration and per-request calls, then sort by `(databaseCallsPerRequest + downstreamCallsPerRequest)` descending, average duration descending, and route name ordinal ascending. Return at most 10 rows. Tests cover fractional/integral boundaries immediately before, at, and after expiry; overflow; ties; restart; and fixed memory.

Alternative considered: retain individual requests for an exact sliding window. Rejected because memory grows with traffic.

Alternative considered: persist aggregates. Rejected because the diagnostic is restart-local and persistence would create another observability workload.

### D5. Process startup sampling is isolated from post-start storage probing

One `OtelDiagnosticsSampler` owns diagnostic sampling. Its `StartAsync` performs one failure-contained process read in every configured state, publishes success or failure, and returns without touching storage. Server availability therefore waits only for current process values or an explicit unavailable result.

The serial async loop waits for `ApplicationStarted`. A host that fails before that signal never probes storage. After the signal, an enabled host performs one immediate storage probe. Every subsequent injected 10-second tick reads process resources in both enabled and off states and independently probes storage only when enabled. Process and storage exceptions are caught and published separately, so one failure cannot skip or invalidate the other sample. Ticks coalesce while an iteration runs. Shutdown starts no new iteration and awaits the current fixed-operation probe; it does not abandon work on `Task.Run`.

`IProcessResourceReader` returns total CPU time, working set, GC heap size, and logical processor count in one fakeable sample. CPU utilization is the CPU-time delta divided by injected elapsed time and that sample's positive processor count, clamped to `[0,1]`. The first valid sample and the first success after failure establish a baseline with null utilization. A non-positive processor count is a process-read failure, not a host-dependent fallback. Storage growth uses the oldest and newest valid samples in a seven-slot, 60-second ring; growth and `growth_window_seconds` are null until two samples exist, then the window reports their actual elapsed seconds up to 60. Process and storage failure publication uses D1's atomic cache invalidation, so stale values and pre-failure growth baselines never reappear after recovery.

`IOtelStorageProbe` composes `IOtelReadinessConnectionFactory` and `IFileSystem`. `OtelDb.OpenReadinessConnection()` clones the read/write-create string with `Default Timeout=1`, opens it, and returns without calling `EnsureInitialized` or acquiring `_initGate`; the metadata probe must not wait behind ingestion schema initialization. The probe performs one `PRAGMA schema_version` header/read-lock operation, then calls nullable `GetFileLength` exactly once for `otel.db`, WAL, and SHM. Missing files contribute zero; other errors propagate. This proves bounded metadata readability only: it never clears the initial write-readiness source. The probe does not issue a synthetic write because recurring canary commits would create the WAL growth being diagnosed. It never counts rows or enumerates telemetry history. SQLite command wait is bounded; host filesystem calls are not cancellable and have no wall-clock guarantee.

Alternative considered: sample inside the status request. Rejected because polling would perform observation work, CPU requires a prior sample, and filesystem failure would affect response latency.

Alternative considered: initialize the OTel schema from readiness. Rejected because it would contend on the managed initialization gate and make a diagnostic probe mutate collector schema.

### D6. Degradation sources and reason arbitration are deterministic

Keep five fixed sources: `collector`, `process_read`, `storage_read`, `storage_write`, and `ingest_protection`. Enabled startup begins with `collector_unverified` on `collector` and `storage_unverified` on `storage_write`; `storage_read` starts clear, and process `StartAsync` publishes either values or `process_read_failed`. A successful metadata probe publishes storage values and clears only a later `storage_read_failed`; it does not clear `storage_unverified`. The first real ingestion write transaction that commits clears `storage_write`; a rollback replaces that source with `storage_write_failed`. State projection is:

- configured off: `off`;
- configured enabled with no active source: `healthy`;
- configured enabled with any active source: `degraded`.

`collector_online` is true only when collection is enabled and the collector source is clear. Protection uses `telemetry_rejected` or `telemetry_dropped`; dropped wins if one outcome contains both. Protection expires five minutes after its latest outcome when the next publication, sample, or snapshot evaluates time. Process and storage-read sources clear only on their corresponding sample success; storage-write clears only on a committed production write. Stable codes are `collector_unverified`, `collector_bind_failed`, `process_read_failed`, `storage_unverified`, `storage_read_failed`, `storage_write_failed`, `telemetry_rejected`, and `telemetry_dropped`; each has one default message and an optional message truncated to 256 characters.

Every activation/refresh receives a monotonically increasing sequence under the state lock. `latest_degradation` is the most recently sequenced degradation event and remains available after recovery; ties use source enum order. Entering-degraded logs use the event that caused the transition. Recovery logs use the source/reason being cleared. Updates that leave the derived state unchanged do not log.

Alternative considered: one mutable degraded flag. Rejected because one subsystem's success could clear another subsystem's failure.

Alternative considered: keep rejection degraded until restart. Rejected because transient protection would never recover.

### D7. A testable host runner owns fallback and state transfer

Move startup orchestration out of top-level statements into an internal `MohistHostRunner`. It accepts `IMohistHostFactory` and `IOtelBindFailureClassifier`. `IMohistHostFactory.CreatePrimary` and `CreateAlternate` return an `IMohistHost` exposing `Services`, `InitializeDatabaseAsync`, `StartAsync`, `StopAsync`, `DisposeAsync`, and `WaitForShutdownAsync`; the production adapter wraps `WebApplication`, while tests use signal-controlled fake hosts. The production adapter implements `InitializeDatabaseAsync` by invoking the existing `DatabaseInitializer.InitializeAsync(Services, cancellationToken)`, which applies EF migrations and then the repository data upgrade. The production factory creates an immutable `MohistHostPlan` containing the shared `RuntimeEpoch`, initial `CollectorResult`, enabled intent, and listener intents. Primary and alternate both pass that plan through one `Build(plan)` composition path that always configures the same Orleans, routes, DI, and one sampler registration; alternate is derived only by removing the OTLP listener intent and replacing the initial collector result. No alternate-only builder path may duplicate those registrations. The classifier wraps `OtelBindFailureDetector` and receives the exception plus configured OTLP endpoint. Top-level `Program` only builds production adapters, captures the epoch, and invokes the runner.

For every host attempt, the runner invokes `InitializeDatabaseAsync` exactly once after construction and before `StartAsync`; the primary therefore migrates/upgrades before its start attempt, and a fallback alternate repeats the idempotent initialization before its own start, preserving both current startup paths. An initialization failure is terminal and is never passed to the bind-failure classifier: `StartAsync` is not called, no alternate is created for a primary initialization failure, and the runner always attempts to dispose the unstarted host. If initialization and disposal both fail, it throws an `AggregateException` preserving initialization then disposal order.

Normal construction starts collector-unverified; after primary `StartAsync` succeeds, the runner publishes collector-online through the host's services before waiting for shutdown. On classified OTLP bind failure, the runner stores failure details outside the failed container, awaits `StopAsync`, and always attempts `DisposeAsync`. If either fails, no alternate host is built: throw the single failure or an `AggregateException` preserving stop then dispose errors when both fail.

Only after clean shutdown does `CreateAlternate` receive the same epoch plus `collector_bind_failed`. The alternate plan omits only the OTLP listener, preserves `Mohist:Otel:Enabled`, and seeds its new `RuntimeObservability` with that collector result before `StartAsync`. The failed primary can publish its initial process sample but never receives `ApplicationStarted`; only the alternate enters post-start sampling, so exactly one storage probe occurs across both host attempts.

Runner unit tests use fake factory/hosts/classifier and fake application-start signals to trigger primary/alternate initialization failure, generic start failure, classified bind failure, stop failure, dispose failure, and dual failure without ports. They own lifecycle-boundary assertions: database initialization precedes each start, a failed initialization prevents that start and is disposed without fallback, initialization/disposal failures retain order, stop/dispose ordering is preserved, no alternate follows shutdown failure, at most one started host remains, the primary performs zero storage probes, the alternate performs one immediate probe, and the alternate snapshot projects degraded with `collector_online=false`. They do not issue HTTP requests or claim that a production silo was started.

A separate non-starting production composition test owns only static graph assertions. It compares primary and alternate `MohistHostPlan` values, verifies that epoch/enabled/application composition are identical while only OTLP listener intent and initial collector result differ, and inspects service descriptors to prove one sampler registration and the shared Orleans/API registration markers per plan. It does not call `Build`, `InitializeDatabaseAsync`, `StartAsync`, resolve physical filesystem adapters, open listeners, or claim runtime probe/silo facts. Because the production adapter has one `Build(plan)` implementation and no alternate composition branch, the inspected plan and registrations are the exact inputs consumed by production construction.

Alternative considered: set `Mohist:Otel:Enabled=false` for fallback. Rejected because it hides configured intent and reports `off` instead of collector degradation.

### D8. Status and agent routes map stable DTOs over shared handlers

Move status assembly out of `TraceQuerier`; it remains responsible for trace listing and bounded SQL query execution. `/otel/api/status` maps `RuntimeObservability.GetSnapshot()` into the snake-case DTO specified by the status spec. Apply `JsonIgnore(Condition = JsonIgnoreCondition.Never)` to every nullable status field and `latest_degradation`, because the shared JSON options omit nulls by default. Raw-JSON specs lock field presence for off, warm-up, failure, recovery, and healthy states. `since` is `RuntimeEpoch.Since`; storage budget is the fixed 1-GiB design budget even while off; usage, growth, and growth window are null while off. The CLI renders all sections and validates the required `status` field. `trace_count` and `span_count` are removed. `/api/health` remains untouched, and T-003's in-memory HTTP API spec owns the status-code/payload assertion that degraded OTel does not change core health.

Extract the inline agent lambdas into private/shared handlers that accept a resolved project identity. Canonical routes continue resolving `{projectRef}` through the endpoint filter. Compatibility routes trim all query/header values, select the first nonblank `projectId` query value and otherwise the first nonblank `X-Mohist-Project` value, then resolve that value through `ProjectRefResolver`; both IDs and names remain accepted. No value returns 400. A nonblank query wins a conflict. Middleware creates the agent scope before either canonical or compatibility project resolution, so resolution database work is included consistently.

The handlers set the scope path and update counters beside the operations that define them. Status counts Sessions considered for active classification and active rows returned; transcript is zero. Activity counts bounded Session candidates before reconciliation, cards returned, and every transcript record materialization across both transcript operations, including repeated materialization. After all awaited work, the handler snapshots the scope and adds `amplification`; existing fields, ordering, limits, and semantics remain unchanged.

Alternative considered: preserve legacy status counts alongside the new DTO. Rejected because exact counts require forbidden scans and process-local replacements would silently change meaning.

Alternative considered: expose global agent counters or only ratios. Rejected because concurrent requests contaminate global values and zero denominators make ratios ambiguous.

## Risks / Trade-offs

- `[Ambient scope leaks across Orleans or detached work] ->` Clear it for incoming grain turns, close atomically at response completion, and queue fire-and-forget work through the scope-suppressing launcher.
- `[Infrastructure adapters miss direct calls] ->` Define database calls as `MohistDbContext` commands and downstream calls as caller-side Orleans plus factory-created HTTP sends; contract tests lock those boundaries.
- `[Automatic tracing appears to duplicate counters] ->` Adapters increment integers only and never start Activities; Trace and amplification retain distinct roles.
- `[OTel child instrumentation forms a feedback loop] ->` Keep the inbound/exporter filters and wrap awaited OTel endpoint, probe, and maintenance work in `SuppressInstrumentationScope`; tests assert suppression disposal and unaffected non-OTel traces.
- `[Route buckets approximate the boundary] ->` Use half-open arithmetic and test exact fractional/integral boundaries; over-retention remains strictly below one second.
- `[Synchronous metadata work blocks shutdown] ->` Use fixed operation count and SQLite command timeout, never abandon a worker, and explicitly accept that a host filesystem call can delay shutdown.
- `[Route cardinality exceeds the cap] ->` Fold overflow into visible `other` while preserving fixed memory.
- `[Transient protection remains degraded after pressure stops] ->` Use deliberate five-minute hysteresis aligned with the diagnostic window and fake-time tests.
- `[Nullable status fields disappear] ->` Override the shared null-ignore policy on the status DTO and assert raw JSON field presence.
- `[Metric names become compatibility surface] ->` Keep the catalog in the capability spec and lock it with one `MeterListener` test.
- `[Compatibility aliases become global views] ->` Require one explicit selector, resolve one project, and delegate to the same handlers.
- `[Breaking status skew misleads old clients] ->` Update CLI before Server in the release and make the new CLI reject old payloads; old-CLI/new-Server skew is unsupported.

## Migration Plan

1. Add `RuntimeEpoch`, `RuntimeObservability`, typed ingest preparation/outcomes, source-owned degradation state, and the fixed Meter catalog.
2. Deliver one usable status module: fixed API/CLI mapping, non-initializing readiness probe, startup/recurring sampler, normal collector publication, and resource gauges.
3. Add request scope, infrastructure adapters, background launcher, and relevant production/test composition-root wiring.
4. Complete feedback exclusion with inbound/exporter filters and suppression scopes around endpoint and background OTel work.
5. Add the bounded route ring and status/CLI ranking.
6. Refactor fallback into `MohistHostRunner` with explicit database initialization before every start attempt, epoch/collector-result transfer, and fakeable host lifecycle.
7. Extract shared agent handlers, add compatibility resolution, and publish response-local/path counters.
8. Run focused Server unit/spec and CLI tests, then repository `npm test`; verify no generated Git changes remain.

There is no database migration or persisted diagnostic state. Deploy CLI before Server. Rollback Server first, then CLI. Disabling `Mohist:Otel:Enabled` stops collector, storage probing, route retention, and Meter measurements while preserving off-state process and response-local diagnostics. Rolling back this publication contract after #437/#471 consume it requires rolling those dependents back first.

## Open Questions

No blocking questions remain.

- External metric export requires a future endpoint/configuration contract that cannot target the built-in receiver.
- #437 can add internal storage watermarks and maintenance state through the typed storage/protection outcomes; any new user-visible status fields require a separate spec change.
