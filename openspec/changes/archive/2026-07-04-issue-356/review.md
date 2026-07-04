# Review Report

## Result: PASS

## Repaired Items

None.

## Blocking Items

None.

## Follow-up Items

None.

## Pre-existing or Out-of-scope Items

- [ID: item-1]
  Severity: warning
  Scope: `npm test` / `packages/runner/tests/runner-host-task-log.spec.ts`
  Evidence: Full `npm test` verification completed the .NET solution phase, then failed in the runner workspace on `RunnerHost task-log best-effort flush (T-003) > RoutesConcurrentIncrementalUploadsToEachWorkItemCollector` with a 5000ms Vitest timeout. This issue changes only `packages/server/src/Mohist.Server/SystemInfo/SystemUpdateService.cs` and `packages/server/tests/Mohist.Server.Tests/Specs/SystemSpecs/SystemUpdateServiceSpecs.cs`; no runner files or package test configuration are touched, so this is out of scope for the reviewed candidate.
  SuggestedAction: Track and stabilize the runner test separately; rerun `npm test` after that fix if full-repo green is required.
  Status: out-of-scope

- [ID: item-2]
  Severity: info
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/SystemSpecs/SystemUpdateServiceSpecs.cs`
  Evidence: The spec file still contains pre-existing fixture-time uses of `DateTimeOffset.UtcNow`, including the `CreateInfo` helper at line 1667 and several persisted-state setup timestamps. The reviewed change meets the issue requirement because `SystemUpdateService.cs` itself has zero `DateTimeOffset.UtcNow` reads and the new time-sensitive specs inject and advance `FakeTimeProvider`; the remaining test wall-clock seeds are not introduced by this change and are not asserted as relative time. They are still inconsistent with the broader `design/testing.md` preference for fixed fixture timestamps.
  SuggestedAction: In a separate cleanup, replace non-essential test fixture `UtcNow` seeds with fixed constants or fake-clock values.
  Status: pre-existing

- [ID: item-3]
  Severity: warning
  Scope: web dependency audit output during focused .NET test build
  Evidence: `dotnet test Mohist.sln --filter FullyQualifiedName~SystemUpdateServiceSpecs` triggered the web build and npm reported 9 dependency vulnerabilities (3 moderate, 3 high, 3 critical). This candidate does not change package manifests or lockfiles, and no exposed secrets or new injection surface were found in the reviewed server change.
  SuggestedAction: Triage `npm audit` in a dependency maintenance issue.
  Status: out-of-scope

## Acceptance Criteria Review

- `SystemUpdateService` now declares `private readonly TimeProvider _time` and assigns it from the constructor path (`packages/server/src/Mohist.Server/SystemInfo/SystemUpdateService.cs:17`, `:27`, `:31-49`).
- The public constructor delegates with `TimeProvider.System`, while the internal constructor accepts an explicit `TimeProvider` as the final parameter (`SystemUpdateService.cs:19-39`). Production DI shape remains unchanged; `SystemUpdateService` is still conventionally registered as an `ISingletonService`, and the existing `TimeProvider.System` registrations remain at `MohistServiceRegistration.cs:89` and `MohistSiloRegistration.cs:55`.
- All current service timestamp reads are sourced from `_time.GetUtcNow()` (`SystemUpdateService.cs:68`, `:127`, `:150`, `:167`, `:185`, `:212`, `:300`, `:497`, `:561`, `:642`, `:649`, `:711`, `:727`). `rg -n "DateTimeOffset\.UtcNow" packages/server/src/Mohist.Server/SystemInfo/SystemUpdateService.cs` returned zero matches.
- The two former static helper paths are instance methods and use `_time`: `CreateFailedTransition` at `SystemUpdateService.cs:702-722` and `ApplyTransitionLog` at `SystemUpdateService.cs:724-733`. `ApplyTransitionLog` preserves the existing precedence of explicit `timestamp`, then `logEntry.At`, then current time.
- The spec helpers construct and pass `FakeTimeProvider` through the internal constructor (`SystemUpdateServiceSpecs.cs:1622-1652`, `:1675-1708`).
- New deterministic specs advance or set the fake clock and assert persisted timestamps without real sleeps: `AdvanceActiveJobAsync_WaitingForReconnectTransition_RecordsAdvancedClockAsUpdatedAt` (`SystemUpdateServiceSpecs.cs:468-507`) and `AdvanceActiveJobAsync_SupersededOnHashDrift_RecordsAdvancedClockAsCompletedAt` (`SystemUpdateServiceSpecs.cs:590-630`).

## Verification

- `rg -n "DateTimeOffset\.UtcNow" "packages/server/src/Mohist.Server/SystemInfo/SystemUpdateService.cs"` -> zero matches.
- `dotnet test Mohist.sln --filter FullyQualifiedName~SystemUpdateServiceSpecs` -> passed, 47 passed, 0 failed, 0 skipped.
- `dotnet test Mohist.sln -p:SkipWebBuild=true` -> passed, 3767 passed, 13 skipped, 0 failed.
- `git diff --check master...HEAD` -> passed with no whitespace errors.
- `npm test` -> out-of-scope failure in runner workspace as documented in item-1; the relevant .NET phase completed before the runner failure.

<promise>PASS</promise>
