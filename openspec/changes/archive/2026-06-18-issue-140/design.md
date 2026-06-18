## Context

The workflow engine uses two Orleans grains to coordinate task execution:

- **WorkflowGrain** — owns the `WorkflowRun` domain aggregate (persisted as JSON via `WorkflowRunStore` to a single DB row with ETag concurrency). It also maintains a **separate** `[PersistentState("lease")] WorkLease` that tracks the currently dispatched work item (workId, workType, runnerId, full `WorkDispatch`).
- **RunnerGrain** (`[Reentrant]`) — tracks runner online/offline status, heartbeat, and `_works` (assigned/running work items keyed by owner+workId).

**Current dispatch flow:** `RunCoreAsync` checks the lease for in-flight work. If empty, it calls `NextWork()` → `PrepareWorkAsync` → `MakeDispatchAsync`, which builds a `WorkDispatch` and writes it into `_leaseState`. The dispatch is assigned to the runner via `RunnerGrain.AssignWorkAsync`.

**Current result flow:** `ReportResultAsync` matches the incoming `(runnerId, workId)` against `_leaseState.State`, clears the lease, and processes the task/check result.

**Problem:** `TaskRunStatus.Running` is defined but never assigned — tasks jump `Pending → Completed/Failed` with no observable in-flight state, no timestamps, and no `TaskStarted` event. The `WorkLease` duplicates dispatch info that belongs on the task. And when a runner dies (`HandleTimeoutAsync`), it clears `_works` and goes offline without notifying any `WorkflowGrain`, leaving orphaned leases forever.

The `EventCatalog` already defines `ReverseDns.TaskStarted` and `ReverseDns.RunnerDisconnected` constants, simplifying wiring.

## Goals / Non-Goals

**Goals:**
- `TaskRun` transitions through `Running` on dispatch, with `StartedAt`/`FinishedAt` timestamps, `RunnerId`/`WorkId` dispatch metadata, and a `TaskStarted` domain event.
- Eliminate `WorkLease` persistent state; `TaskRun` (and `StageCheck` dispatch fields) become the single source of truth for in-flight dispatch.
- Close the runner-lost gap: `HandleTimeoutAsync` notifies affected workflows; heartbeat safety net catches misses.
- Preserve all existing workflow behavior (dispatch, result processing, retry, rerun, feedback loop, repair scheduling).

**Non-Goals:**
- No `Cancelled` task state (stopped workflows are terminal; `Failed(reason="stopped")` suffices).
- No per-task staleness TTL (runner-liveness propagation is the orphan signal).
- No `Running` state or new events for `StageCheck` (dispatch metadata only).
- No changes to runner polling/reporting protocol, check/stage approval model, or heartbeat reminder mechanism.
- No runner-lost notification for `AgentJob`-owned work (separate concern, same pattern can be applied later).

## Decisions

### D1: Dispatch metadata on TaskRun/StageCheck, not a separate lease

**Choice:** Store `RunnerId`, `WorkId`, `StartedAt`, `FinishedAt` on `TaskRun`; store `DispatchWorkId`, `DispatchRunnerId`, `DispatchedAt` on `StageCheck`. Remove the `WorkLease` record and `[PersistentState("lease")]` injection entirely.

**Rationale:** `TaskRun` is already persisted in the `WorkflowRun` JSON blob (single DB row via `_runStore`). The lease was a second Orleans persistent state with its own write path (`WriteStateAsync`), creating a dual-write consistency problem — a grain crash between `SaveRunAsync` and `SaveLeaseAsync` could leave them out of sync. Consolidating onto `TaskRun` removes the second write path entirely.

**Alternative considered:** Keep the lease but also set `Running` on `TaskRun`. Rejected because it maintains the duplication and dual-write problem the issue is meant to solve.

**Alternative considered:** Store the full `WorkDispatch` JSON on `TaskRun` for exact re-assignment. Rejected — the dispatch is regenerable (see D2), and persisting a large rendered-variable blob per running task is wasteful.

### D2: Reactivation recovery re-dispatches rather than restoring a stored dispatch

**Choice:** On grain reactivation, `RunCoreAsync` scans the current stage for `Running` tasks (or checks with `DispatchWorkId != null`). For each in-flight item, it checks runner liveness:
- **Runner alive:** re-assign the work by regenerating the dispatch via `PrepareWorkAsync`. For tasks, `workId` is deterministic (`= taskRunId`, e.g. `"build-impl.1"`), so regeneration produces the same id — `RunnerGrain.AssignWorkAsync` is idempotent by work key. For checks, the stored `DispatchWorkId` is reused (passed as an override to `MakeDispatchAsync`).
- **Runner offline:** fail the task as `runner-lost` (tasks) or clear dispatch metadata and re-queue (checks).

**Rationale:** The current `RestoreDispatch(WorkLease)` returns the full `WorkDispatch` stored in the lease. Without the lease, we regenerate it. This works because:
1. If the runner is alive and already executing → re-assignment is a no-op (runner deduplicates by work key).
2. If the runner is alive but lost its `_works` (RunnerGrain reactivation) → the regenerated dispatch lets it re-poll and pick up the work.
3. If the runner is dead → fail/re-queue.

The dispatch payload (variables, prompts) may differ slightly from the original if config changed between dispatch and reactivation. This is acceptable: a live runner ignores the re-assignment (already executing), and a dead runner's task is failed regardless.

**Alternative considered:** Store the full `WorkDispatch` on `TaskRun` for exact restoration. Rejected per D1 — adds a large blob and the regeneration path is sufficient.

**Alternative considered:** Don't re-assign on reactivation; rely solely on heartbeat safety net. Rejected — if the RunnerGrain reactivated and lost `_works`, the task would be stuck indefinitely because the heartbeat only checks runner *online* status, not whether the runner has the work assignment.

### D3: Runner-lost notification via new `NotifyRunnerLostAsync(runnerId)` grain method

**Choice:** Add `Task NotifyRunnerLostAsync(string runnerId)` to `IWorkflowGrain`. `RunnerGrain.HandleTimeoutAsync` collects distinct `WorkflowRunId`s from `_works` (where `OwnerKind == Workflow`), calls `NotifyRunnerLostAsync` on each, then clears `_works` and goes offline. The workflow grain scans its `Running` tasks for the given `runnerId` and fails matches as `runner-lost`.

**Rationale:** The runner already knows which workflows it holds work for (via `_works`). Passing just the `runnerId` (not individual work IDs) is simpler — the workflow already knows its own tasks and their `RunnerId` fields. The workflow handles only its own tasks; no cross-grain work-id matching needed.

**Method shape — notify per-runner vs per-work-item:** Per-runner is chosen because:
1. The workflow scans its stage once for all matching Running tasks.
2. It's idempotent — a second notification for the same runner finds no Running tasks (already failed).
3. Fewer grain calls (one per affected workflow, not one per work item).

**Alternative considered:** Broadcast a `RunnerDisconnected` event on the event bus; workflows subscribe. Rejected — Orleans grains don't naturally subscribe to bus events without additional infrastructure. The `ReverseDns.RunnerDisconnected` constant exists for future bus-level use, but direct grain-to-grain call is simpler and lower-latency for this issue.

**Alternative considered:** Workflow polls `RunnerGrain.IsAvailableAsync` on every heartbeat for every Running task. Rejected as *primary* path (scales poorly — N workflows × M runners per tick), but used as safety net (see D4).

**Partial-failure handling:** Notifications are best-effort. If one `NotifyRunnerLostAsync` call throws, `HandleTimeoutAsync` logs the error and continues to the next workflow. The heartbeat safety net (D4) catches any workflow that missed the notification.

### D4: Heartbeat safety net checks runner liveness for Running tasks

**Choice:** The existing heartbeat reminder (`EnsureWorkHeartbeatAsync`, 1-minute period) gains a runner-liveness check. For each `Running` task in the current stage, it calls `RunnerGrain.IsAvailableAsync(task.RunnerId)`. If offline, the task is failed as `runner-lost`.

**Rationale:** The heartbeat already fires on a timer and calls `RunCoreAsync`. Adding a liveness check is a minimal extension — same reminder, same cadence, one extra `IsAvailableAsync` call per Running task. This catches cases where `HandleTimeoutAsync`'s notification was lost or the runner grain was destroyed before notifying.

**Why only Running tasks (not dispatched checks):** Checks don't have a `Running` state — their in-flight signal is `DispatchWorkId != null && Status == Pending`. Check recovery happens on grain reactivation (D2). If the grain stays activated and a check's runner dies silently (notification lost), the check remains stuck until grain reactivation. This is an acceptable edge case — the primary notification path (D3) covers the common case. Extending the heartbeat to checks is a trivial future addition if needed.

**Alternative considered:** Separate reminder for liveness probes. Rejected — unnecessary complexity; the existing reminder cadence (1 min) is sufficient given the runner heartbeat timeout (2 min).

### D5: No Cancelled state — merge into Failed

**Choice:** Stopped workflows produce `Failed(reason="stopped")` for any in-flight task, not a separate `Cancelled` status.

**Rationale:** Stopped workflows are terminal. The only recovery is `RerunAsync` (full restart, discards all task state). `RetryAsync` only works on `Failed` workflows (`WorkflowGrain.cs:1341`). A `Cancelled` state would have zero behavioral difference from `Failed(reason="stopped")`. If resume-after-stop is added in the future, `Cancelled` can be introduced as a backward-compatible enum addition.

### D6: Runner-liveness propagation, not per-task staleness TTL

**Choice:** Orphan detection uses the existing `RunnerGrain` heartbeat timeout (2 minutes) as the signal. No per-task "max duration" threshold.

**Rationale:** Task durations vary 1000x — a health check runs in seconds, an implementation task runs 20-60 minutes. A single TTL cannot correctly classify both. Runner liveness is the correct signal: if the runner is alive and heartbeating, the task is presumed alive (agent hangs are handled by runner liveness probes — `probeTimeoutMs`, `livenessQuietThresholdMs`).

## Implementation Plan

### Area 1: Domain model (`Workflow/Domain/Run/`)

**`TaskRun.cs`:**
- Add nullable fields: `StartedAt`, `FinishedAt` (`DateTimeOffset?`), `RunnerId`, `WorkId` (`string?`).
- These are `set` properties (mutable, like `Status`), set during lifecycle transitions.

**`WorkflowRun.Task.cs`:**
- Add `StartTask(string workId, string runnerId)` extension: sets `Status = Running`, `StartedAt = DateTimeOffset.UtcNow`, `WorkId`, `RunnerId`, emits `TaskStarted(stage, taskId, runnerId)`.
- Modify `CompleteTask()`: set `task.FinishedAt = DateTimeOffset.UtcNow` before the existing `Completed` transition.
- Modify `FailTask()`: set `task.FinishedAt` before the existing `Failed` transition.
- Add `FailTaskForRunnerLost()`: fails the Running task with reason `"runner-lost"`, sets `FinishedAt`, emits `TaskFailed` + `StageFailed` + `WorkflowRunFailed`.

**`StageCheck.cs`:**
- Add nullable fields: `DispatchWorkId`, `DispatchRunnerId` (`string?`), `DispatchedAt` (`DateTimeOffset?`).

**`WorkflowEvent.cs`:**
- Add `TaskStarted` to the `WorkflowEvent` union.
- Add `public sealed record TaskStarted(string Stage, string TaskId, string RunnerId);`.

**`Shared.cs`:**
- Remove the `WorkLease` record and `[GenerateSerializer]` attribute.

### Area 2: Event serialization (`Infrastructure/Events/`)

**`WorkflowEventSerializer.cs`:**
- Add `TaskStarted => EventCatalog.ReverseDns.TaskStarted` to `BusType` switch.
- Add `nameof(TaskStarted) => data.Deserialize<TaskStarted>(JsonOptions)!` to `FromData` switch.
- Add `TaskStarted x => x` to `Unwrap` switch.

(The `EventCatalog.ReverseDns.TaskStarted` constant already exists.)

### Area 3: WorkflowGrain refactor (`Workflow/Grains/WorkflowGrain.cs`)

**Constructor:** Remove `[PersistentState("lease")] IPersistentState<WorkLease> leaseState` parameter and `_leaseState` field.

**`OnActivateAsync`:** Read `_lastRunnerId` from `_run?.Claim?.RunnerId` only (remove `_leaseState.State?.RunnerId` fallback).

**`RunCoreAsync` (the core refactor):** Replace the lease-check block:
```
// OLD: check _leaseState.State.WorkId → restore dispatch
// NEW: scan current stage for Running tasks or dispatched checks
```
- If a `Running` task exists:
  - Check `RunnerGrain.IsAvailableAsync(task.RunnerId)`.
  - If alive: reconstruct `WorkflowWork.Task` from `TaskRun` fields, call `PrepareWorkAsync` to regenerate dispatch, re-assign via `AssignRunnerWorkAsync`. (workId is deterministic for tasks.)
  - If offline: call `FailTaskForRunnerLost`, commit events, continue.
- If a dispatched check exists (`DispatchWorkId != null, Status == Pending`):
  - If runner alive: re-assign using stored `DispatchWorkId`.
  - If offline: clear `DispatchWorkId`/`DispatchRunnerId`/`DispatchedAt`, let `NextWork()` re-dispatch.
- Otherwise: proceed with normal `NextWork()` dispatch.

**`MakeDispatchAsync`:** Remove `_leaseState.State = new WorkLease(...)` line. Instead, after building the dispatch:
- For `workType == "task"`: call `run.StartTask(workId, runnerId)` to set Running + metadata + emit TaskStarted.
- For checks: set `DispatchWorkId`, `DispatchRunnerId`, `DispatchedAt` on the matching `StageCheck`.
- Accept an optional `workIdOverride` parameter for check re-dispatch on reactivation.

**`ReportResultAsync`:** Replace lease matching:
- For tasks: find the `Running` task whose `WorkId == workId && RunnerId == runnerId`. If no match, return (ignore stale result).
- For checks: find the check whose `DispatchWorkId == workId && DispatchRunnerId == runnerId`.
- Remove `ClearAndDeleteLeaseAsync()` call. For task completion: `CompleteTask()`/`FailTask()` already set terminal status + `FinishedAt`. For checks: clear `DispatchWorkId`/`DispatchRunnerId`/`DispatchedAt` on result processing.

**`GetCurrentWorkIdAsync`:** Return the `Running` task's `WorkId`, or the dispatched check's `DispatchWorkId`.

**`GetActiveWorkAsync`:** Reconstruct `WorkflowActiveWorkView` from the `Running` task or dispatched check instead of lease fields.

**Remove:** `SaveLeaseAsync()`, `ClearAndDeleteLeaseAsync()`, `ClearChecksLeaseAsync()`, `RestoreDispatch(WorkLease)`. Simplify `ClearExecutableStateAsync` (no lease to clear; only release stage locks).

**`SaveRunAsync` calls:** Remove `SaveLeaseAsync()` calls after `SaveRunAsync()`. The run JSON now includes all dispatch state.

**Heartbeat (`EnsureWorkHeartbeatAsync` / `ReceiveReminder`):** After the existing `RunCoreAsync` call (or before it), add: for each `Running` task in the current stage, if `!await RunnerGrain.IsAvailableAsync(task.RunnerId)`, fail the task as `runner-lost` via `FailTaskForRunnerLost`.

**Add `NotifyRunnerLostAsync(string runnerId)`:** Scan current stage for `Running` tasks with `RunnerId == runnerId`. For each, call `FailTaskForRunnerLost`. Commit events. Idempotent — no-op if no matching tasks.

**`On(WorkflowEvent)` handler:** Add `TaskStarted => EnsureWorkHeartbeatAsync()` (task started → workflow is runnable → ensure heartbeat).

### Area 4: RunnerGrain (`Runner/Grains/RunnerGrain.cs`)

**`HandleTimeoutAsync`:** Before clearing `_works`:
1. Collect distinct `WorkflowRunId`s from `_works` where `Dispatch.OwnerKind == WorkDispatchOwnerKinds.Workflow`.
2. For each, call `GrainFactory.GetGrain<IWorkflowGrain>(workflowRunId).NotifyRunnerLostAsync(RunnerId)`.
3. Best-effort: wrap each call in try/catch, log failures, continue.
4. Then proceed with existing `_works.Clear()` + offline + unregister.

**`UnregisterAsync`:** Apply the same notification pattern (graceful shutdown may leave work behind if the runner didn't report all results). Same best-effort approach.

## Risks / Trade-offs

- **[Re-dispatch on reactivation may produce slightly different variables]** → Mitigation: if the runner is alive and executing, re-assignment is a no-op (idempotent by work key). If the runner lost the work, a slightly different dispatch is acceptable — the task is being re-executed anyway. The workId is deterministic for tasks, so the runner's dedup logic works.

- **[Dual-write window during migration]** → At deploy time, in-flight runs have `WorkLease` state but no `Running` task status (old code never set it). After deploy, the grain reactivates, finds no `Running` task, and the lease is gone (removed from constructor). The task appears `Pending` and will be re-dispatched. This is correct — the old lease was already unreliable for runner-lost detection. No data migration needed; orphaned leases at deploy time are unrecoverable in the current design regardless.

- **[Notification lost between HandleTimeoutAsync and workflow]** → Mitigation: heartbeat safety net (D4) catches orphans within 1-2 minutes. The heartbeat checks `RunnerGrain.IsAvailableAsync`, which returns offline after the 2-minute heartbeat timeout.

- **[RunnerGrain reactivation loses `_works`, workflow doesn't know]** → Mitigation: on WorkflowGrain reactivation, D2 re-assigns the work. If the WorkflowGrain doesn't reactivate but the RunnerGrain does, the heartbeat safety net won't catch it (runner is "online" but lost the work). This is an existing limitation — the current lease-based design has the same gap. Future improvement: RunnerGrain could notify workflows on reactivation if it finds no tracked work for a known assignment.

- **[Check stuck if notification lost and grain doesn't reactivate]** → Mitigation: rare edge case (both notification and grain reactivation must fail). Primary notification path covers the common case. Heartbeat check for dispatched checks can be trivially added later.

- **[`On(WorkflowEvent)` handler missing TaskStarted case]** → The switch expression in `WorkflowEventSerializer` and the `On()` handler must both handle `TaskStarted`. Missing either causes a runtime exception. Mitigation: compile-time exhaustiveness checking (C# switch expressions on unions warn on missing cases).

## Migration Plan

1. **Deploy order:** Single deploy. The `WorkflowGrain` constructor change (removing `[PersistentState("lease")]`) and the domain model changes ship together. No phased rollout needed because:
   - Old persisted runs deserialize fine: new nullable fields on `TaskRun`/`StageCheck` default to `null`; old `Running`-never-set tasks are `Pending` and will be re-dispatched.
   - The `WorkLease` grain storage table becomes orphaned (no code reads it). It can be cleaned up via Orleans storage cleanup at convenience.
   - The `TaskStarted` event is new; old event consumers ignore unknown CloudEvent types (or fail gracefully per existing patterns).

2. **Rollback:** Revert the deploy. Old code resumes. Runs that were dispatched with the new code (task in `Running` state with `WorkId`/`RunnerId` set) will be seen by old code — old code ignores these fields (they're not in the old `TaskRun` model) and treats the task as `Pending` (since old code never checks for `Running` in `NextWork()`). Wait — old `NextWork()` uses `CurrentTask()` which returns the first non-completed task. A `Running` task is not `Completed`, so it would be returned as pending work and re-dispatched. This is safe — the task gets re-dispatched and the old lease mechanism takes over.

3. **In-flight runs at deploy:** Tasks mid-execution will be re-dispatched when the grain reactivates with new code. The runner's `AssignWorkAsync` deduplicates by work key, so a live runner ignores the duplicate. The task continues and reports its result normally.

## Open Questions

1. **Should `NotifyRunnerLostAsync` also handle AgentJob-owned work?** The current design scopes to `WorkDispatchOwnerKinds.Workflow`. AgentJob grain already has its own liveness model. If needed, the same pattern applies — add `NotifyRunnerLostAsync` to `IAgentJobGrain`.

2. **Should the heartbeat safety net also clear orphaned dispatched checks?** The issue limits the heartbeat to `Running` tasks. Extending to checks is trivial but out of scope. If check stickiness becomes observed in practice, add it.

3. **Should `TaskStarted` carry the `WorkId`?** Currently specified as `TaskStarted(Stage, TaskId, RunnerId)`. The `WorkId` equals the `TaskId` for tasks (deterministic), so it's derivable. If checks ever get events, `CheckStarted` would need a separate `WorkId` since check workIds are Guid-based.
