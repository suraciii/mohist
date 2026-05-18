## Context

Mohist currently records workflow progress mainly through `WorkflowRun`, `StageRun`, `workflow_tasks`, `workflow_checks`, queue tasks, and `coder_session` rows. The workflow aggregate can remain `running` even when the agent process that was executing the current work item has stopped or disappeared. In that state, the issue projection may mark the issue as blocked and the web UI may offer `Retry`, while the retry endpoint rejects the request because the latest workflow run is still `running` and has no failed task/check to retry.

The missing abstraction is a work-item execution attempt. Tasks and checks already carry some attempt-like data (`attempts`, `runCount`, status, output), but the latest execution condition is not modeled explicitly enough to distinguish live `Running`, terminal `Failed`, and stopped/lost `Interrupted`. This change introduces attempt state at the work item boundary and makes recovery decisions derive from that latest attempt instead of from `issue.status === blocked` or workflow-level status alone.

Runtime evidence remains implementation detail. Queue task state, coder session state, ACP session information, and agent process liveness are used to reconcile an attempt, but are not exposed as a first-class domain entity.

## Goals / Non-Goals

**Goals:**

- Model the current task/check execution condition as the latest attempt of a stage work item.
- Add `Interrupted` as a distinct attempt state for stopped or lost execution.
- Reconcile stale `Running` attempts before API, CLI, and UI expose primary recovery actions.
- Restrict `Retry` to failed latest attempts and present interrupted work as resume/rerun/inspect guidance.
- Keep `WorkflowRun` and stage state derived from work item progress so run-level status cannot contradict the current work item.
- Preserve historical stop, interruption, and failure evidence for inspection.
- Cover the #229 stale-running shape and a genuine failed-attempt retry path with regression tests.

**Non-Goals:**

- Reintroduce `restart`.
- Add rewind-to-stage behavior.
- Model runtime proof as a persisted first-class domain entity.
- Redesign the issue detail page.
- Change Integrate rollback or delivery semantics.
- Hide historical stop/failure/interruption records.

## Decisions

### D1: Represent attempts under tasks and checks, not under WorkflowRun

Add a work-item attempt model to the workflow domain and persistence layer. A work item is either a task or a check within a `StageRun`; its latest attempt has a state of `running`, `completed`, `failed`, or `interrupted`, plus timestamps, attempt number, optional output/error details, and links to runtime evidence identifiers such as queue task id, ACP session id, coder session id, execution id, and process pid when available.

The existing task/check status remains the compact progress summary used by stage completion rules. The latest attempt becomes the source of truth for recovery. Task/check status is derived from or synchronized with the latest attempt: completed attempts produce completed/passed work, failed attempts produce failed/error work, interrupted attempts leave the work item incomplete and move the workflow to waiting-for-recovery instead of failed, and running attempts are valid only while live evidence exists.

This can be implemented either with explicit `workflow_work_item_attempts` rows or with an embedded attempt list on task/check snapshots. Prefer a separate table keyed by `workflow_run_id`, `stage`, `work_item_type`, `work_item_id`, and `attempt_number` because it preserves attempt history without overloading the existing `attempts` and `run_count` counters. The aggregate should expose a small API such as `startWorkAttempt`, `completeWorkAttempt`, `failWorkAttempt`, `interruptWorkAttempt`, and `latestWorkAttempt` rather than leaking table details.

**Alternatives considered:** Keep using task/check status plus `attempts` counters. This is too ambiguous because a stopped agent can look like pending/running without explaining whether retry is valid. Add attempts to `WorkflowRun`. This matches neither the product model nor retry semantics because retry targets failed work, not the entire run.

### D2: Reconcile running attempts through a recovery service boundary

Introduce a reconciliation operation in `WorkflowApplicationService` that loads the latest run, finds the current work item and latest attempt, and validates `running` attempts against live evidence. Evidence checks should be centralized behind a small port, for example `WorkflowAttemptEvidencePort`, with methods that answer whether there is an active queue task and whether the related coder session/process is still live.

If the latest attempt is `running` and there is no live queue task and no live agent process/session evidence, reconciliation marks the attempt `interrupted`, records an interruption reason such as `agent-stopped` or `agent-lost`, updates the current stage/run projection to waiting-for-recovery, and emits workflow log/SSE events. It must not mark the attempt failed unless the agent or task handler produced a failed result.

Reconciliation should run before recovery-sensitive reads and writes: issue detail/stage-state API, retry availability, resume/rerun endpoints, CLI status/show/recovery commands, queue recovery scans, and workflow resume decisions. This makes stale states self-heal when users inspect or act, without requiring a separate background job to be perfectly reliable.

**Alternatives considered:** Run only a periodic background cleanup. This reduces request-time work but still allows stale UI/API contradictions between cleanup runs. Let each endpoint inspect queue/session state independently. This spreads runtime-evidence rules across the codebase and risks inconsistent recovery actions.

### D3: Derive recovery actions from latest attempt state

Add one projection shape, shared by API, CLI, and UI, that describes the current work item, latest attempt state, workflow summary state, and allowed actions. The action mapping is:

- `running`: wait/stop when live evidence exists.
- `completed`: continue workflow.
- `failed`: retry failed work.
- `interrupted`: resume, rerun stage, or inspect.

`checkRetryAvailability` and `retryStageOrReject` should stop requiring only `WorkflowRun.status === 'failed'`; instead they should ask the reconciled aggregate for the latest failed work attempt in the current stage. If the latest attempt is interrupted, retry returns a conflict with an interrupted-specific reason and suggested actions. Genuine failed task/check attempts still expose retry and reuse the existing reset-downstream behavior, scoped to the failed work item.

The web UI should render primary actions from this backend recovery projection, not from `issue.status === 'blocked'`. Existing blocked reasons remain visible as diagnostic text, but they do not independently enable `Retry`.

**Alternatives considered:** Keep UI heuristics and only adjust the retry endpoint. This would continue to let UI, CLI, and API disagree. Treat interrupted as a subtype of failed to reuse retry. This would mislead users and erase the difference between an execution result and lost/stopped execution.

### D4: Make workflow-level status a summary of current work progress

Keep `WorkflowRun.status` as the persisted compact state for compatibility, but add a derived workflow recovery summary in the aggregate/projection: `running`, `awaiting-approval`, `waiting-for-recovery`, or `completed`. When the latest attempt is interrupted, the run must not continue to project as actively running. The persisted run may either use a new status such as `blocked`/`interrupted` or keep the existing status enum and project waiting-for-recovery from the latest attempt; prefer a minimal schema change only if existing consumers require filtering non-running runs at SQL level.

The key invariant is: if the current work item's latest attempt is not running, the workflow summary cannot say active running. Existing stage completion and failure rules continue to own completed and failed outcomes, while interruption owns recovery waiting.

**Alternatives considered:** Add many workflow statuses for every recovery case. This pushes work-item detail into the run and recreates the current contradiction risk. Leave `WorkflowRun.status` as the only user-facing state. This cannot represent interrupted work without conflating it with failed or running.

### D5: Capture interruption at both active stop time and stale-read time

When Mohist intentionally stops an agent, the stop path should mark the related coder session as cancelled/interrupted and call the workflow attempt interruption API for the associated execution id. When Mohist discovers stale evidence later, reconciliation performs the same domain transition idempotently.

The `agent-session` task handler should continue to report genuine execution errors as failed task results. It should not convert all thrown errors into failed attempts if the session state indicates user stop or lost process; those paths should become interrupted attempts with diagnostic output.

**Alternatives considered:** Only handle stale-read reconciliation. This fixes #229 eventually but loses the best moment to record an intentional stop. Only handle stop-time updates. This misses crashes, process disappearance, and older inconsistent rows.

### D6: Preserve existing retry/rerun behavior through a narrow adapter

Retry should target the latest failed work attempt and then reuse the current stage reset logic where possible: failed tasks reset that task and downstream work; failed checks reset that check/downstream checks and any dependent repair work. Rerun stage remains broader and creates fresh attempts for all work items in the stage. Resume for interrupted attempts should not pretend there is a failed result; it should either continue from the next incomplete work item or schedule fresh execution for the interrupted work item according to the stage runner's existing dispatch path.

This keeps the implementation localized: the domain learns latest-attempt state and recovery classification, while stage runners continue to execute tasks/checks through existing dispatch factories.

**Alternatives considered:** Rewrite stage execution around attempts in one step. This is cleaner long term but too broad for the bug fix. Duplicate retry logic for attempts. This risks diverging from existing task/check reset semantics.

## Risks / Trade-offs

- [Risk] Existing rows have no attempt history. → Migration should synthesize one latest attempt from current task/check status where possible: completed/passed become completed, failed/error/skipped become failed, running becomes running until reconciliation proves interruption, and pending has no attempt.
- [Risk] Process liveness checks can be wrong if a PID is reused. → Treat PID as supporting evidence only when paired with coder session/execution id and recent session/queue activity; absence of evidence may interrupt, but presence should require matching context where possible.
- [Risk] Request-time reconciliation can add latency to issue detail and CLI status. → Keep evidence checks bounded and local; avoid expensive process scans, and only reconcile the current/latest running attempt.
- [Risk] Some callers may still use `issue.status === blocked` or raw `WorkflowRun.status` for actions. → Add tests around UI action rendering, API retry conflicts, and CLI/status projection; prefer a shared recovery projection type.
- [Risk] Interrupted work may be resumed incorrectly if partial side effects occurred. → Present inspect/rerun as available guidance, and make resume re-enter the normal stage runner from persisted work state rather than assuming the interrupted attempt completed anything.
- [Risk] New attempt persistence can drift from existing task/check status. → Make all domain transitions update both under one aggregate save, and add invariant tests for latest attempt versus task/check/stage/run projection.

## Migration Plan

1. Add attempt domain types, snapshot fields, repository mapping, and a migration for the attempt table if using separate rows.
2. Backfill/synthesize latest attempts for existing task/check rows during migration or snapshot repair.
3. Update task/check execution paths to start, complete, fail, and interrupt work attempts through the aggregate.
4. Add the reconciliation service and call it before recovery-sensitive reads/writes and queue/session recovery scans.
5. Change retry availability and retry execution to use latest failed work attempt state instead of workflow-run failure alone.
6. Add a shared recovery projection to API responses and CLI status/show output.
7. Update Issue Detail action rendering to use the recovery projection and remove blocked-status-only `Retry` rendering.
8. Add regressions for the #229 shape and for a genuine failed task/check retry path.
9. Rollback strategy: keep migrations additive, preserve old task/check columns, and allow UI/API to fall back to existing status fields if the recovery projection is absent during a mixed-version rollout.

## Open Questions

- Should the persisted `workflow_runs.status` enum gain an explicit non-running recovery state, or should waiting-for-recovery remain projection-only to minimize schema churn?
- What should the first shipped `resume` behavior do for an interrupted agent-session task: re-dispatch the same work item as a fresh attempt, or only surface inspect/rerun until a safer resume semantic is defined?
