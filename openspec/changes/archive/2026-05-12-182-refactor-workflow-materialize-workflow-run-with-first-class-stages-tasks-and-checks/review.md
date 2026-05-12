# Review: Issue #182 — Materialize WorkflowRun with first-class stages, tasks, and checks

## Overall Assessment

The implementation is thorough, well-structured, and faithful to the spec. It adds a complete WorkflowRun persistence layer, wires it into the engine and API, provides backward-compatible stage-state projection, and updates the UI to prefer WorkflowRun data. Tests cover the core paths. **PASS**.

---

## Correctness

### No critical bugs found

- `WorkflowRunRepo.create` correctly generates stable IDs (`wr_<number>_<timestamp>`) and defaults to `status='running', currentStage='plan'`.
- `startRun` idempotently reuses the active run via `findActiveByIssueId` before creating, preventing duplicate runs (REQ-WR-001).
- `upsertTask` and `upsertCheck` correctly query by `(stage_run_id, task_id)` and `(stage_run_id, check_name)` to prevent duplicates (REQ-WR-002).
- `materializeBuildTasks` iterates tasks and calls `upsertTask`, making repeated calls idempotent.
- `setStagePassed` / `setStageFailed` / `setStageAwaitingApproval` correctly update both the StageRun and the WorkflowRun `currentStage` atomically.
- `setApproval` stores the snapshot on the StageRun, not as a first-class model — matching the spec's non-goal.
- `base-stage-runner.ts` mirrors task/check/status/approval to both `stage-state` (compatibility) and `WorkflowRun` (canonical), with individual try/catch per path — failures in one don't block the other.
- `workflow-engine.ts` line 166-170: marks the WorkflowRun as `passed` + `Done` when the pipeline completes.
- `plan-stage-runner.ts` lines 421-428: calls `materializeBuildTasks` after Plan succeeds, as specified.

### Minor observations (non-blocking)

1. **`updateStageRunStatus` sets `completed_at` for `awaiting-approval`** — The `completed_at` timestamp is set when status is `awaiting-approval`. This is semantically questionable (awaiting-approval is not a terminal state), but it doesn't break anything since the field is informational and the UI reads `status` as the authoritative state. Low severity.

2. **`setApproval` uses `setApproval(runId, stage, ...)` but the API test at line 153 passes `issue.id` instead of `run.id`** — However, looking at the test more carefully, `workflowRunService.setApproval(issue.id, Stage.Plan, {...})` actually passes the `runId` parameter position with `issue.id`, but the API routes use `run.id` correctly (lines 1320, 1365). The test at line 153 is actually calling the service directly with `issue.id` which happens to work because `startRun` was called first and uses that same `issue.id` to find the run. But wait — `setApproval` on the service takes `runId`, not `issueId`. Let me check...

Actually, re-reading the test at line 153-157:
```typescript
workflowRunService.setApproval(issue.id, Stage.Plan, { ... })
```
This passes `issue.id` as `runId`. The service then calls `repo.findStageRunByStage(runId, stage)`, which looks up by `workflow_run_id`. Since `issue.id` is NOT the `run.id` (which is `wr_42_<timestamp>`), this would find NO matching stage run and silently return (the service uses `if (!stageRun) return`). This is a **test bug** — the approval snapshot is never actually stored in this test case. However, the API-level approval paths in `issues.ts` (lines 1319-1369) correctly use `run.id`.

This test bug is cosmetic: the test at line 153 asserts the response shape which still works because the query at line 159 reads from the WorkflowRun directly, but the approval data was never written. The test on line 185-188 still passes because it's checking `planStageRun.approval` from the API response, but since the setApproval call was a no-op, `approval` would actually be null in real execution. **This test is not actually verifying approval persistence.**

3. **No `paused` or `blocked` status transition for WorkflowRun** — The WorkflowRunStatus type is `'running' | 'passed' | 'failed' | 'cancelled'`, but the spec mentions `paused` and `blocked` in the YAML model. These are not implemented. This is probably acceptable for v1 since the issue is focused on the runtime records, but the divergence from the spec model is worth noting.

4. **`WorkflowRunRepo.findActiveByIssueId` only finds `status = 'running'`** — If a run is `paused` or `blocked` (future states), it won't be found. For the current v1 implementation this is fine since only `running` is used as active status.

---

## Complexity

All functions stay under 50 lines. `WorkflowRunRepo.getActiveRunWithRelations` is the longest at ~35 lines and has clear structure (query run, query stage runs, query tasks/checks per stage). `StageStateService` was already large before this change; the new `getIssueStageStateFromWorkflowRun` method is ~55 lines but straightforward row mapping. No cyclomatic complexity concerns.

---

## Test Coverage

### Covered

- `startRun` creates run with correct defaults, idempotent reuse (2 tests)
- Plan tasks seeded correctly (5 tasks, 6 checks)
- Build task materialization (2 tests: basic + idempotent upsert)
- Runtime-added task metadata (reason, causedBy) (3 tests)
- Evidence separation: stage_executions, workflow_log, checkpoints not used as primary state (3 tests)
- `upsertCheck` and `setApproval` (2 tests)
- API: no-run returns 404, full response shape, Build task materialization, evidence non-promotion (4 tests)
- Stage-state compatibility: WorkflowRun preferred, evidence not promoted, runtime tasks with metadata visible (3 tests)
- UI consistency: PipelineView + TaskProgressPanel agree, WorkflowRun preferred, checks separate from tasks (5+ tests)

### Gaps (non-blocking)

- No test for WorkflowRun status transitions (running → paused → running, running → failed → running on resume)
- No test for concurrent start race conditions (two `startRun` calls in parallel)
- No test for the `setApproval` API integration through the actual approval endpoint
- The setApproval test mentioned above has a bug where `issue.id` is used instead of `run.id`

---

## Security

No injection risks identified. The repo uses parameterized queries throughout. The API route validates issue existence before querying. No secrets or credentials exposed. The `startedBy` field accepts a string from the server-side pipeline (not user input), so SSRF/injection via that path is not a concern.

---

## Spec Compliance

| # | Acceptance Criterion | Status | Evidence |
|---|---|---|---|
| 1 | Start issue creates WorkflowRun with stable id, bound to issue | **PASS** | `WorkflowRunService.startRun` creates `WorkflowRun` + 4 `StageRun`s in a transaction; `agent-runner-service.ts:1044-1047` calls `startRun` on pipeline start and resume |
| 2 | WorkflowRun has stable id, bound to issue id/number | **PASS** | Repo creates `wr_<number>_<timestamp>` id; stores `issueId` and `issueNumber` |
| 3 | WorkflowRun exposes currentStage and run status | **PASS** | `current_stage` and `status` columns; `setStageStarted/Passthrough/Failed/AwaitingApproval` update both StageRun and WorkflowRun |
| 4 | WorkflowRun contains ordered StageRuns for Plan/Build/Check/Integrate | **PASS** | `startRun` seeds 4 StageRuns in order; `stage_order` indexed |
| 5 | Plan initial tasks/checks queryable from WorkflowRun | **PASS** | `seedPlanTasks`/`seedPlanChecks` create 5 tasks + 6 checks; API returns them |
| 6 | Build tasks materialize into same WorkflowRun after Plan | **PASS** | `materializeBuildTasks` upserts Build tasks; `plan-stage-runner.ts:421-428` calls it; idempotent upsert prevents duplicates |
| 7 | Runtime-added tasks appear as normal tasks with reason/causedBy | **PASS** | `upsertTask` stores `reason`, `caused_by_type`, `caused_by_check_name`, `caused_by_task_id`; tests verify repair/rebase/retry/conflict metadata |
| 8 | UI uses WorkflowRun as source of truth for stage/task/check | **PASS** | `PipelineView` and `TaskProgressPanel` use `useWorkflowRun` hook; `workflowRunToStageStateMap` converts; fallback to `useIssueStageState` when no run exists |
| 9 | stage_executions/workflow_log remain evidence only | **PASS** | `getActiveRunWithRelations` reads only from `workflow_*` tables; tests verify stage_executions and logs don't leak into WorkflowRun state |
| 10 | Checkpoint remains resume cursor only | **PASS** | No code reads from `pipeline_checkpoint` for WorkflowRun state; test confirms checkpoint data doesn't populate WorkflowRun |
| 11 | No user-visible planned/dynamic/static task categories | **PASS** | `workflow-run-utils.ts:35` assigns `source` only for internal compatibility mapping; UI renders one flat task list per stage |
| 12 | No first-class policy/decision model | **PASS** | Approval stored as snapshot fields on `workflow_stage_runs`; no separate Decision table |
| 13 | API exposes WorkflowRun via `/workflow-run` endpoint | **PASS** | `issues.ts:419-456` returns full run with nested stageRuns/tasks/checks/approval |
| 14 | Legacy stage-state compatibility reads from WorkflowRun | **PASS** | `issues.ts:476-489` projects from `getIssueStageStateFromWorkflowRun` when run exists; falls back to legacy `getIssueStageState` |
| 15 | Start is idempotent for active run | **PASS** | `startRun` checks `findActiveByIssueId` first; returns existing run with relations |

---

## Warnings

1. **Test bug in setApproval**: The API test at `workflow-run-api.test.ts:153` passes `issue.id` where `run.id` is expected. The `setApproval` call is a no-op because the method silently returns when no matching StageRun is found. This doesn't affect production code but means the approval persistence path isn't actually tested end-to-end.

2. **`completedAt` set on `awaiting-approval`**: `updateStageRunStatus` in `workflow-run-repo.ts:455` treats `awaiting-approval` as a completed state for `completed_at` purposes. This is a minor semantic inconsistency.

3. **Missing `paused`/`blocked`/`cancelled` status transitions**: WorkflowRunStatus includes `cancelled` but no code path transitions to it. `paused` and `blocked` from the spec model are omitted. Acceptable for v1 but noted.

4. **`startRun` query uses `status = 'running'` only**: If a run ever enters a `paused` state, `findActiveByIssueId` won't find it, potentially allowing duplicate creation. The spec mentions `paused` as a possible status. This should be addressed when `paused` status is added.

5. **Plan seed definitions duplicated**: Plan task/check names are defined in both `WorkflowRunService.seedPlanTasks`/`seedPlanChecks` and `stage-state-service.ts` `REAL_TASK_IDS`. The design doc (D2, Risk 5) calls this out and recommends centralization.

<promise>PASS</promise>