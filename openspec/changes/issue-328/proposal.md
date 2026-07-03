## Why

The service behind `mo update server/runner` crams 5 types (3 interfaces, the
persistence repository, the process executor, the HTTP readiness probe, and the
main service) plus 7 orchestration responsibilities into one 1074-line file,
and its query entry point `GetLatestStatusAsync` silently drives the state
machine — it restarts systemd units, writes job state, and releases file locks
— so a caller cannot tell from the name whether invoking it mutates the world.
Failure handling compounds this: the "construct next state → append log → save
→ maybe release lock" template and a hand-written "mark failed and save" block
are duplicated across 15+ sites even though a `FailAsync` helper already exists,
so each catch block drifts independently.

## What Changes

- Split `SystemUpdateService.cs` into one type (or cohesive type group) per
  file, keeping the `Mohist.Server.SystemInfo` namespace and all existing type
  / member names so DI wiring and the HTTP contract are unchanged:
  - `FileSystemSystemUpdateStore` (file lock + atomic temp-file rename) → its
    own file, alongside the `ISystemUpdateStore` interface.
  - `ProcessSystemUpdateCommandRunner` → its own file, alongside
    `ISystemUpdateCommandRunner` and the `SystemCommandRequest` /
    `SystemCommandResult` records.
  - `HttpSystemReadinessProbe` (health + root + bundled-asset HTML parsing) →
    its own file, alongside `ISystemReadinessProbe` and the
    `SystemReadinessResult` record.
  - `SystemUpdateJobState` (with its `ActiveStatuses` / `TerminalStatuses`
    constants) → its own file.
- `SystemUpdateService` retains only update-job orchestration (start, the
  build → restart-server → wait-for-reconnect pipeline, CLI outcome recording,
  supersession, and runtime-consistency reporting).
- Fix the command/query violation in `GetLatestStatusAsync`: the read path
  becomes read-only. The state-machine advancements it currently performs as
  side effects — superseding a stale `waiting-for-reconnect` job, persisting a
  `waiting-for-reconnect` transition, and (on readiness + hash match)
  restarting the runner and marking the job `succeeded` then releasing the lock
  — move to an explicit command method. The same polling caller that advances
  the job today still advances it; it just does so through a command, not a
  query. State-transition **semantics** (status set, log messages, lock release
  points, ordering) are preserved, only the entry path changes.
- Consolidate the duplicated state-save templates into shared transition
  helpers (extend the existing `FailAsync` / append-log-save pattern): every
  `catch` block and every "build next state → save" site routes through the
  shared method instead of re-implementing the equivalent logic.
- Do **not** touch the already-healthy siblings in the same directory
  (`RuntimeBuildInfo`, `ServiceStatusChecker`, `GitSourceInspector`,
  `PhysicalFileSystem`, `SystemdInstallDetector`, `SystemdUnitParser`,
  `SystemInfoService`).
- Do **not** introduce a cross-platform service-manager abstraction (launchd /
  Windows Service), change the file-lock mechanism, alter the terminal-status
  set, or rewrite state-transition semantics.

## Capabilities

Each becomes a `specs/<name>/spec.md` describing the required behavior for this
change:

- `system-update-service-composition`: The update orchestrator is composed of
  single-responsibility collaborators, each in its own file — the persistence
  repository (`FileSystemSystemUpdateStore` + `ISystemUpdateStore`), the process
  command executor (`ProcessSystemUpdateCommandRunner` +
  `ISystemUpdateCommandRunner` + command records), and the HTTP readiness probe
  (`HttpSystemReadinessProbe` + `ISystemReadinessProbe` + readiness record); the
  `SystemUpdateJobState` model lives separately; the main service retains only
  orchestration. The split preserves namespaces, type names, and DI
  registration, and leaves the already-healthy sibling types untouched.
- `system-update-status-read`: The status query entry point is a pure read —
  it does not restart services, persist job state, or release locks. All
  state-machine advancements that readiness polling used to trigger as query
  side effects (supersede stale jobs, persist the waiting transition, restart
  the runner, mark succeeded, release the lock) occur only on an explicit
  command path, with transition semantics and ordering preserved.
- `system-update-job-transitions`: Repeated state-transition side effects —
  building the next job state, appending a log entry, persisting, and
  optionally releasing the lock, plus the "mark failed and save" pattern — are
  consolidated into shared transition helpers. `catch` blocks reuse the failure
  handler rather than reconstructing failed state inline, so there is exactly
  one definition of each transition.

## Impact

- **Server (C#)**: `packages/server/src/Mohist.Server/SystemInfo/SystemUpdateService.cs`
  is decomposed into multiple files in the same directory/namespace; the
  `SystemUpdateService` class shrinks to orchestration. The CQS violation at
  `SystemUpdateService.cs:436-525` (`GetLatestStatusAsync`) is resolved by
  extracting its advancement branches into a command method. Duplicated failure
  / save templates at `SystemUpdateService.cs:416-425`, `:846-853`,
  `:1008-1017`, `:920-933` collapse onto the existing `FailAsync`
  (`SystemUpdateService.cs:1039`) and a shared save-transition helper.
- **DI / hosting**: No wiring change — `ISingletonService` and the collaborator
  registrations in `MohistServiceRegistration` keep working because type names,
  namespaces, and constructor surfaces are unchanged.
- **HTTP API / data**: No public contract change. The polling endpoint still
  reflects job progress; the advancements it used to perform as hidden side
  effects now run on the command path the polling loop calls, with identical
  status set, log content, and lock-release points.
- **Tests**: `packages/server/tests/Mohist.Server.Tests/Specs/SystemSpecs/SystemUpdateServiceSpecs.cs`
  (and any CLI/runner update specs) must pass unchanged — this is a behavior-
  preserving refactor. New spec coverage will assert the read path is free of
  the former side effects and that transitions route through shared helpers.
- **Dependencies**: None added or removed. Linux + systemd assumption retained.
