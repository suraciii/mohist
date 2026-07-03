## Context

A stopped workflow run can still be reported as "awaiting approval". `WorkflowRun.Stop()` (`Workflow/Domain/Run/WorkflowRun.Lifecycle.cs:126`) only does `run.Status = WorkflowRunStatus.Stopped` — it never touches the current stage's `ApprovalStatus` or `StageRunStatus`. Since the "is awaiting approval" predicate is `IsAwaitingApproval => stage.ApprovalStatus is { Result: null }` (`WorkflowRun.Stage.cs:88`), a stage parked in `AwaitingApproval` keeps reporting an unresolved gate after the run is terminated. Downstream consumers faithfully surface this: `WorkflowStatusMapper` builds a per-stage `ApprovalStatusView` from `s.ApprovalStatus` (`WorkflowStatusMapper.cs:37`), and `MohistDefaultWorkflowProjection.StageApprovals` yields an `awaiting` entry for any stage whose `ApprovalStatus` is non-null (`MohistDefaultWorkflowProjection.cs:64-70`). The live symptom (#331): a cancelled/stopped issue persists `approvalState.status == awaiting` on the board.

The violated invariant: **a terminal (Stopped) run must never carry a residual approval gate.** Approval is a door on a live run that will continue; once stopped it is meaningless. This is a domain-layer omission, not a projection/UI bug.

Key code facts grounding the design:

- `StageRunStatus { Pending, Running, AwaitingApproval, Completed, Failed }`. A stage only enters `AwaitingApproval` via `TryRequestApproval` (`WorkflowRun.Stage.cs:118-125`), whose precondition is tasks-done + checks-passed; the stage was `Running` immediately before.
- An identical "approval-invalidation" cleanup already exists in `AddRuntimeTasks` (`WorkflowRun.Work.cs:94-98`): `if (current.IsAwaitingApproval) current.ApprovalStatus = null; ... current.Status = StageRunStatus.Running;`. Stop is the stronger invalidation context and should mirror it.
- `Stop()` is guarded against terminal state (`WorkflowRun.Lifecycle.cs:128`); the grain's `StopAsync` repeats the same guard (`WorkflowGrain.cs:134`).
- The grain already self-heals one class of stale state on rehydration: `OnActivateAsync` runs `ReconcileReadyStatusWithInFlightWork()` and, if it returns true, writes the corrected state back (`WorkflowGrain.cs:67-74`).
- The issue read path does **not** go through the grain: `IssueQuerier.LoadWorkflowStatesAsync` (`IssueQuerier.cs:2187-2210`) reads `db.WorkflowRuns.State`, deserializes it, and calls `WorkflowStatusMapper.BuildStatusView` directly. So a dirty persisted `State` keeps producing a dirty view until the `State` itself is corrected.

## Goals / Non-Goals

**Goals:**

- `WorkflowRun.Stop()` clears a residual awaiting-approval gate on the current stage (null `ApprovalStatus`, `StageRunStatus` away from `AwaitingApproval`), so all stops from now on leave a self-consistent run.
- The cleanup is driven by runtime state (not by arrival of a fresh event), so it also repairs already-persisted dirty records like #331 when their state is next re-evaluated.
- `Stop()` keeps emitting only `WorkflowRunStopped` — no approval-decision events.
- Downstream consumers (`WorkflowStatusMapper`, `MohistDefaultWorkflowProjection.StageApprovals`, `IssueQuerier`, board) need no code change; they read the cleaned domain state.

**Non-Goals:**

- No projection / mapper / frontend changes to the approval predicate (per proposal boundary).
- No inbox cleanup of historical `approval_requested` notifications.
- No change to `ClearExecutableStateAsync` lock/task cleanup.
- No new approval-resolution event (`StageApprovalResolved` etc.) on stop.
- No persistence migration / schema change.

## Decisions

### D1. Clean up the current stage inside `WorkflowRun.Stop()`, mirroring `AddRuntimeTasks`

In `Stop()`, before transitioning `run.Status`, inspect the current stage; if `IsAwaitingApproval`, null its `ApprovalStatus` and flip `StageRunStatus` off `AwaitingApproval`.

```csharp
var current = run.CurrentStage();
if (current.IsAwaitingApproval)
{
    current.ApprovalStatus = null;
    current.Status = StageRunStatus.Running;
}
run.Status = WorkflowRunStatus.Stopped;
return [new WorkflowRunStopped()];
```

**Target status = `Running`.** The stage was `Running` immediately before requesting approval (the only entry to `AwaitingApproval` is `TryRequestApproval`, reached from `Running` with tasks-done + checks-passed). This is exactly the pattern `AddRuntimeTasks` (`WorkflowRun.Work.cs:95-98`) already uses for its approval-invalidation, and it matches how a stop from a `Ready`/`Running` state already leaves the stage today (`Stop_LandsOnStopped` leaves the stage `Running` while the run is `Stopped`). The run-level `Stopped` is the authoritative terminal signal.

- *Alternative — set stage to `Completed`:* rejected. `Completed` implies the gate passed and the run advanced past the stage; for a stopped run that is misleading and would deviate from existing stop semantics (which leave the stage as-is).
- *Alternative — null `ApprovalStatus` only, leave `StageRunStatus == AwaitingApproval`:* rejected. Violates the spec ("SHALL NOT be `AwaitingApproval`") and leaves a self-contradictory field (a stage claiming to await approval with no approval object).

### D2. Factor the cleanup as an idempotent domain method, also invoked on grain rehydration (self-heal)

`Stop()` cannot repair already-persisted dirty state because it (and the grain's `StopAsync`) throw on terminal status — a run persisted as `Stopped` can never be re-stopped. So self-healing of #331-class records must happen where state is re-evaluated without a stop: grain activation. The grain already has this exact pattern for another invariant (`ReconcileReadyStatusWithInFlightWork` in `OnActivateAsync`, `WorkflowGrain.cs:67-74`, with write-back).

Decision: extract the cleanup into a small idempotent domain method (e.g. `bool ReconcileStoppedApprovalGate()`, sibling to `ReconcileReadyStatusWithInFlightWork` in `Shared.cs`) that returns whether it changed state, then:

1. Call it from `Stop()` (D1) before the `Stopped` transition.
2. Call it from `OnActivateAsync` over rehydrated state, but **only when `run.Status == Stopped`** (so a live run genuinely awaiting approval is never disturbed); when it returns true, save the run back (mirroring the existing reconcile write-back at `WorkflowGrain.cs:70-73`).

The method's guard is the residual-gate condition `current.IsAwaitingApproval` → clear `ApprovalStatus`, set `StageRunStatus = Running`, return true. Guarding on `IsAwaitingApproval` (rather than `run.Status == Stopped`) is what lets the same method serve both call sites: inside `Stop()` the run is not yet `Stopped`, so a `Stopped`-only guard would make the cleanup a no-op and leave the gate dangling on every fresh stop (breaking the stop-clears-gate requirement). The `Stopped` scoping for the self-heal path lives at the `OnActivateAsync` caller instead. The method is idempotent — once the gate is cleared, `IsAwaitingApproval` is false, so subsequent activations are no-ops (no write amplification). This makes the fix self-healing for already-poisoned records the next time their grain activates, without a data migration.

- *Alternative — relax the `Stop()` guard to allow idempotent re-stop over `Stopped`+dirty:* rejected. Widens a terminal run's mutation surface, re-runs `ClearExecutableStateAsync`, and re-emits `WorkflowRunStopped` for an already-terminal run — large blast radius for a low-risk bug.
- *Alternative — one-off State scrub migration:* rejected. Spec explicitly wants self-healing without a migration; a reconcile-on-activate covers #331 and any future poison uniformly.

**Note on scope:** the proposal text scopes the change to "仅 `WorkflowRun.Stop()`". D2 is a minimal, necessary widening — the cleanup logic itself stays in the domain (a single method), and the only extra call site is the grain's existing rehydration reconcile, which the spec's "self-heals persisted dirty data" requirement (`spec.md` requirement 2 / scenario 3) and #331's acceptance criterion compel. No consumer/projection code changes.

### D3. No new events

`Stop()` continues to return `[WorkflowRunStopped]` only. The cleanup is a state correction over existing state, not an approve/reject verdict, so no `StageApprovalResolved`/`StageApprovalRequested` is emitted. The rehydration reconcile path (D2) emits no events at all — it only writes back corrected state, consistent with `ReconcileReadyStatusWithInFlightWork`.

### D4. Only the current stage is in scope

Only the current stage can be awaiting approval (`TryRequestApproval` runs solely on `run.CurrentStage()`), so scoping the cleanup to `CurrentStage()` is complete. Non-current stages are left untouched.

## Risks / Trade-offs

- `[Self-heal depends on grain reactivation]` -> The issue read path (`IssueQuerier.LoadWorkflowStatesAsync`) reads persisted `State` directly from the DB and bypasses the grain. D2's self-heal therefore fires only when the workflow grain for the poisoned run next activates (e.g. a workflow command or operator poke). For a cancelled issue that is never touched again, the dirty `State` — and thus the stale board "awaiting" — persists until then. Mitigation: any grain call reactivates and write-backs; an operator can force reactivation. If instant correction for *all* historical dirty records on the read path is required, the fallback is a read-time terminal guard in `StageApprovals`/`BuildStatusView` (see Open Questions) — but that widens the proposal's stated boundary.
- `[Stage left as Running while run is Stopped]` -> Looks odd in a raw audit, but is consistent with today's stop-from-Ready behavior and the run-level `Stopped` is authoritative. Mitigation: a code comment at the cleanup site noting the rationale.
- `[Reconcile mutates state on the activate/read-prep path]` -> Could surprise readers expecting activate to be pure. Mitigation: mirror the established `ReconcileReadyStatusWithInFlightWork` pattern exactly (guard → mutate → return changed → grain saves), so the behavior is familiar and the guard is narrow (`Stopped` + current-stage awaiting only).
- `[Idempotency across repeated activation]` -> After the first write-back the guard (`IsAwaitingApproval`) is false, so subsequent activations are no-ops. No write amplification.

## Migration Plan

1. Implement D1 + D2 (domain cleanup method, `Stop()` call, grain-activate reconcile call) and the spec assertions.
2. Deploy. All new and in-flight stops immediately produce self-consistent `State`.
3. Already-poisoned records (e.g. #331) self-correct on next activation of their workflow grain (write-back of cleaned `State`).
4. Verify #331: trigger its grain to reactivate (or observe on next natural activation), then confirm the board no longer reports awaiting approval and `mo issue show` no longer contradicts itself.
5. Rollback: revert the change; no schema/persistence change exists to undo. Already-cleaned records remain correct (removing the fix only affects future stops).

## Open Questions

- **Self-heal reach for historical dirty data.** Is grain-reactivation-triggered self-heal sufficient for #331 and similar cancelled records (domain-only, honors the proposal boundary), or should we additionally add a read-time terminal guard in `MohistDefaultWorkflowProjection.StageApprovals` / `WorkflowStatusMapper.BuildStatusView` (suppress any approval presentation when `run.Status` is terminal) so that *every* dirty record — touched grain or not — reads clean instantly? The latter guarantees correction for all records but widens the stated "consumers need no change" boundary into the projection layer. Recommend: ship domain-only (D1+D2) first; add the read-time guard only if reactivation proves insufficient in practice.
- **Reactivation trigger.** Do we want an explicit operator command/mechanism to force-reactivate (and thus self-heal) a specific poisoned run's grain, rather than waiting for an organic grain call?
