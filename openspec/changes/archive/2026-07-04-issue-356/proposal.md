## Why

`design/testing.md` lists "no real wall clock" as a hard constraint and names the
injection seam: C# services take `TimeProvider`, tests use `FakeTimeProvider`.
`SystemUpdateService` is one of the hottest wall-clock readers in the repo and
its time-driven transitions — waiting-for-reconnect → readiness probe,
superseded (running hash drift) judgement, command/log timestamps, failure and
recovery timestamps — currently cannot be exercised deterministically. Any spec
that wants to cover these branches today must resort to real `Task.Delay` /
polling, which is exactly the flaky recipe `design/testing.md` calls out.

This issue is also a prerequisite for issue#02 (fire-and-forget stale-state
self-healing): that reconciler cannot be tested deterministically until the
update service's clock is injectable.

## What Changes

- `SystemUpdateService` gains a `TimeProvider` dependency, following the existing
  `AttachmentService.cs:50` / `WorkflowArtifactUploadService.cs` pattern: the
  public constructor defaults to `TimeProvider.System`, and the `internal`
  test-facing constructor accepts an explicit instance.
- Every `DateTimeOffset.UtcNow` read in the file is replaced with
  `_time.GetUtcNow()`. The issue body cites 16 sites with stale line numbers
  from a longer historical revision; the current file holds **13** call sites
  (creation/superseded/waiting/ready/completed/recovered timestamps in
  `StartAsync`, `AdvanceActiveJobAsync`, `RecordCliOutcomeAsync`,
  `SupersedeStaleWebJobsAsync`, `RunUpdateAsync`, `TryRestoreRunnerAsync`,
  `RunCommandAsync`, `CreateFailedTransition`, and the `ApplyTransitionLog`
  fallback). Each is verified to be a "now" read, not "run elapsed".
- DI registration is unchanged in shape: `SystemUpdateService` stays an
  `ISingletonService` (Scrutor auto-registered) and `TimeProvider.System`
  remains the registered default (`MohistServiceRegistration.cs:89`), so
  production resolves the real clock. The `SystemUpdateServiceSpecs` test
  helpers (`CreateService` / `CreateConsistencyService`) are threaded with a
  `FakeTimeProvider`.
- At least one new or converted spec drives a time-dependent transition via
  `FakeTimeProvider.Advance` (e.g. waiting-for-reconnect readiness retry or
  superseded-on-hash-drift), asserting on the advanced timestamp with **no**
  real waiting.
- No observable behavior change: timestamp formats, state-transition timing, and
  log ordering are identical to today — only the source of "now" changes.

## Capabilities

### New Capabilities

- `system-update-time-injection`: The `SystemUpdateService` obtains all
  timestamps from an injected `TimeProvider` rather than `DateTimeOffset.UtcNow`,
  so its time-driven transitions (waiting-for-reconnect, superseded, ready,
  succeeded, recovered, failed, and the transition-log fallback timestamp) are
  deterministic under a fake clock. Covers the constructor-injection contract,
  the "no `DateTimeOffset.UtcNow` residue in this file" invariant, the default
  `TimeProvider.System` production wiring, and FakeTimeProvider-driven spec
  coverage of at least one time branch.

### Modified Capabilities

- None. No user-visible or system-observable behavior of the update flow
  changes; only the internal source of "now" and its testability.

## Impact

- **Server code**: `packages/server/src/Mohist.Server/SystemInfo/SystemUpdateService.cs`
  — add `TimeProvider` field + constructor parameter, replace all 13 current
  `DateTimeOffset.UtcNow` reads with `_time.GetUtcNow()`.
- **DI / hosting**: No change required. `TimeProvider.System` is already
  registered (`MohistServiceRegistration.cs:89`, `MohistSiloRegistration.cs:55`);
  `SystemUpdateService` remains `ISingletonService`. The public constructor
  hard-codes `TimeProvider.System` (matching `AttachmentService`), so no DI
  resolution of `TimeProvider` is even needed for this service.
- **Tests**: `packages/server/tests/Mohist.Server.Tests/Specs/SystemSpecs/SystemUpdateServiceSpecs.cs`
  — thread `FakeTimeProvider` through `CreateService` /
  `CreateConsistencyService`; add/convert at least one spec using
  `FakeTimeProvider.Advance` to assert a time-driven transition without real
  waiting.
- **Out of scope (non-goals)**: no changes to `ProcessSystemUpdateCommandRunner`
  or `HttpSystemReadinessProbe` internals; no sweep of `DateTimeOffset.UtcNow`
  in other files (`WorkflowRun.*.cs`, `*ProfileManager.cs`, etc.); no rewrite of
  unaffected `SystemUpdateServiceSpecs` cases; no new time abstraction beyond
  the framework `TimeProvider`.
- **Risk**: medium — 13 call sites in the core update flow, each needing a
  semantic ("now" vs "elapsed") check; mitigated by the no-residue grep gate and
  the FakeTimeProvider-driven spec.
