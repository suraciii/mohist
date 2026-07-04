# Review Report

## Result: FAIL

## Repaired Items

- None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/server/src/Mohist.Server/SystemInfo/SystemUpdateService.cs
  Evidence: The final success transition now sets `CompletedAt` from `completedAt` at lines 182-190, then calls `PersistTransitionAsync` with no log entry at lines 183-192. `ApplyTransitionLog` stamps no-log transitions with a fresh `DateTimeOffset.UtcNow` at lines 719-727, so the persisted `succeeded` state now has `UpdatedAt` later than `CompletedAt`. The pre-change code assigned both fields from the same `completedAt` value, and the issue/spec require transition semantics, response timestamps, and persisted data shape to remain behavior-preserving. This affects the public status payload because both timestamps are returned by `ToResponse`. [disallowed:product-behavior-change]
  SuggestedAction: Preserve the old timestamp semantics for no-log terminal transitions, for example by letting the transition helper accept an explicit timestamp or by preserving a caller-supplied `UpdatedAt` when no log entry is appended. Add a regression spec that the ready -> succeeded transition persists equal `UpdatedAt` and `CompletedAt`.
  Verification: Run `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj -p:SkipWebBuild=true --no-build --filter FullyQualifiedName~AdvanceActiveJobAsync_WhenReady_RestartsRunnerBeforeReadyCompletion` plus the full system-update spec class.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: verification / packages/server/tests/Mohist.Server.Tests/Specs/SystemSpecs/SystemUpdateServiceSpecs.cs
  Evidence: The candidate does not currently demonstrate the required "all existing system update spec pass" acceptance criterion in aggregate. `npm test` timed out after 120s during the .NET test phase. A focused aggregate run, `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj -p:SkipWebBuild=true --no-build --filter FullyQualifiedName~SystemUpdateServiceSpecs --logger console;verbosity=normal`, timed out after 300s twice. Narrower subsets and individual tests passed, which points to an aggregate-suite hang or leaked async work rather than a compile failure.
  SuggestedAction: Identify the spec or background update task that prevents the full `SystemUpdateServiceSpecs` filtered run from completing, then make the aggregate class run and `npm test` finish reliably.
  Verification: `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj -p:SkipWebBuild=true --no-build --filter FullyQualifiedName~SystemUpdateServiceSpecs --logger console;verbosity=normal` and `npm test` both complete without timeout.
  Status: open

- [ID: item-3]
  Severity: test-gap
  Scope: packages/server/tests/Mohist.Server.Tests/Specs/SystemSpecs/SystemUpdateServiceSpecs.cs
  Evidence: The status-read spec explicitly requires readiness-failure deduplication: when the persisted stage/reason already match the readiness failure, polling must append no log and perform no persist. The current tests cover changing readiness failures at lines 391-421 and a mismatched existing reason at lines 505-535, but there is no test where the stored `Stage` is `Waiting for reconnect` and the stored `Reason` exactly equals the readiness failure reason, with an assertion that no new saved state/log entry is produced.
  SuggestedAction: Add a focused spec that seeds a waiting job with the same stage/reason returned by the readiness probe, calls `AdvanceActiveJobAsync` or `GetStatusEnvelopeAsync`, and asserts the store did not save another state and the log count is unchanged.
  Verification: Run the new focused spec and the full `SystemUpdateServiceSpecs` class.
  Status: open

- [ID: item-4]
  Severity: test-gap
  Scope: packages/server/tests/Mohist.Server.Tests/Specs/SystemSpecs/SystemUpdateServiceSpecs.cs
  Evidence: `SourceAudit_AppendLogSaveAsyncSequenceOnlyInSharedHelper` at lines 1383-1395 is effectively vacuous. Its regex searches for an inline `AppendLog(...)` immediately followed by `_store.SaveAsync(...)`, but the refactored source intentionally separates `AppendLog` into `ApplyTransitionLog` and `_store.SaveAsync` into `PersistTransitionAsync`. The test does not assert that any match exists, so it would pass even if the shared-helper structure changed in a way that no longer proves the intended consolidation.
  SuggestedAction: Replace this audit with assertions that `AppendLog` is only called from the intended helper path and that all transition saves flow through `PersistTransitionAsync`, or remove the vacuous test and rely on non-vacuous behavioral specs.
  Verification: Run `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj -p:SkipWebBuild=true --no-build --filter FullyQualifiedName~SourceAudit`.
  Status: open

## Follow-up Items

- None.

## Pre-existing or Out-of-scope Items

- [ID: item-5]
  Severity: info
  Scope: packages/server/src/Mohist.Server/SystemInfo/SystemUpdateService.cs
  Evidence: The update state machine still relies directly on `DateTimeOffset.UtcNow` throughout the service. This was explicitly called out as out of scope in the design, so it is not a blocker for this review.
  SuggestedAction: Consider a later `TimeProvider` pass if timestamp behavior needs deterministic unit-level coverage.
  Status: out-of-scope

<promise>FAIL</promise>
