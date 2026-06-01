## Context

Mohist stores workflow scheduling state in two places: backlog state tracks whether a workflow is waiting or running, and workflow leases track active work ownership by a runner. The current implementation can leave stale entries behind when a workflow is paused, cancelled through its issue, failed, completed, or claimed by a runner but then returns no work. Startup recovery currently focuses on registering runnable persisted workflows, but does not reconcile stale backlog or lease rows that were already persisted.

The result is conflicting operational state: a workflow can be visible as both waiting and running, a paused or terminal workflow can still have a running claim or lease, and runner polling can keep assignments for workflows that are no longer runnable. This design treats persisted scheduling state as runtime authority that must be repaired at every lifecycle boundary, not as a UI projection problem.

Stakeholders are backend workflow execution, runner polling, recovery, diagnostics, and users/operators relying on queue and runner status to understand live work.

## Goals / Non-Goals

**Goals:**

- Enforce one scheduling state per workflow in a backlog: waiting without a lease, running with an active lease, or absent when non-runnable.
- Remove backlog waiting/running entries and clear active leases when a workflow is paused, cancelled, failed, completed, or otherwise non-runnable.
- Ensure `WorkflowBacklogGrain.RegisterAsync` and claim paths cannot persist the same workflow in both `Waiting` and `Running`.
- Repair runner poll claims when `RunnerGrain.PollAsync` claims a workflow but `WorkflowGrain.GetWorkAsync` returns no work.
- Extend startup recovery so it reconciles existing `BacklogStates` and `WorkflowLeases` against authoritative workflow run state.
- Preserve diagnostic evidence when stale state is repaired or in-flight work is abandoned.

**Non-Goals:**

- Do not change queued-state UI projection behavior tracked by issue #23.
- Do not implement configurable runner concurrency tracked by issue #22.
- Do not change active leased task timeline styling tracked by issue #21.
- Do not rely on manual SQLite cleanup as the product fix.
- Do not introduce a new scheduling product capability; this is consistency repair for existing workflow scheduling semantics.

## Decisions

1. Centralize non-runnable cleanup behind a workflow unschedule operation.

   Workflow pause and terminal transition paths should call a shared operation that removes the workflow from all relevant backlog buckets and clears any active workflow lease. `IssueGrain.CancelAsync` should continue to pause the workflow, but `WorkflowGrain.PauseAsync` must become responsible for unscheduling the workflow before the paused state is externally useful for scheduling.

   Rationale: pause, cancellation, failure, completion, and stale no-work repair all need the same invariant: non-runnable workflows have no backlog entry and no active lease.

   Alternatives considered: duplicate cleanup in each caller, or only clean during startup recovery. Duplicating cleanup risks drift between lifecycle paths. Recovery-only cleanup leaves live runtime state inconsistent until restart.

2. Make backlog registration and claiming exclusive by construction.

   `WorkflowBacklogGrain.RegisterAsync` should remove any stale running claim for the workflow before adding it to waiting, even when the workflow is already waiting. Claiming should persist the workflow only in running and remove it from waiting in the same state update. Releasing should remove from both waiting and running when the caller intends to unschedule a workflow.

   Rationale: backlog state is the queue authority, so the grain must prevent impossible states at write time instead of relying on readers to interpret them.

   Alternatives considered: add read-time filtering to ignore duplicates, or add periodic cleanup only. Read-time filtering hides corruption but leaves diagnostics and future claims unreliable. Periodic cleanup reduces but does not eliminate live race windows.

3. Treat workflow leases as active only when paired with a running backlog claim and runnable work.

   Lease clearing should be part of unscheduling, terminal transition cleanup, startup reconciliation, and poll no-work repair. If an in-flight task is abandoned because the workflow became non-runnable, the active lease should be cleared while diagnostic evidence records the reason, workflow id, runner id, and affected work item when available.

   Rationale: the specs define runner assignment as active only while the workflow has runnable leased work. Keeping a lease after pause or terminal transition makes runner and backlog views contradictory.

   Alternatives considered: mark stale leases as inactive but keep them in the active lease table, or require runners to release leases later. Keeping inactive rows in the active table preserves ambiguity. Waiting for runners fails when the runner is gone or the workflow is already paused.

4. Repair poll-claim-no-work synchronously in `RunnerGrain.PollAsync`.

   After a runner claims a workflow, if `WorkflowGrain.GetWorkAsync` returns null, the runner should remove the workflow from `_assignedWorkflows`, release or repair the backlog running claim, clear any active lease for that claim if one exists, and emit a stale-state diagnostic before continuing or returning no assignment.

   Rationale: the poll path is where a stale backlog claim can become a permanent runner assignment. Repairing immediately prevents capacity from being consumed by non-work and prevents stale persisted running claims from surviving the failed poll.

   Alternatives considered: leave assignment until a heartbeat timeout, or push all repair to `GetWorkAsync`. Heartbeat timeout delays capacity recovery and keeps diagnostics misleading. `GetWorkAsync` can report no work, but the runner owns `_assignedWorkflows` and must clean its own assignment state.

5. Expand startup recovery from registration to reconciliation.

   `WorkflowBacklogRecoveryService` should inspect persisted backlog waiting entries, running entries, and workflow leases. For each workflow id, it should load authoritative workflow run state. Paused, failed, completed, cancelled, missing, or no-work workflows should be removed from backlog state and leases. Runnable workflows without active leases may be registered as waiting. Running entries should remain only when they have a corresponding active lease and the workflow is still runnable.

   Rationale: recovery is the only reliable place to repair stale persisted state left by older versions, crashes, or runner loss before the new cleanup paths existed.

   Alternatives considered: only repair rows touched by future lifecycle events, or delete all persisted backlog state on startup and rebuild. Future-only repair leaves existing stale rows indefinitely. Full delete-and-rebuild is simpler but risks dropping legitimate active lease diagnostics and in-flight ownership without recording why.

6. Prefer idempotent repair APIs over one-off SQL cleanup.

   Backlog release, lease clearing, and recovery repair should be safe to call repeatedly and tolerate missing workflow ids, missing leases, and already-clean backlog state. Tests should exercise behavior through grains and services rather than relying on direct database mutation except for arranging persisted stale state.

   Rationale: lifecycle transitions, recovery, and runner polling can overlap across restarts and retries. Idempotent repair keeps consistency logic robust and avoids special casing every stale-state shape.

   Alternatives considered: narrow command methods for each stale case. Narrow methods are easier to reason about locally but increase the chance that a new lifecycle path misses one cleanup step.

## Risks / Trade-offs

- [Risk] Clearing an active lease during cancellation may hide what work was abandoned -> Mitigation: record explicit abandonment or stale-state diagnostics with workflow id, runner id, reason, and work item details when available.
- [Risk] Recovery may remove a running claim for a workflow whose runner is still alive but temporarily unreachable -> Mitigation: only keep running entries when both runnable workflow state and active lease state agree; otherwise prefer safe requeue or cleanup over preserving contradictory authority.
- [Risk] Central cleanup may require new grain calls across workflow, backlog, lease, and runner assignment state -> Mitigation: keep the operation idempotent and small, and call it only at lifecycle boundaries or proven stale poll states.
- [Risk] Repairing `PollAsync` synchronously adds work to the hot polling path -> Mitigation: only run the repair after a claim produces no work, which should be exceptional; normal successful claims remain unchanged.
- [Risk] Existing persisted corrupt rows may include workflow ids that no longer map to a run -> Mitigation: recovery treats missing authoritative workflow state as non-runnable and removes backlog entries and leases with diagnostics.

## Migration Plan

1. Add or extend idempotent backlog repair/release behavior so a workflow can be removed from both waiting and running buckets, and registration/claiming enforce waiting/running exclusivity.
2. Add or extend lease cleanup so active workflow leases can be cleared with a diagnostic reason such as pause, terminal transition, cancellation, stale poll claim, or recovery reconciliation.
3. Wire workflow lifecycle transitions to cleanup scheduling state when pausing, cancelling through issue pause, completing, failing, or cancelling a workflow run.
4. Update `RunnerGrain.PollAsync` so no-work after claim removes runner assignment and repairs the claim before capacity is considered consumed.
5. Update startup recovery to reconcile all persisted waiting, running, and lease entries against workflow run state before registering runnable work.
6. Add backend tests for cancellation with active lease cleanup, completed/failed terminal cleanup, waiting/running deduplication, and poll-claim-no-work repair.
7. Deploy normally. On startup, reconciliation repairs stale rows created by older versions.

Rollback strategy: rolling back code may stop future automatic repair, but rows already removed by the new version should remain valid because absence from backlog/lease state is the correct representation for paused or terminal workflows. If rollback is required, preserve diagnostics and avoid restoring stale `BacklogStates` or `WorkflowLeases` from backup unless they are known to represent active runnable work.

## Open Questions

- What exact diagnostic sink should stale-state repair use if no existing workflow event type fits repair or lease-abandoned evidence?
- Should startup recovery requeue runnable workflows that have a running entry but no active lease, or remove them and rely on workflow state to register them again? The preferred behavior is to make the final persisted state waiting without a lease if the workflow is runnable and has available work.
- Should lease clearing notify the previous runner grain to drop `_assignedWorkflows` if that runner is still active, or is assignment cleanup on the next runner status/poll sufficient? The acceptance criteria require assignment tracking to clear when leases are released, so the implementation should choose the smallest reliable notification or shared assignment cleanup path.
