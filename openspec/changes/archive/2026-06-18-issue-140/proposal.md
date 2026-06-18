## Why

`TaskRunStatus.Running` is defined but never assigned — tasks jump straight from `Pending` to `Completed`/`Failed`, leaving no observable in-flight state, no dispatch timestamps, and no `TaskStarted` event. Dispatch tracking lives in a separate `WorkLease` persistent state on `WorkflowGrain`, duplicating information that logically belongs on the task. Worse, when a runner dies, `RunnerGrain.HandleTimeoutAsync` clears its local work and goes offline but never notifies any `WorkflowGrain`, so the orphaned task's `WorkLease` sits forever and the work is silently lost. This change completes the task lifecycle, makes `TaskRun` the single source of truth for in-flight dispatch, and closes the runner-lost recovery gap.

## What Changes

- `TaskRun` actually transitions through `Running` on dispatch (today the status is defined but never set).
- `TaskRun` gains `StartedAt`, `FinishedAt`, `RunnerId`, and `WorkId` fields recording dispatch and completion timing.
- A new `TaskStarted` domain event is emitted on the `Pending` → `Running` transition (alongside the existing enriched `TaskCompleted` / `TaskFailed`).
- **BREAKING** (internal grain contract): `WorkLease` persistent state is removed from `WorkflowGrain`. `TaskRun` becomes the single source of truth for in-flight task dispatch. Dispatch recovery (grain reactivation), idempotent dispatch, and result matching all read from `TaskRun.Status == Running` / `TaskRun.WorkId` instead of `_leaseState`.
- `StageCheck` absorbs dispatch metadata (`DispatchWorkId`, `DispatchRunnerId`, `DispatchedAt`) to eliminate the shared `WorkLease` for checks too. Checks keep `Pending → Passed | Failed` — no new `Running` state or events.
- `RunnerGrain.HandleTimeoutAsync` notifies affected `WorkflowGrain`s of abandoned work before going offline, so the grain can transition the `Running` task to `Failed(reason="runner-lost")`.
- Workflow heartbeat acts as a safety net: it checks whether any `Running` task's runner is offline and fails orphans that slipped past the notification.
- No `Cancelled` task state is added (stopped workflows are terminal; merge into `Failed(reason="stopped")`).
- No per-task staleness TTL is added (runner-liveness propagation is the orphan signal, since task durations vary 1000x).

## Capabilities

### New Capabilities

- `orphaned-task-recovery`: Detection and recovery of tasks abandoned by a dead runner. Covers the `RunnerGrain` → `WorkflowGrain` loss notification on heartbeat timeout and the workflow heartbeat safety net that fails `Running` tasks whose runner is offline.

### Modified Capabilities

- `workflow-run`: `TaskRun` gains a real `Running` transition with `StartedAt`/`FinishedAt` timestamps, `RunnerId`/`WorkId` dispatch metadata, and a `TaskStarted` domain event. `WorkLease` persistent state is removed; `TaskRun` becomes the single source of truth for in-flight dispatch (recovery, idempotent dispatch, result matching). `StageCheck` gains dispatch metadata fields for lease-free recovery.

## Impact

- **Domain model** (`Workflow/Domain/Run/`): `TaskRun.cs` (new fields, `Running` transitions), `StageCheck.cs` (dispatch fields), `Shared.cs` (`WorkLease` record removed), `WorkflowEvent.cs` (new `TaskStarted` event).
- **WorkflowGrain** (`Workflow/Grains/WorkflowGrain.cs`): `[PersistentState("lease")]` removed; `RunCoreAsync`, `ReportResultAsync`, `GetCurrentWorkIdAsync`, reactivation restore, and `ClearChecksLeaseAsync`/`SaveLeaseAsync` rewritten to read from `TaskRun`/`StageCheck` dispatch state. Heartbeat (`EnsureWorkHeartbeatAsync`) gains offline-runner check.
- **RunnerGrain** (`Runner/Grains/RunnerGrain.cs`): `HandleTimeoutAsync` notifies affected `WorkflowGrain`s of abandoned work before clearing `_works` and going offline.
- **Persistence**: `WorkLease` Orleans persistent state grain storage usage removed; existing persisted `TaskRun`/`StageCheck` state carries the new fields. Storage migration concerns are limited to in-flight runs at deploy time (orphaned leases are already unrecoverable in the current design).
- **Domain events**: Consumers of `WorkflowEvent` (projections, logs, SSE/event-bus) must handle the new `TaskStarted` variant.
- No changes to runner polling/reporting protocol, check/stage approval model, retry/rerun semantics, or heartbeat reminder mechanism itself.
