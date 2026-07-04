# Review Report

## Result: FAIL

## Repaired Items

_None._

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/SystemInfo/SystemUpdateRecoveryService.cs`
  Evidence: The reconciler persists the recovered job as terminal `failed` before releasing the stale lock (`SaveAsync` at lines 81, then `ReleaseStaleLockAsync` at line 82). If the process is cancelled/crashes after the save, or stale-lock deletion silently fails in `FileSystemSystemUpdateStore.ReleaseLockFile` (`IOException` is swallowed at lines 195-204), the next boot sees a terminal job and returns early (`TerminalStatuses.Contains` at lines 56-57). The stale `.lock` file can then remain forever even though the latest job is terminal, and `TryAcquireLockAsync` will still fail at lock-file creation. This preserves the exact wedge the issue is trying to eliminate, just in a terminal-state variant. [disallowed:behavior/data-safety]
  SuggestedAction: Make recovery atomic or retryable with respect to lock removal. For example, avoid committing the terminal state until the matching stale lock is actually removed, or add a narrowly scoped startup cleanup path for a terminal latest job whose lock file still names that same job. Add a temp-file regression where recovery is interrupted or lock deletion fails after the failed state is saved, then verify the next startup can still free the lock and a new update can start.
  Verification: `npm test` currently passes, but no test covers the terminal-state-plus-stale-lock failure mode.
  Status: open

- [ID: item-2]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/SystemSpecs/SystemUpdateRecoverySpecs.cs`
  Evidence: The new recovery specs use real wall-clock/process data despite the issue requiring fake time plus injected process start time, and `design/testing.md` lines 65-90 explicitly forbids real wall-clock fixture timestamps. New examples include `DateTimeOffset.UtcNow.AddMinutes(-5)` at line 186, the default-provider test reading the real current process and comparing to `DateTimeOffset.UtcNow` at lines 214-218, and `CreateSystemUpdateService` seeding `RunningInfo.StartedAt` with `DateTimeOffset.UtcNow` at line 259. [disallowed:test-design]
  SuggestedAction: Replace fixture timestamps with fixed constants or the existing `FakeTimeProvider`. Remove or refactor `ProcessStartTimeProvider_DefaultReadsActualProcess` so specs do not read real process information or assert against the wall clock; source-audit or DI-registration tests can verify the production default is wired without making the spec timing-dependent.
  Verification: Re-run `npm test`; additionally grep the new recovery spec for `DateTimeOffset.UtcNow`, `DateTime.UtcNow`, and direct default `ProcessStartTimeProvider()` usage.
  Status: open

- [ID: item-3]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/SystemInfo/SystemUpdateRecoveryService.cs`
  Evidence: Recovery appends a failed log entry with a local `AppendLog` helper that never enforces the existing 200-entry cap (`SystemUpdateRecoveryService.cs` lines 91-98), while `SystemUpdateService.AppendLog` caps to `MaxLogEntries = 200` (`SystemUpdateService.cs` lines 735-740). A stale job already at the cap will be persisted with 201 entries after recovery. [disallowed:behavior]
  SuggestedAction: Apply the same log cap on recovery, preferably through a shared helper or a local constant with a test that starts from 200 logs and verifies the recovery log is retained while the total remains capped.
  Verification: Add and run a focused recovery spec for a stale active job with 200 existing log entries.
  Status: open

- [ID: item-4]
  Severity: cleanup
  Scope: `packages/server/src/Mohist.Server/SystemInfo/FileSystemSystemUpdateStore.cs`
  Evidence: `ReleaseStaleLockAsync` is exposed as an async API but blocks synchronously with `_gate.Wait(cancellationToken)` at line 105 and then returns `Task.CompletedTask`. Neighboring store methods use `await _gate.WaitAsync(cancellationToken)`. This is small, but it is an unnecessary sync-over-async style mismatch on startup I/O code. [disallowed:cleanup-only]
  SuggestedAction: Make `ReleaseStaleLockAsync` an `async Task` method and use `await _gate.WaitAsync(cancellationToken)` to match the rest of the store.
  Verification: Re-run `npm test` after the change.
  Status: open

## Follow-up Items

_None._

## Pre-existing or Out-of-scope Items

- [ID: item-5]
  Severity: warning
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/SystemSpecs/SystemUpdateServiceSpecs.cs` and neighboring system specs
  Evidence: The surrounding system specs already contain many `DateTimeOffset.UtcNow` fixture timestamps. These were not introduced by this issue except where noted in item-2, but they conflict with the repository testing guide's fixed-time preference and make it easier for new specs to copy the same pattern.
  SuggestedAction: Clean these up separately by moving system-update fixtures onto fixed constants and injected `FakeTimeProvider` values.
  Status: pre-existing

## Verification

- Read issue 357 via `mo issue show 357 --project-id proj_f6c141d63b6243bfbb481737b2243b87`.
- Reviewed workflow artifacts under `openspec/changes/issue-357/` and all changed product/test files from `git diff master...HEAD`.
- Ran `npm test`: the first run completed the .NET suite but hit the 120s timeout during workspace tests; reran with a 300s timeout and the command completed successfully. Server result: 3794 passed, 13 skipped. Runner/Web workspace Vitest summary: 65 files passed, 908 tests passed.
- Ran `git diff --check master...HEAD`: no whitespace errors reported.

<promise>FAIL</promise>
