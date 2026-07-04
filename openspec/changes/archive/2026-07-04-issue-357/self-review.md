# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: `tasks.json` T-001 `output` referenced `packages/server/src/Mohist.Server/SystemInfo/ISystemUpdateStore.cs` as a separate file, but no such file exists — `ISystemUpdateStore` is declared inside `FileSystemSystemUpdateStore.cs:7`. The description also said "the in-memory fake store" (singular), while there are two test fakes implementing `ISystemUpdateStore` (`InMemoryUpdateStore` at `SystemUpdateServiceSpecs.cs:1740` and `OrderTrackingStore` at `:1711`); both must implement the new method or the build breaks.
  Verification: `glob **/ISystemUpdateStore.cs` returns no matches; `grep ": ISystemUpdateStore"` confirms the interface is co-located and lists all three implementers. Corrected the `output` to name the real file and enumerate every fake that needs the new method.
  Status: resolved

## Blocking Items

_None._

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: feasibility
  Evidence: `design.md` Open Questions flags that the default `IProcessStartTimeProvider` should prefer a .NET 11 API (e.g. `Environment.ProcessStartTime`-style) over `Process.GetCurrentProcess().StartTime.ToUniversalTime()` if one exists. This is correctly deferred to implementation time and does not affect the spec/contract, but the implementer of T-002 should resolve it before writing `ProcessStartTimeProvider`.
  SuggestedAction: During T-002, check for a .NET 11 process-start-time API; otherwise fall back to `Process.GetCurrentProcess().StartTime.ToUniversalTime()` as the design states. Behavioral outcome is identical either way.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: consistency
  Evidence: `design.md` D6 says the reconciler's explicit `AddHostedService` registration "matches `EpicReconciliationService` / `AttachmentCleanupService` / `StagePopulationSnapshotService`", and D1 chooses a direct `IHostedService` (explicitly NOT `BackgroundService`). Those three referenced services are `BackgroundService` subclasses (`EpicReconciliationService.cs:34`, `AttachmentCleanupService.cs:6`, `StagePopulationSnapshotService.cs:50`). The "matches" wording is about the registration call (`AddHostedService`, confirmed at `MohistServiceRegistration.cs:81-87`), not the base class, so the decision is internally consistent — but a reader could briefly confuse the two.
  SuggestedAction: Optional wording tweak in D6 to say "registered via `AddHostedService` like …" rather than "matches …" to avoid the base-class confusion. Non-blocking.
  Status: follow-up

## Review Notes

- **Alignment**: Every "What Changes" / Capability entry in `proposal.md` traces to an issue acceptance criterion. The proposal's addition of `ReleaseStaleLockAsync` is a *necessary* extension of the issue's "release the lock" requirement: verified against `FileSystemSystemUpdateStore.cs:59-82` that even after marking the job `failed`, `TryCreateLockFile` (`:71`, `FileMode.CreateNew`) would still fail on the lingering `.lock` file, and `ReleaseLockAsync` (`:84-100`) no-ops post-restart because `_lockOwnerJobId` is process-local. Without this, the issue's self-heal goal would not be met.
- **Completeness**: All issue sub-requirements ((a) stale→failed+lock, (b) fresh untouched, (c) terminal untouched, plus no-op and injectable-time) are covered 1:1 by spec scenarios in `specs/stale-update-job-recovery/spec.md`, and every requirement has a task.
- **Consistency**: Capability name `stale-update-job-recovery` matches the spec directory. Task `spec` anchors resolve to the `### Requirement:` headings in the spec file. Type/method names (`SystemUpdateRecoveryService`, `IProcessStartTimeProvider`, `ReleaseStaleLockAsync`, reason literal `"interrupted by process restart"`) are identical across proposal/design/specs/tasks. Verified the issue's stale line refs (`SystemUpdateService.cs:405`) were corrected to real current locations (`:86` Task.Run, `:51` StartAsync, `:474` RunUpdateAsync, `:522` finally).
- **Feasibility**: Two tasks, each a complete vertical slice (T-001: store capability + fakes + tests; T-002: abstraction + reconciler + registration + spec). No over-fine "define interface / register DI / add test" tasks; tests are inlined in the implementing tasks. Line references in design (`ReleaseLockFile` at `:174-189`, `TimeProvider.System` at `MohistServiceRegistration.cs:89`, hosted services at `:81-87`) all verified accurate. The `SourceAudit_ReleaseLockAsyncOnlyInSharedHelpersAndRunUpdateFinally` test audits only `SystemUpdateService.cs` source (`SystemUpdateServiceSpecs.cs:1591-1596`), so a new `ReleaseStaleLockAsync` call in a new `SystemUpdateRecoveryService.cs` file will not trip it — the design's claim holds.
- **Dependency completeness**: T-001 `dependsOn: []` priority 1; T-002 `dependsOn: ["T-001"]` priority 2. Target IDs exist, priorities strictly increase, no cycle.

<promise>PASS</promise>
