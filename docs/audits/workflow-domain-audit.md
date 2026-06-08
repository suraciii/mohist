# Workflow Domain Audit

> Scope: `packages/server/src/Mohist.Server/Workflow/` and its persistence layer (`packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/`). Cross-references to `EventCatalog`, `CloudEventFactory`, `RunnerGrain`, `IssueGrain` are kept only where they directly affect workflow correctness.
>
> Audit method: read every Workflow domain file in full, cross-checked emit sites and bus subscriptions via `codegraph_context` / `codegraph_explore` / `codegraph_search`, read the default profile yaml and the relevant spec tests in `tests/Mohist.Server.Tests/Specs/Workflow/`.
>
> Out of scope (audited by other agents): `EventBus` plumbing, SignalR hub, Issue state machine, Project grain, AgentSession, Runner protocol details beyond what WorkflowGrain needs.

## Summary table

| # | Severity | Title | File |
|---|----------|-------|------|
| 1 | **P0** | `lease_expired` event is emitted but never consumed; workflow can deadlock if runner crashes | `Workflow/Grains/WorkflowGrain.cs:105-130` |
| 2 | **P0** | Approval-rejection re-run path is the only recovery and has no spec coverage | `Workflow/Grains/WorkflowGrain.cs:203-229` + `Workflow/Domain/Run/WorkflowRun.Approval.cs:29-48` + `WorkflowRun.Failure.cs:54-72` |
| 3 | **P0** | Reverse-DNS workflow events (TaskCompleted, CheckPassed, RepairScheduled, etc.) are not in `EventCatalog.All`; SignalR bridge never forwards them to the Web | `Infrastructure/Data/Workflow/WorkflowRunStore.cs:102-115` + `Infrastructure/Events/EventCatalog.cs:14-71` |
| 4 | **P0** | `lease_expired` and most domain events emit without `projectid` extension; `EventBridge.ForwardToHub` falls back to `project:global` group | `Workflow/Grains/WorkflowGrain.cs:119-129` + `Infrastructure/Data/Workflow/WorkflowRunStore.cs:102-115` |
| 5 | **P1** | `OnDeactivateAsync` flushes run JSON but drops the in-flight events list — silent event loss on silo shutdown | `Workflow/Grains/WorkflowGrain.cs:69-84` |
| 6 | **P1** | `RetryFailedCheck(null)` throws if check is not currently `Failed` — no idempotency on rapid user clicks | `Workflow/Domain/Run/WorkflowRun.Stage.cs:143-153` |
| 7 | **P1** | `AddRuntimeTask` silently cancels a pending `AwaitingApproval` request | `Workflow/Domain/Run/WorkflowRun.Work.cs:54-86` |
| 8 | **P1** | `OnDeactivateAsync` swallows non-concurrency save exceptions → in-memory mutations lost on next activation | `Workflow/Grains/WorkflowGrain.cs:69-84` |
| 9 | **P1** | ETag optimistic-lock is a "versioned audit trail" (per its own test), not real concurrency protection; misleading API | `Infrastructure/Data/Workflow/WorkflowRunStore.cs:62-78` + `tests/.../WorkflowRunStoreSpecs.cs:22-26` |
| 10 | **P1** | No spec for `Stop`/`Rerun` while `AwaitingApproval`; UI may offer a "rerun" while approval still pending | `Workflow/Grains/WorkflowGrain.cs:181-229` + `WorkflowRun.Lifecycle.cs:87-93` |
| 11 | **P1** | `WorkflowRunFailed` / `StageFailed` do not carry a `runnerid` extension; downstream trace from bus to the runner that actually failed is impossible | `Infrastructure/Data/Events/WorkflowEventPersistence.cs:12-43` + `Workflow/Grains/WorkflowGrain.cs:1115-1142` |
| 12 | **P2** | `OnWorkflowCompletedAsync` does not emit `stage_changed action="completed"`; UI has no terminal transition notification | `Workflow/Grains/WorkflowGrain.cs:1033-1036` |
| 13 | **P2** | `WorkflowEventPersistence.StageAsync` uses `MAX(Id)+1` for event IDs; safe only by virtue of grain-serialised saves — fragile | `Infrastructure/Data/Events/WorkflowEventPersistence.cs:21-24` |
| 14 | **P2** | Heartbeat reminder `5s due / 60s period` vs. `5 min` lease timeout → up to `6 min` detection latency for a stuck lease | `Workflow/Grains/WorkflowGrain.cs:20-22, 95` |
| 15 | **P2** | `WorkflowRunRow.MetadataProjectId` is `[DatabaseGenerated(Computed)]` but no trigger or computed expression exists | `Infrastructure/Data/Workflow/WorkflowRunRow.cs:15-16` |
| 16 | **P2** | `WorkflowRunRow` carries no `ProjectId` index for tenant scoping; large-scale `WorkflowRuns` table will have no project-side filtering | `Infrastructure/Data/Workflow/WorkflowRunRow.cs` |
| 17 | **P2** | ETag increment on every save doubles write traffic even when nothing material changed | `Infrastructure/Data/Workflow/WorkflowRunStore.cs:77` |
| 18 | **P2** | `RunnerDisconnected` event lacks `projectid`; SignalR fans it to global group | `Runner/Grains/RunnerGrain.cs:229-237` |
| 19 | **P2** | `GetCurrentWorkIdAsync` performs a DB read on every call (no cache on the in-memory `_lease`) | `Workflow/Grains/WorkflowGrain.cs:441-445` |
| 20 | **P2** | `WorktreeCleanupService` only listens for `WorkflowRunCompleted`; a Stopped/Failed workflow leaves the worktree behind until manual cleanup | `Issue/Services/WorkflowProfiles/WorktreeCleanupService.cs:41` |
| 21 | **P3** | `TryRequestApproval` (WorkflowRun.Stage.cs:99-128) is a misleading name — it's the post-advance status calculator with dead branches | `Workflow/Domain/Run/WorkflowRun.Stage.cs:99-128` |
| 22 | **P3** | `Advance` uses `while (current.Status == Completed)` where only one iteration is possible | `Workflow/Domain/Run/WorkflowRun.Stage.cs:49-65` |
| 23 | **P3** | `TaskStarted` / `CheckStarted` / `WorkflowRunRetrying` / `WorkflowRunRerunning` are declared in `EventCatalog.ReverseDns` but never emitted | `Infrastructure/Events/EventCatalog.cs:85-100` |
| 24 | **P3** | `EmitStageChanged` produces `"Unknown"` status when `CurrentStageId` is null (e.g. between `Create` and `Start`) | `Workflow/Grains/WorkflowGrain.cs:923-956` |
| 25 | **P3** | `DispatchLifecycleHooksAsync<T>` is dead code kept "for any callers" — none exist | `Workflow/Grains/WorkflowGrain.cs:1060-1066` |
| 26 | **P3** | `JsonElementSurrogate` re-parses on every grain-call deserialisation | `Workflow/Grains/Surrogates/JsonElementSurrogate.cs:13-21` |
| 27 | **P3** | Default profile's `plan` stage declares `self-review` as a task + `self-review-passed` as a `core/marker` check — both must succeed, but there is no "review is required if-and-only-if self-review task produced a marker" guard | `Issue/Services/WorkflowProfiles/mohist-default.workflow.yaml:49-85` |
| 28 | **P3** | `WorkflowRun.Failure` is not cleared on `Retry` for `ApprovalRejected` (it short-circuits to a throw), so a stale failure detail survives a subsequent `Rerun` flow | `Workflow/Domain/Run/WorkflowRun.Failure.cs:24-72` |

---

## P0 findings (must fix)

### P0-1. `lease_expired` is fire-and-forget; no workflow recovery

- **Where**: `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:95-130`
- **What**:
  - `CheckLeaseAgeAsync` runs on the 1-min reminder; when a lease is older than 5 min it only calls `_eventBus.Emit(... LeaseExpired ...)`.
  - The lease is NOT cleared. `_lease.DispatchedAt` is not updated. The work is not re-dispatched. The workflow status is not transitioned. The grain still believes a runner is working.
  - The docstring on line 97-104 promises: "*the workflow can re-dispatch or surface a task-failed transition*". Neither happens.
  - There is no `OnLeaseExpired` handler in this grain, and `grep -r LeaseExpired packages/server/src/Mohist.Server` shows zero subscribers (only the `WorkflowGrain` definition site and `EventCatalog.ReverseDns` constant).
  - Failure mode: a runner process dies after picking up work but before reporting. The workflow sits in `Running` with an in-flight lease forever. The Web UI shows the stage as "running" indefinitely. The runner's agent session is in a permanent "running" state too (only `RunnerDisconnected` clears it, and that requires the runner's heartbeat to time out, which is a different code path).
- **Fix**:
  1. Have `CheckLeaseAgeAsync` synchronously transition the in-flight lease to "expired": clear `_lease`, persist a new `WorkflowRunFailed(FailureReason.LeaseExpired)` (or add a new `FailureReason.LeaseExpired`), and call `EnsureWorkHeartbeatAsync()` to re-dispatch.
  2. Keep the `bus.Emit(LeaseExpired)` for downstream AgentSession fan-out, but make the local state machine the primary recovery mechanism.
  3. Add a `FailureReason.LeaseExpired` enum value and route it through `WorkflowStatusMapper.BuildAvailableActions` (so the UI can offer `retry`).
- **Test gap**: There is NO spec for the lease-timeout path. `WorkflowLeaseActivationSpecs` covers activation/restoration, not the 5-min no-report path. Add a test that injects a stale lease (`DispatchedAt = DateTime.UtcNow - 6 minutes`) and asserts the workflow transitions to `Failed` and re-dispatches.

### P0-2. Approval-rejection recovery is rerun-current-stage with no spec coverage

- **Where**:
  - `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:203-229` (`RejectAsync`, `RerunAsync`)
  - `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Approval.cs:29-48` (`Reject` sets `FailureReason.ApprovalRejected`, status `Failed`)
  - `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Failure.cs:54-72` (`Rerun` creates a new `StageRun` with `Attempt+1`)
  - `packages/server/src/Mohist.Server/Workflow/Services/WorkflowStatusMapper.cs:91-117` (`BuildAvailableActions` only adds `rerun` for `ApprovalRejected`, not `retry`)
- **What**:
  - The AGENTS.md note in the user's brief explicitly mentions this as a known audit finding: "驳回和失败都重跑当前 stage, 跨阶段回退待修". It is by design, but the design is "rejection behaves the same as a generic failure" — the user is forced to manually click "Rerun stage" to continue.
  - The issue: if the audit requirement is "an approval rejection must FAIL the audit, not silently re-run", then the current behaviour is wrong (it succeeds by being re-runnable). If the requirement is "rejection is recoverable via rerun", then:
    - The recovery path is untested. `ApprovalGateSpecs` only has `AwaitingApproval_UserRejects_WorkflowFails` (the fail, not the recovery).
    - `Rerun` replaces the `StageRun` with a fresh one (no `Tasks`, no `Checks`, no `ApprovalStatus`). The new stage will re-initialize via the `StageInit` work and re-run all tasks from scratch — which means the rejection reason is lost (the `Failure.Message` is on the OLD stage, replaced). The user has no record of "why was this rejected last time" when re-running.
  - The `Rerun` for `ApprovalRejected` does NOT call `TryScheduleRequestedCheckRepairAsync` first (only `Retry` does). So if the rejection was caused by a failed check that has a repair config, the user has no shortcut to "just retry the check with a repair task".
- **Fix**:
  1. Add spec `ApprovalGateSpecs.RejectedApproval_Rerun_RestartsStageFromScratch` (positive test for the recovery).
  2. Persist the rejection reason on the new `StageRun` (e.g. `LastRejectionReason`) so the next attempt is informed.
  3. If the audit really requires that rejection be terminal, change `Reject` to set `WorkflowRunStatus.Cancelled` instead of `Failed` and remove the `rerun` action.
- **Test gap**: zero coverage of the rerun-after-reject path. The full approval lifecycle is "approve → continue" / "reject → fail" with no third leg.

### P0-3. Reverse-DNS workflow events are invisible to the SignalR/Web bridge

- **Where**:
  - `packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/WorkflowRunStore.cs:102-115` (`Publish` emits reverse-DNS names like `com.mohist.workflow.task.completed`)
  - `packages/server/src/Mohist.Server/Infrastructure/Events/EventCatalog.cs:14-71` (`All` array, used by `EventBridge.StartAsync` to subscribe)
  - `packages/server/src/Mohist.Server/Events/Hub/EventBridge.cs:29-37` (subscribes only to `EventCatalog.All`)
- **What**:
  - The 17 reverse-DNS workflow events (`WorkflowRunStarted`, `StageStarted`, `TaskCompleted`, `TaskFailed`, `CheckPassed`, `CheckFailed`, `CheckPending`, `RepairScheduled`, etc.) are emitted by `WorkflowRunStore.Publish` but their types (`com.mohist.workflow.*`) are NOT in `EventCatalog.All`.
  - The `EventBridge` only subscribes to the 56 snake_case names in `EventCatalog.All` (which are the legacy names) plus 0 reverse-DNS names. So `EventBridge.ForwardToHub` never fires for `TaskCompleted` / `CheckPassed` / `RepairScheduled` / etc.
  - The only event that crosses the bridge end-to-end is the legacy `stage_changed` (emitted by `WorkflowGrain.EmitStageChanged`).
  - Effect: the Web UI cannot show per-task progress, per-check pass/fail, repair-scheduled notifications, or stage-started transitions in real time. It can only see the coarse `stage_changed` events.
  - Note: `IssueGrain` does subscribe directly via `_eventBus.OnType(EventCatalog.ReverseDns.WorkflowRunCompleted, ...)` (line 74-76 of `Issue/Grains/IssueGrain.cs`), so the issue-status side-effect still works. But the Web (via SignalR) is blind.
- **Fix**:
  - Add the missing reverse-DNS names to `EventCatalog.All` (or, preferably, iterate both arrays in `EventBridge.StartAsync`).
  - Better: have a single source of truth — `EventCatalog.ReverseDns.*` constants — and have `EventCatalog.All` be the union of legacy + reverse-DNS names.
- **Test gap**: `WorkflowEventApiSpecs` tests the API surface (returns DB rows) but not the SignalR bridge. There is no test that asserts "after a task completes, the Web receives a `TaskCompleted` SignalR event".

### P0-4. `lease_expired` and reverse-DNS events lack `projectid` extension

- **Where**:
  - `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:119-129` (`CheckLeaseAgeAsync` emit)
  - `packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/WorkflowRunStore.cs:102-115` (`Publish` emit)
  - `packages/server/src/Mohist.Server/Events/Hub/EventBridge.cs:50-75` (`ExtractProjectId` falls back to `"global"`)
- **What**:
  - `CloudEventFactory.Create` takes `projectId` as a parameter and sets `evt["projectid"]` (line 49). It also lifts `projectid` from any `IProjectScoped` payload (line 29-32).
  - `CheckLeaseAgeAsync` builds the `extraExtensions` dict manually and does NOT pass `projectId`. The resulting event has no `projectid` attribute.
  - `WorkflowRunStore.Publish` passes `workflowRunId: runId` but never `projectId`. The data payload is the deserialised `WorkflowEvent` record, which does NOT implement `IProjectScoped`. So no lift happens.
  - The `EventBridge.ExtractProjectId` iterates extensions; finding none, it returns null and routes the event to the `project:global` group. All users on all projects see every workflow's task/check events.
- **Fix**:
  1. In `CheckLeaseAgeAsync`, get `projectId` from `_variables?.String("project", "id")` and pass it to `CloudEventFactory.Create` (or via `extraExtensions`).
  2. In `WorkflowRunStore.Publish`, the `WorkflowRun` has `Metadata.Annotations["projectId"]` (set by `BuildRunMetadata`, line 1084 of `WorkflowGrain.cs`). Extract it from the row state in the store and pass it to `CloudEventFactory.Create`.
  3. Even better: have `WorkflowEventPersistence.StageAsync` store the `projectId` in a queryable column so `Publish` doesn't have to re-parse the JSON.
- **Test gap**: no test asserts that any emitted event carries the `projectid` extension.

---

## P1 findings (should fix)

### P1-1. `OnDeactivateAsync` drops in-flight events

- **Where**: `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:69-84`
- **What**:
  - `OnDeactivateAsync` calls `await _runStore.SaveAsync(_run, ct)` (the no-events overload, line 36-41 of `WorkflowRunStore.cs`). This persists the JSON state but does NOT call `WorkflowEventPersistence.StageAsync` and does NOT call `Publish`.
  - If the grain is mid-`CommitAsync` (which calls `SaveAsync(_run, events)` and then `Publish`) and gets deactivated (e.g. silo shutdown for an update), the events that were about to be persisted may be in `_run`'s state but the bus emit is lost.
  - The `On(e)` side-effects in `CommitAsync` (lines 972-974) are ALSO lost — `EnsureWorkHeartbeatAsync` / `DisableWorkHeartbeatAsync` / lock releases are not invoked.
- **Fix**: in `OnDeactivateAsync`, if `_runDirty`, also flush the events list. Refactor: maintain an `_pendingEvents` list, append on every transition, flush in both `CommitAsync` and `OnDeactivateAsync`.
- **Test gap**: no test simulates silo shutdown mid-`CommitAsync`.

### P1-2. `RetryFailedCheck(null)` is not idempotent

- **Where**: `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Stage.cs:143-153`
- **What**:
  - `WorkflowRun.Retry` (line 24-52) handles `FailureReason.CheckUnrepaired` by calling `current.RetryFailedCheck(current.Failure.CheckName)`. If the check is no longer in `Failed` status (e.g. a previous retry already moved it to `Pending`, or a different retry path was used), the `FirstOrDefault` returns null and the `?? throw` raises `WorkflowDomainException`.
  - In `RetryAsync` (WorkflowGrain.cs:211-219), the user may click "retry" twice in quick succession. The first click calls `TryScheduleRequestedCheckRepairAsync` which schedules a repair (status → Running). The second click calls `Retry` which tries `RetryFailedCheck` — but the check may be back in `Pending` or `Passed` state from a re-run. The exception propagates to the user as a 500.
- **Fix**: make `RetryFailedCheck` idempotent — if the check is already `Pending` or `Passed`, return without error. Or guard `RetryAsync` at the grain level: check `current.Failure` matches current state before calling `Retry`.

### P1-3. `AddRuntimeTask` silently cancels a pending approval

- **Where**: `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Work.cs:54-86`
- **What**:
  - When the user is waiting on approval (`stage.IsAwaitingApproval == true`), adding a runtime task via `AddRuntimeTask` (or `AddTasksAsync`) sets `stage.ApprovalStatus = null` (line 79-80) and clears `current.Failure` (line 78). The pending approval is silently cancelled.
  - The user (or an admin script) does not get a "this will cancel the pending approval" warning. The audit trail shows the approval was requested but never resolved.
- **Fix**:
  - Either: require an explicit `cancel-approval` flag, or refuse to add a task while `AwaitingApproval` unless `cancel-approval: true` is passed.
  - Emit an `ApprovalCancelled` event for traceability.

### P1-4. Non-concurrency save exceptions are swallowed on deactivation

- **Where**: `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:69-84`
- **What**:
  - `OnDeactivateAsync` catches `Exception` (line 78) and just logs. In-memory mutations are lost.
  - For example: if a workflow is in `Paused` state, the user calls `StopAsync`, the `CommitAsync` succeeds, but a few seconds later the silo is shutting down. `OnDeactivateAsync` flushes. If the flush fails (DB connection lost, disk full, etc.), the in-memory `_run` has `Status=Stopped` but the DB still has `Status=Paused`. On next activation, the grain reloads and the user's "Stop" is lost. The workflow continues running.
- **Fix**:
  - Don't catch `Exception` — only catch transient errors and retry.
  - On deactivation, schedule a background retry if the save fails.
  - At minimum, emit a `WorkflowStateLost` event so an external reconciler (e.g. `IssueWorkflowReconciliationService`) can detect the drift.

### P1-5. ETag optimistic-lock is a no-op

- **Where**:
  - `packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/WorkflowRunStore.cs:62-78`
  - `packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/WorkflowRunRow.cs:6-16` (no `[ConcurrencyCheck]` on `ETag`, it's a shadow property)
  - `tests/Mohist.Server.Tests/Specs/Workflow/Grain/WorkflowRunStoreSpecs.cs:22-26` (the test comment explicitly says: "Real concurrency protection comes from the Orleans grain single-thread model; ETag here is just a versioned audit trail.")
- **What**:
  - The ETag shadow property is incremented on every save (`entry.Property<long>("ETag").CurrentValue = entry.Property<long>("ETag").OriginalValue + 1`). Because EF Core does not have `[ConcurrencyCheck]` on the property, no actual concurrency check happens.
  - If two transactions somehow read the same row (e.g. an out-of-band migration script updates the row), both will silently succeed with the same ETag value. No `DbUpdateConcurrencyException` is raised.
  - The test at line 20-74 explicitly verifies that no concurrency exception is thrown even when an external mutator changes the ETag. The mechanism is a "versioned audit trail", not a lock.
- **Fix**:
  - Either: add `[ConcurrencyCheck]` to the ETag property in `WorkflowRunRow` so EF Core enforces it, and update the store to throw on conflict.
  - Or: remove the ETag entirely and document that concurrency safety comes from grain serialisation.
  - The current state (audit-trail-only) is misleading — the API surface suggests a lock.

### P1-6. No spec for `Stop` / `Rerun` while `AwaitingApproval`

- **Where**:
  - `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:181-193` (`StopAsync` allows `Running or Paused`, NOT `AwaitingApproval` — bug?)
  - `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Lifecycle.cs:87-93` (`Stop` allows `Running or Paused`)
  - `packages/server/src/Mohist.Server/Workflow/Services/WorkflowStatusMapper.cs:91-117` (`AvailableActions` for `AwaitingApproval` only offers `approve` and `reject`)
- **What**:
  - When a workflow is `AwaitingApproval`, the user can only approve or reject. There is no "cancel the approval and rerun" action. The AGENTS.md note confirms this is by design.
  - But: the `WorkflowStatusMapper.BuildAvailableActions` for `Failed` with `ApprovalRejected` adds `rerun`. For `AwaitingApproval`, no `stop` action is offered. So if a user wants to abandon a workflow that is stuck in approval, they have no UI path. The only option is to wait, or to call the API directly.
  - `StopAsync` rejects `AwaitingApproval` (throws `WorkflowDomainException`). So the user is locked out.
- **Fix**:
  - Add a "cancel approval" action to `BuildAvailableActions` for `AwaitingApproval` (calls a new `CancelApprovalAsync` that clears `ApprovalStatus` and sets status back to `Running`).
  - Or allow `StopAsync` for `AwaitingApproval` (transitions to `Stopped`).
  - Add a spec test for whichever path is chosen.

### P1-7. Failure events lack `runnerid` / `workid` for traceability

- **Where**:
  - `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Check.cs:73-94` (`FailCheck` returns `[new CheckFailed(...), new StageFailed(...), new WorkflowRunFailed(...)]`)
  - `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Task.cs:24-40` (`FailTask` similar)
  - `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Failure.cs:11-22` (`FailStage` similar)
  - `packages/server/src/Mohist.Server/Infrastructure/Data/Events/WorkflowEventPersistence.cs:12-43` (stages events without runner context)
- **What**:
  - All `*Failed` events carry only the stage name, check name, or task ID. They do not carry the runner ID that produced the failure.
  - When a workflow fails in production, the operator has to cross-reference the event log with the runner's session log to find which runner (and which host) was responsible. This is doable but slow.
  - The `WorkflowGrain` knows the runner ID (`_lease.RunnerId` is still set when `ReportResultAsync` is called, and the runner's ID is in the message). It just doesn't propagate it to the event.
- **Fix**: extend `CheckFailed`, `TaskFailed`, `StageFailed`, `WorkflowRunFailed` records to include an optional `RunnerId` field, and pass it from `ProcessTaskResult` / `ProcessCheckResultAsync`.

### P1-8. Retry emits ambiguous events; the `WorkflowRunResumed` from `ScheduleCheckRepair` is not tied to a check

- **Where**:
  - `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Check.cs:38-56` (`ScheduleCheckRepair` returns `[new RepairScheduled(...), new WorkflowRunResumed()]`)
  - `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Failure.cs:24-52` (`Retry` returns `[new WorkflowRunResumed()]`)
- **What**:
  - When a repair is injected, two events fire: `RepairScheduled` (good) AND `WorkflowRunResumed`. But the `WorkflowRunResumed` is misleading — the run was never "Paused", it was "Failed". The Resume implies a pause/resume cycle that never happened.
  - The semantics for downstream subscribers (e.g. `WorktreeCleanupService`, `IssueGrain`) are unclear: should they treat this as a fresh resume or as a re-entry from a failed state?
- **Fix**: rename to `WorkflowRunRepairing` or `WorkflowRunRetrying` (the `EventCatalog.ReverseDns` already declares `WorkflowRunRetrying` and `WorkflowRunRerunning` but neither is emitted).

---

## P2 findings (could improve)

### P2-1. `OnWorkflowCompletedAsync` does not emit `stage_changed`

- **Where**: `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:1033-1036`
- **What**: every other terminal transition (`failed`, `stopped`, `approved`, `rejected`, `paused`, `resumed`, `started`) emits a `stage_changed` event. `completed` does not. The Web UI sees the workflow disappear from "Running" without an explicit terminal notification (it must infer completion from the status change in a later poll).
- **Fix**: add `EmitStageChanged("completed")` to `OnWorkflowCompletedAsync`.

### P2-2. Event ID is `MAX+1` per source

- **Where**: `packages/server/src/Mohist.Server/Infrastructure/Data/Events/WorkflowEventPersistence.cs:21-24`
- **What**: `await db.Events.Where(e => e.Source == source).Select(e => (long?)e.Id).MaxAsync(ct) ?? 0) + 1` is a read-then-write. Safe today because grain serialisation makes the read and write a single critical section. Fragile if the events are ever inserted from outside the grain (e.g. an admin tool replaying events).
- **Fix**: use a SQL sequence per source, or include the source in the primary key (`Source`, `Id`).

### P2-3. Heartbeat reminder cadence vs lease timeout

- **Where**: `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:20-22, 95`
- **What**: due time 5 s, period 60 s, lease timeout 5 min. The reminder fires at 5 s, 65 s, 125 s, 185 s, 245 s, 305 s after activation. The lease is detected as expired between 305 s and 365 s of age — a 60 s detection window. For a 5-min lease timeout this is fine, but it's a documented magic number. There is no env-var override.
- **Fix**: lift `LeaseTimeout`, `WorkHeartbeatReminderDueTime`, `WorkHeartbeatReminderPeriod` to configuration.

### P2-4. `WorkflowRunRow.MetadataProjectId` is `[DatabaseGenerated(Computed)]` with no DB expression

- **Where**: `packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/WorkflowRunRow.cs:15-16`
- **What**: the column is declared computed. No migration or trigger populates it. `WorkflowProfileManager.ResolveRunContextAsync` reads it but it's always null. It is dead code in the schema.
- **Fix**: either add a SQL trigger / migration to populate it, or remove the column.

### P2-5. `WorkflowRuns` table has no project-side index

- **Where**: `packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/WorkflowRunRow.cs:6-16`
- **What**: only `[Key] WorkflowRunId` is indexed. All queriers (`IssueQuerier` at line 188-195, `WorkflowProfileManager.ResolveRunContextAsync` at line 100-101) filter by `WorkflowRunId` first, so the index is used. But the table will be O(issues) in size, and any future "list all runs for a project" query will full-scan.
- **Fix**: add a secondary index on `MetadataProjectId` once that column is populated (P2-4).

### P2-6. ETag bump on every save is wasteful

- **Where**: `packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/WorkflowRunStore.cs:77`
- **What**: even `SaveAsync(_run)` with no events bumps the ETag. The ETag value is included in the JSON column on the next save. No external consumer reads the ETag, so the bump is a no-op audit trail.
- **Fix**: remove the ETag entirely, or only bump it when the JSON actually changed (compare hashes).

### P2-7. `RunnerDisconnected` lacks `projectid`

- **Where**: `packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs:229-237`
- **What**: same root cause as P0-4. The runner grain knows its `ProjectId` (line 59) but doesn't pass it to `CloudEventFactory.Create`. The EventBridge routes to `project:global`.
- **Fix**: pass `_projectId` to the factory.

### P2-8. `GetCurrentWorkIdAsync` does a DB read on every call

- **Where**: `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:441-445`
- **What**: `_lease` is already in memory (cached after first restore). `GetCurrentWorkIdAsync` calls `RestoreLeaseAsync` which calls `_leaseStore.LoadAsync` if `_lease is null` — but for the steady-state case where `_lease` is set, it just returns `_lease?.WorkId`. Actually, looking more carefully, `RestoreLeaseAsync` only loads from DB if `_lease is null`. So if `_lease` is in memory, no DB read. ✓. False alarm — this is fine.

### P2-9. `WorktreeCleanupService` only cleans on `Completed`

- **Where**: `packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/WorktreeCleanupService.cs:41`
- **What**: a `Stopped` or `Failed` workflow leaves its worktree behind. If the user re-runs the workflow later (after fixing the failure), a new worktree is created (or the old one is reused — unclear). The current behaviour: stopped/failed worktrees accumulate.
- **Fix**: also subscribe to `WorkflowRunStopped` and `WorkflowRunFailed`, OR add a periodic sweeper for stale worktrees.

### P2-10. `EventBridge` lacks tests for filter routing

- **Where**: `packages/server/src/Mohist.Server/Events/Hub/EventBridge.cs:50-75`
- **What**: the bridge's `ExtractProjectId` falls back to `"global"` when no extension is set. There is no test asserting that events with `projectid` go to the correct project group and events without it go to global.
- **Fix**: add a spec test that subscribes to the hub, emits a `WorkflowEvent` with and without `projectid`, and asserts the routing.

### P2-11. `WorkflowRun.ScheduleCheckRepair` resets ALL checks, not just the failed one

- **Where**: `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Stage.cs:155-180` (the `extension(StageRun)` block)
- **What**: `ScheduleCheckRepair` iterates over `stage.Checks` and resets every check to `Pending` (line 172-177). This is correct for the "fix the whole stage" semantics, but it means a repair of check A re-runs checks B, C, D even if they already passed.
- **Fix**: only reset the failed check, OR document the "reset-all" behaviour explicitly in the YAML profile semantics.

### P2-12. `StageDefinition` `Resources` are not validated

- **Where**: `packages/server/src/Mohist.Server/Workflow/Domain/Definition/WorkflowDefinition.cs:22-29`
- **What**: `Resources` is `List<string>?`. No validation that they are non-empty, non-duplicate, or that the `LockBehavior` is one of `{sequential, parallel, ...}`. The string is compared in `GetSequentialLockResourceAsync` (line 514 of WorkflowGrain.cs) using case-insensitive `"sequential"`. Any other value is silently treated as "no lock".
- **Fix**: enum `LockBehavior { Sequential, Parallel }`; validate at YAML parse time.

### P2-13. `RunnerGrain` does not subscribe to `LeaseExpired` either

- **Where**: `packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs` (no subscription)
- **What**: even if P0-1 is fixed to emit `LeaseExpired` as a real workflow event, the `RunnerGrain` does not listen for it. A runner that has a stuck agent session (the runner process is alive, the agent inside is dead) will not be told to clean up the session.
- **Fix**: subscribe in `RunnerGrain.OnActivateAsync` (or `RunnerRegistry`) to `EventCatalog.ReverseDns.LeaseExpired` filtered by `RunnerId` extension.

---

## P3 findings (nitpicks)

### P3-1. `TryRequestApproval` is misleadingly named

- **Where**: `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Stage.cs:99-128`
- **What**: the method is a "post-advance status calculator" with four branches (request approval, set Failed, preserve AwaitingApproval, set Completed/Running). The name suggests it only handles approval. The fourth branch (line 117-126) is dead code in practice (it's only reached when `ApprovalStatus is not null` AND `Result is not null` — which means it was already resolved, in which case we wouldn't be re-calculating).
- **Fix**: rename to `RecalculateStageStatus` and remove the dead branch.

### P3-2. `Advance` uses `while` for what is effectively `if`

- **Where**: `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Stage.cs:49-65`
- **What**: the loop body always sets `current.Status = Running` on entry to a new stage, so the loop condition is false after one iteration. The "while" is misleading.
- **Fix**: replace with a tail-recursive call or `if` with a `return Advance()`.

### P3-3. Dead `EventCatalog.ReverseDns` constants

- **Where**: `packages/server/src/Mohist.Server/Infrastructure/Events/EventCatalog.cs:85-100, 113-114`
- **What**: `WorkflowRunRetrying`, `WorkflowRunRerunning`, `TaskStarted`, `CheckStarted` are declared but never emitted. `IssueCompleted` / `IssueCancelled` belong to the Issue domain, not Workflow (out of scope).
- **Fix**: either emit them (P1-8 hint) or remove them.

### P3-4. `EmitStageChanged` emits `"Unknown"` for null current stage

- **Where**: `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:923-956`
- **What**: when `_run.CurrentStageId` is null (between `Create` and `Start`), the status becomes `"Unknown"`. The event is still emitted with `stage=null`, `status="Unknown"`.
- **Fix**: guard against null `CurrentStageId` and skip emission, or emit a `WorkflowRunCreated` event with the "Unknown" status but with `action="created"`.

### P3-5. `DispatchLifecycleHooksAsync` is dead code

- **Where**: `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:1060-1066`
- **What**: a no-op shim "retained for any callers that may still reference it". No callers exist (verified via grep).
- **Fix**: delete it.

### P3-6. `JsonElementSurrogate` re-parses on every deserialisation

- **Where**: `packages/server/src/Mohist.Server/Workflow/Grains/Surrogates/JsonElementSurrogate.cs:13-21`
- **What**: the surrogate stores `RawJson` and deserialises on every grain call. For a `WorkflowDefinition` with many `JsonElement` fields, this is a CPU cost.
- **Fix**: store the parsed `JsonDocument` or `JsonNode` and re-use it. (Hard to do with Orleans serialisation — the current design is the simplest that works.)

### P3-7. Default profile: `self-review` task + `self-review-passed` check coupling

- **Where**: `packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/mohist-default.workflow.yaml:49-85`
- **What**: the `self-review` task is a regular task; `self-review-passed` is a `core/marker` check that requires the task to write `<promise>PASS</promise>` to `self-review.md`. There is no "skip this check if the task was skipped" logic. If the runner fails to write the marker for any reason, the check fails and a repair is injected, even if the underlying plan is fine.
- **Fix**: add a `skipIfMissing` flag to `CheckDefinition`, or document that the `self-review` task MUST write the marker.

### P3-8. `Rerun` for `ApprovalRejected` does not call `ResetStageFailure` on the new stage

- **Where**: `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Failure.cs:54-72`
- **What**: `Rerun` replaces the `StageRun` with a fresh one. `Failure` is null on the new stage. But the new stage's `Tasks` and `Checks` are also empty (the new `StageRun` is bare). The `StageInit` work will re-initialise them. The `run.Failure` is set to null (line 66). So the state is clean. ✓. False alarm — this is fine.

### P3-9. `WorkflowRun.Metadata.CreatedAt` is set to `DateTimeOffset.MinValue` on `Start`

- **Where**: `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:1094`
- **What**: `new WorkflowRunMetadata(input.Name, DateTimeOffset.MinValue, input.Labels, annotations)` — the `CreatedAt` is `MinValue`. It is never updated. `WorkflowStatusMapper` exposes it as `MetadataView.CreatedAt`, which will show as `0001-01-01T00:00:00.0000000+00:00`.
- **Fix**: set `CreatedAt = DateTimeOffset.UtcNow` in `WorkflowRun.Create` (line 31 of `WorkflowRun.Lifecycle.cs` already does this: `new WorkflowRunMetadata(null, DateTimeOffset.UtcNow)`). The grain's override is wrong.

### P3-10. `WorkflowRun.ScheduleCheckRepair` emits `WorkflowRunResumed` even when run was Failed

- **Where**: `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Check.cs:38-56`
- **What**: see P1-8. The event name is misleading but the subscription-based UI may rely on it. Keep, but rename in a follow-up.

---

## Spec coverage gaps (no tests for these paths)

| Path | Spec needed |
|------|-------------|
| Lease timeout (P0-1) | Inject a `WorkLease` with `DispatchedAt = UtcNow - 6min`, advance the reminder, assert the workflow transitions to `Failed` and re-dispatches. |
| Approval-reject → rerun recovery (P0-2) | After `RejectAsync("reason")`, call `RerunAsync()`, assert the stage restarts and the rejection reason is persisted/visible. |
| `TaskCompleted` reaches the SignalR hub (P0-3) | Start a workflow, complete a task, assert the hub receives `com.mohist.workflow.task.completed` with `projectid` extension. |
| Approval-reject recovery with check-repair shortcut | After a check-fails-then-rejection, call `RerunAsync()`, assert the repair task is re-injected (or document that it isn't). |
| `Stop` while `AwaitingApproval` (P1-6) | Call `StopAsync` on a workflow that is in `AwaitingApproval`. Assert the behaviour matches the chosen design. |
| `RunTaskRace` (concurrent `ReportResult` + `Stop`) | Spawn two concurrent calls; assert ETag conflict is handled gracefully (or document the 500). |
| `OnDeactivateAsync` event flush (P1-1) | Deactivate the grain mid-`CommitAsync`; assert the events are persisted AND the bus is notified. |
| Retry-while-already-running idempotency (P1-2) | Click "retry" twice in <1s; assert the second call is a no-op (not a 500). |
| `RetryAsync` for `ApprovalRejected` (P0-2) | Document whether this throws (current behaviour) or schedules a repair. |

---

## Executive summary

The Workflow domain is **not production-ready**. The state machine logic is sound for the happy path and the documented spec, but the domain has at least three blocking issues for any deployment where a runner can crash, the Web UI needs real-time per-task feedback, or audit trails matter. The top three risks are: **(1) `lease_expired` is fire-and-forget — if a runner dies after picking up work, the workflow sits in `Running` with a permanent lease and no recovery path (P0-1)**; **(2) the 17 reverse-DNS workflow events (`TaskCompleted`, `CheckPassed`, `RepairScheduled`, etc.) are emitted but never reach the Web UI because `EventBridge` only subscribes to the 56 legacy snake_case names, so per-task and per-check real-time progress is invisible to users (P0-3)**; **(3) approval rejection is silently re-runnable via `Rerun` with no spec coverage, no audit trail of why it was rejected, and no test asserting the recovery path works (P0-2)**. The top three quick wins are: **(a) make `CheckLeaseAgeAsync` actually reclaim the lease and transition the workflow to `Failed` with a new `FailureReason.LeaseExpired` — 20 lines of code, unblocks the entire dead-runner scenario**; **(b) add the reverse-DNS workflow event names to `EventCatalog.All` (or union the two arrays in `EventBridge.StartAsync`) — 17 lines of code, unblocks real-time Web UI updates**; **(c) add a spec test for the approval-reject → rerun recovery path and persist the rejection reason on the new `StageRun` — 30 lines of code, closes the largest unverified workflow loop**. The state machine, ETag persistence, lease-store persistence, and stage-lock semantics are all well-implemented and well-tested for the documented happy path; the gaps are mostly in cross-cutting event completeness and the runner-crash recovery path.
