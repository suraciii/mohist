# Review - Issue 470

81 files changed, +7466/-511 lines across all 8 tasks. All tasks are committed and unit
tests (1318) pass. This review found correctness bugs, spec violations, and
observability gaps that must be resolved before merge.

## Correctness bugs

### 1. TOCTOU race between `CompleteRequest`/`RecordIngest` and `Dispose()`

`RuntimeObservability.cs:217-234` — `CompleteRequest` checks `IsDisposed()` outside the
lock (line 217), then records to `_httpRequestCount`, `_httpRequestDuration`,
`_httpRequestDatabaseCalls`, `_httpRequestDownstreamCalls` (lines 226-229) without any
re-guard. `Dispose()` can set `_disposed = true` and call `_meter.Dispose()` on another
thread between the check and the record calls, throwing from disposed instruments.
`RecordIngest` at line 324 has the same pattern. The route recording at line 233 does
re-check `_disposed` inside the lock, but the Meter instrument calls at 226-229 do not.

### 2. `maxNanos` constant overflows in `unchecked` context

`TraceIngester.cs:307` — `unchecked(3_155_378_975_999_999_999L * 100L) + 99L` means
`DateTimeOffset.MaxValue.Ticks * 100`. This overflows `long.MaxValue` in `unchecked`,
wrapping to ~1.94 × 10^18 (July 2031 in Unix nanoseconds). Any OTLP span timestamp after
that date is silently rejected by the `nanos > maxNanos` clamp on line 308. Additionally,
the constant is in DateTimeOffset ticks (epoch 0001-01-01), not Unix epoch nanoseconds
(epoch 1970-01-01), so even with correct arithmetic the ranges don't align.

### 3. Dropped and rejected counts lost on `RolledBack` and `Cancelled` batches

`RuntimeObservabilityContracts.cs:569-574` — In the `RolledBack` and `Cancelled` cases of
`IngestOutcomeBuilder.Build`, `dropped` and `rejected` remain at their initial value `0L`
even though `classification.MalformedDropped`, `classification.OtherDropped`, and
`classification.ProtectionRejected` may be non-zero. The design spec (D1) says
"provisional rejected/dropped classifications do not become non-retryable loss while the
exporter is being asked to retry" — this is intentional for the wire response, but the
metrics spec at `specs/runtime-observability-metrics/spec.md` Scenario "mixed batch rolls
back" says `dropped` SHALL NOT increment, which is correct. However `rejected` spans
classified before the write SHOULD still increment `received` (they do via
`classification.ParsedForWrite`), and the spec explicitly confirms both rejected and
dropped stay at zero for rolled-back. This is spec-compliant per the detailed scenario at
spec line 100 ("repeating the same rolled-back request SHALL NOT repeatedly increment
rejected or dropped counters").

**Verdict: NOT a bug — spec-compliant. The counters are intentionally zero for retryable
batches because the exporter is expected to retry the entire request.**

### 4. `EnsureDirectoryExists` calls real filesystem, not injected `IFileSystem`

`OtelDb.cs:291` — `Directory.CreateDirectory(dir)` is a static real-filesystem call that
bypasses the `IFileSystem` injected into the method. Tests using a fake filesystem that
exercise `OtelDb` constructor paths requiring directory creation will silently create real
directories on disk. Violates design constraint "no real filesystem in tests."

### 5. `IFileSystem.GetFileLength` default implementation uses real filesystem

`PhysicalFileSystem.cs:9` / `IFileSystem` default — The default interface method
`GetFileLength` calls `new FileInfo(path).Length`, accessing the real filesystem. Fake
implementations like `InMemoryServerFileSystem` do not override this method, so calling
`GetFileLength` on a fake filesystem falls through to the real host filesystem.

### 6. `_logger` field stored but never used in `MohistHostRunner`

`MohistHostRunner.cs:43,49` — The runner accepts and stores `ILogger<MohistHostRunner>`
but never logs. No primary-success log, no fallback-trigger log, no initialization-failure
log, no shutdown log. Operators have no structured log visibility into host lifecycle events.

### 7. `LoggerFactory` never disposed in `Program.cs`

`Program.cs:11` — `LoggerFactory.Create(...)` returns `IDisposable` but is never disposed.
Resource leak.

### 8. `OtelBindFailureClassifier` lacks logger in production

`Program.cs:9` — `new OtelBindFailureClassifier()` is constructed with no logger.
`LogWarning` call at `OtelBindFailureClassifier.cs:39-43` never fires in production.
Bind-failure fallback is only visible via `Console.Error`, not the structured file logger.

### 9. Bind host forced to `IPAddress.Any` / `IPAddress.Loopback`

`MohistHostFactory.cs:95-104` — Any `Host` value that is not exactly `"0.0.0.0"` or `"*"`
is mapped to `IPAddress.Loopback` (`127.0.0.1`). IPv6 addresses like `::1`, custom
interface IPs like `192.168.1.100`, and `127.0.0.2` are all silently overridden to IPv4
loopback. Configuration in `Mohist:Host` and `Mohist:Otel:BindHost` is effectively ignored.

### 10. Bind-failure detection only matches four hardcoded host patterns

`OtelBindFailureDetector.cs:35-38` — `IsOtlpPortBindFailure` only matches exception
messages containing `127.0.0.1:{port}`, `0.0.0.0:{port}`, `[::]:{port}`, or
`localhost:{port}`. Custom bind hosts cause the detector to miss bind failures entirely.

### 11. No guard that `Enabled=true` requires non-null `ListenerIntent`

`IMohistHost.cs:63-67` — `MohistHostPlan.Primary()` accepts `enabled: true` but has no
guard that `listenerIntent` must be non-null. A null listener intent with `enabled: true`
produces a plan where Kestrel never binds a collector but `RuntimeObservability` starts
with `enabled = true`. The runner publishes `CollectorResult.Online()` after `StartAsync`
succeeds, producing a false-positive healthy status — the collector was never bound.

## Spec / contract compliance

### 12. `AgentActivityFeedAssembler` hardcodes `Completed: 0, Failed: 0`

`AgentActivityFeedAssembler.cs:112-117` — `ActivitySummaryDto` fields `Completed` and
`Failed` are always zero. If the activity feed intentionally only shows active/waiting,
these fields should not exist on the DTO. Otherwise the UI sees permanently zero values
regardless of actual state.

### 13. `workType` empty-string fallback inconsistency

`WorkflowActivityQuerier.cs:121` treats empty-string `workType` as `"task"`; 
`AgentActivityFeedAssembler.cs:225,247` only coalesces `null` to `"task"`, leaving empty
string as-is. Two surfaces disagree on the same underlying data.

### 14. `SystemUpdateService` bypasses DI-registered `IBackgroundTaskLauncher`

`SystemUpdateService.cs:21-30` — The DI constructor hardcodes `new BackgroundTaskLauncher()`
instead of accepting `IBackgroundTaskLauncher` from DI. Every other consumer
(`BackgroundHermesIssueNotificationDispatcher`, `IssueStore`, `WorkflowRunStore`,
`AgentSessionStore`) injects it correctly. A decorated or test-fake launcher registered
in DI won't reach `SystemUpdateService`.

### 15. `AgentSessionStore.Deserialize` swallows all exceptions silently

`AgentSessionStore.cs:143-156` — A bare `catch { return null; }` block silently discards
all exceptions from `JsonSerializer.Deserialize`, `ApplyColumnDefaults`, and
`ValidateState`. Corrupt session rows silently disappear from query results with zero log
output.

### 16. Transcript parts loaded from database twice in same activity request

`AgentActivityFeedAssembler.cs:96-97` — Both `LoadLatestEventsAsync` and
`LoadEventSummariesWithCountAsync` independently materialize the same transcript parts from
the database. The spec says "count each transcript record materialized, including repeated
materialization" (spec-compliant), but the DB I/O is inherently doubled per request.

### 17. CLI does not respect `MOHIST_OTEL_DB_PATH` environment variable

`MohistCliCommands.Otel.cs:152-177` — `ResolveDatabasePath` only checks the `--db` CLI
argument and the default path (`MOHIST_DB_PATH` directory or `~/.mohist/otel.db`). The
server follows `OtelOptions.DbPathEnvironmentVariable` (`MOHIST_OTEL_DB_PATH`) as well.
If a non-default path is set via that env var, `mo otel status` finds nothing.

## Observability and resilience gaps

### 18. Duplicate `IsOtelRequest` logic in two middleware classes

`RuntimeRequestMetricsMiddleware.cs:57-59` and `OtelSuppressionMiddleware.cs:20-22` — Both
classes independently re-implement the same "/otel/v1" + "/otel/api" path matching. If
routing conventions change, both must be updated in sync. Desynchronization would cause
suppressed requests to still create work scopes or vice versa.

### 19. `EmitTransitions` silently swallows all transition emission failures

`RuntimeObservability.cs:907-926` — Empty `catch { }` block means logger or transition-sink
failures are invisible. An internal counter should at minimum track emission failures.

### 20. `ResolveUnitDir` bypasses `IEnvironmentVariableProvider`

`SystemdInstallDetector.cs:112-113` — Directly calls `Environment.GetFolderPath` for home
directory resolution, while the rest of the codebase resolves `HOME` through
`IEnvironmentVariableProvider`. If `HOME` is overridden, `SystemdInstallDetector` looks in
the wrong directory.

### 21. `ActivitySummaryDto.Completed` / `ActivitySummaryDto.Failed` always zero

`AgentRoutes.cs:180` — `EmbeddedRunnerEnabled` is unconditionally `false`, never derived
from system state.

### 22. `OtelQueryRoutes` and `TraceQuerier` each create independent budget timers

`OtelQueryRoutes.cs:128-134` and `TraceQuerier.cs:139-146` — Two separate 10-second timers
are created per query request. The route handler cancels one; the querier internally
interrupts SQLite via the other. Split cancellation handling increases fragility.

## Design / maintainability issues

### 23. `RuntimeEpoch` double-registration via `TryAddSingleton` + `AddSingleton`

`MohistServiceRegistration.cs:156-157` (`TryAddSingleton`) and
`MohistHostFactory.cs:109` (`AddSingleton`) — The plan's epoch overwrites the DI-fallback
registration. If `ApplyPlan` ever changes to `TryAddSingleton`, the plan's epoch would be
silently ignored and a new epoch captured.

### 24. `OtelPortBindingLog` utility class in `Program.cs` top-level statements

`Program.cs:27-41` — The class is referenced from `OtelBindFailureClassifier.cs` but lives
in a top-level statement file. If `Program.cs` is refactored, cross-file references to
compiler-generated `Program` class members break.

### 25. `RuntimeObservability` mixes state machine, metric publishing, route aggregation, and snapshot generation

`RuntimeObservability.cs` — 983-line class sharing one `_gate` lock. Under high traffic,
`CompleteRequest` (per-request route bucket updates) contends with `GetSnapshot`
and gauge callbacks on the same lock.

---

`Mohist.Server.UnitTests`: 1318 passed, 0 failed. `Mohist.Server.SpecTests` could not be
completed within the timeout window (300s); they are known to be environment-sensitive.

<promise>FAIL</promise>
