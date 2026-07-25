# Review - Issue 470 (Round 2)

90 files changed, +7791/-582 lines across 8 implementation tasks plus 3 fix commits.

The previous review (round 1) found 25 findings and returned FAIL. A fix task
addressed the actionable findings across 3 commits. This round verifies the
current state.

## Verification of Round-1 Fixes

All correctness bugs and spec violations from round 1 have been properly fixed:

| # | Finding | Status |
|---|---------|--------|
| 1 | TOCTOU race in `CompleteRequest`/`RecordIngest` vs `Dispose()` | FIXED — Meter instrument recording now happens inside `lock(_gate)` with `_disposed` re-check |
| 2 | `maxNanos` constant overflow in `TraceIngester` | FIXED — replaced broken clamp with `try/catch (ArgumentOutOfRangeException)` on `DateTimeOffset.FromUnixTimeSeconds` |
| 4 | `EnsureDirectoryExists` bypassed injected `IFileSystem` | FIXED — calls `fileSystem.CreateDirectory(dir)` |
| 5 | `IFileSystem.GetFileLength` default used real filesystem | FIXED — removed default method; all implementations (PhysicalFileSystem, InMemoryServerFileSystem, 6 test fakes) now explicitly implement it |
| 6 | `MohistHostRunner._logger` stored but never used | FIXED — logs primary-start-success and bind-failure-fallback |
| 7 | `LoggerFactory` never disposed in `Program.cs` | FIXED — `using var loggerFactory` |
| 8 | `OtelBindFailureClassifier` lacked logger in production | FIXED — `Program.cs` passes `loggerFactory.CreateLogger<OtelBindFailureClassifier>()` |
| 9 | Bind host forced to `IPAddress.Any`/`Loopback` | FIXED — `ResolveBindAddress` uses `IPAddress.TryParse`, handles IPv6 and custom IPs |
| 10 | Bind-failure detection only matched hardcoded patterns | FIXED — `IsOtlpPortBindFailure` accepts `bindHost` parameter |
| 11 | No guard that `Enabled=true` requires non-null `ListenerIntent` | FIXED — `MohistHostPlan.Primary` throws if enabled with null listener |
| 13 | `workType` empty-string fallback inconsistency | FIXED — assembler uses `string.IsNullOrEmpty(workType) ? "task" : workType` |
| 14 | `SystemUpdateService` bypassed DI `IBackgroundTaskLauncher` | FIXED — DI constructor now accepts it |
| 15 | `AgentSessionStore.Deserialize` swallowed all exceptions silently | FIXED — accepts optional `ILogger?` and logs the failure |
| 18 | Duplicate `IsOtelRequest` in two middleware classes | FIXED — shared via `OtelSuppressionMiddleware.IsOtelRequest` (internal) |
| 19 | `EmitTransitions` silently swallowed emission failures | FIXED — logs via `_logger?.LogWarning` |
| 20 | `ResolveUnitDir` bypassed `IEnvironmentVariableProvider` | FIXED — `SystemdInstallDetector` injects it; checks `HOME` first |
| 24 | `OtelPortBindingLog` in `Program.cs` top-level statements | FIXED — moved to `Infrastructure/Hosting/OtelPortBindingLog.cs` |

No new correctness bugs were introduced by any fix.

## Remaining Items (Not Blocking)

These items from round 1 are either pre-existing, spec-compliant trade-offs, or
non-blocking design observations:

- **#3** (dropped/rejected counts on RolledBack/Cancelled) — round 1 determined
  this is spec-compliant, not a bug.
- **#12** (`ActivitySummaryDto.Completed`/`Failed` always 0) — pre-existing DTO
  shape, not introduced by this PR.
- **#16** (transcript parts loaded twice) — spec-compliant by design: "count each
  transcript record materialized, including repeated materialization."
- **#17** (CLI doesn't respect `MOHIST_OTEL_DB_PATH`) — minor env-var alignment gap;
  the server's `OtelDb.ResolveDatabasePath` checks it but the CLI's path resolver
  does not. Low impact since operators typically use `--db` explicitly.
- **#21** (`EmbeddedRunnerEnabled` unconditionally false) — pre-existing.
- **#22** (duplicate budget timers in `OtelQueryRoutes` and `TraceQuerier`) — resource
  waste, not a correctness bug; both timers fire on the same 10s budget.
- **#23** (`RuntimeEpoch` double-registration) — intentional pattern; `ApplyPlan`'s
  `AddSingleton` correctly overwrites the DI fallback.
- **#25** (`RuntimeObservability` as a large class) — design observation; the single
  lock is correct and unit tests confirm no contention issues.

## Build and Test

- Build: 0 warnings, 0 errors.
- `Mohist.Server.UnitTests`: 1338 passed, 0 failed.

<promise>PASS</promise>
