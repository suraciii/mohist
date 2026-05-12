## Context

Mohist currently has several partial representations of pipeline progress. `issues.stage` and `issues.status` identify the coarse issue position, `stage_states` / `stage_tasks` / `stage_checks` store a user-facing stage projection, `stage_executions` and logs store execution evidence, `tasks.json` stores planned Build work, and checkpoints store resume cursors. The recent stage-state work made the UI more accurate, but the root runtime concept is still implicit: there is no stable object that owns one issue advancement run and its stage/task/check instances.

This design adds a thin WorkflowRun runtime layer without replacing the runner architecture or introducing a workflow DSL. The first version should make WorkflowRun the canonical current-state model while keeping existing evidence, log, checkpoint, and stage-state compatibility paths available during migration.

## Goals / Non-Goals

**Goals:**

- Create one active WorkflowRun when an issue is started, with stable identity and issue binding.
- Store ordered StageRun records for the default executable stages: `plan`, `build`, `check`, and `integrate`.
- Store runtime Task and Check instances under their StageRun, including initial Plan tasks/checks, Build tasks materialized from `tasks.json`, and runtime-added repair/rebase/retry/conflict-resolution tasks.
- Provide a small backend API that lets the UI query the active/current WorkflowRun for an issue.
- Make WorkflowRun the source of truth for issue progress UI while preserving the #181 task/check display semantics.
- Preserve `stage_executions`, `workflow_log`, session logs, and checkpoints as separate evidence and resume-cursor layers.
- Keep compatibility with current stage-state consumers while implementation migrates incrementally.

**Non-Goals:**

- No `workflow.yaml`, pipeline DSL, DAG, matrix, fallback chain, or general workflow-definition engine.
- No first-class policy, decision, or approval model beyond an approval snapshot on StageRun.
- No promotion of logs, agent session events, thoughts, or diagnostic evidence to visible tasks.
- No change to the established Task/Check boundary: tasks perform work; checks validate.
- No full rewrite of Plan/Build/Check/Integrate runners in this change.

## Decisions

### D1: Add a thin WorkflowRun persistence model, not a pipeline DSL

Create four runtime tables:

- `workflow_runs`: `id`, `issue_id`, `issue_number`, `status`, `current_stage`, `started_by`, `created_at`, `updated_at`.
- `workflow_stage_runs`: `id`, `workflow_run_id`, `stage`, `status`, `stage_order`, approval snapshot columns, timestamps.
- `workflow_tasks`: `id`, `workflow_run_id`, `stage_run_id`, `task_id`, `title`, `status`, `task_order`, `attempts`, `artifacts`, `output`, `reason`, `caused_by`, timestamps.
- `workflow_checks`: `id`, `workflow_run_id`, `stage_run_id`, `check_name`, `title`, `status`, `message`, `output`, `run_count`, timestamps.

Use simple string ids that are stable and readable, for example `wr_<issueNumber>_<timestamp>` for a run and `<runId>/<stage>` for a StageRun. Task/check row ids should include the StageRun id plus task/check name, with an attempt or suffix only when multiple rows with the same logical name must coexist. The first implementation can keep one current row per logical task/check and rely on `attempts` / `run_count`; repeated check evidence can remain in `stage_executions` until a later history model is needed.

**Alternatives considered:** Store WorkflowRun as JSON on `issues`; rejected because querying, partial updates, and UI projection become fragile. Introduce a full workflow-definition schema; rejected because this issue only needs runtime materialization for the built-in pipeline.

### D2: Centralize runtime state mutations in `WorkflowRunService`

Add `WorkflowRunRepo` for table-level persistence and `WorkflowRunService` for workflow-specific operations:

- `startRun(issue, startedBy)` creates the run, default StageRuns, initial Plan tasks, and initial Plan checks idempotently.
- `getActiveRunForIssue(issueId)` returns the current run with nested stages/tasks/checks.
- `ensureStageRun(runId, stage)` guarantees the StageRun exists before a runner writes task/check state.
- `setCurrentStage(runId, stage)` updates run current stage and stage statuses together.
- `upsertTask(runId, stage, task)` writes work state and explanation metadata.
- `upsertCheck(runId, stage, check)` writes validation state.
- `setApproval(runId, stage, approval)` stores the approval snapshot.
- `materializeBuildTasks(runId, tasksFile)` creates Build task rows from approved `tasks.json`.

Runner and API code should call the service, not table-specific repos directly. The service hides id generation, default seed definitions, JSON serialization, stage ordering, and compatibility mirroring.

**Alternatives considered:** Extend `StageStateService` to become WorkflowRun; rejected because it would preserve the issue+stage projection as the conceptual root and make the new model harder to reason about. Let each runner write raw WorkflowRun tables; rejected because Plan/Build/Check/Integrate would duplicate id, status, and seed logic.

### D3: Start creates or reuses exactly one active run for the issue

`mo issue start <number>` and `POST /api/issues/:number/start` should create the active WorkflowRun before runner execution advances the issue. Starting an already-started issue should reuse the active non-terminal run rather than create a duplicate. A new run should only be created for an explicit future rerun/reopen policy, not as an accidental side effect of resume.

The initial run has:

- `status = running`
- `currentStage = plan`
- StageRuns for `plan`, `build`, `check`, `integrate`, with Plan running and later stages pending
- Plan tasks: `proposal`, `specs`, `design`, `tasks`, `self-review`
- Plan checks: `proposal-complete`, `specs-complete`, `design-complete`, `tasks-valid`, `self-review-passed`, `user-approval`

**Alternatives considered:** Create StageRuns lazily only when each stage starts; rejected because the UI and API should be able to show the full run skeleton immediately. Always create a new run on every start/resume; rejected because it breaks stable run identity and makes resume ambiguous.

### D4: WorkflowRun becomes canonical; stage-state becomes compatibility projection

For the first implementation, keep `stage_states`, `stage_tasks`, and `stage_checks` because existing UI hooks and tests depend on `/issues/:number/stage-state`. The write path should move toward WorkflowRun first, then optionally mirror to stage-state during the transition. The read path should prefer WorkflowRun and can expose either:

- a new `/api/issues/:number/workflow-run` endpoint for native WorkflowRun data, plus
- a compatibility `/stage-state` response built from WorkflowRun when a run exists.

This keeps the UI migration small and avoids requiring every consumer to switch in one step. Once the UI reads the native endpoint, the stage-state endpoint can remain as a compatibility facade.

**Alternatives considered:** Replace `/stage-state` immediately; rejected because it increases regression risk in Issue Detail. Continue treating stage-state as canonical and wrap it as a run; rejected because the product invariant says WorkflowRun owns StageRuns, Tasks, and Checks.

### D5: Evidence and resume cursor remain separate layers

`stage_executions` should continue recording attempt-level task/check result evidence. `workflow_log` and session stream logs should continue recording events and agent process evidence. `pipeline_checkpoint` should continue answering only where resume can safely continue.

WorkflowRun status should be updated by workflow lifecycle events, not reconstructed from logs or checkpoints. The UI can link to evidence for diagnostics, but the primary stage/task/check state comes from WorkflowRun.

**Alternatives considered:** Derive WorkflowRun from `stage_executions` and logs on read; rejected because event streams are evidence, not durable current state, and replay semantics are not defined. Store checkpoint fields on WorkflowRun; rejected because resume safety is a separate concern and can change independently of visible progress.

### D6: Materialize Build tasks at the artifact boundary

Plan owns producing and validating `tasks.json`. Once `tasks.json` exists and is accepted by Plan checks, `WorkflowRunService.materializeBuildTasks` should create Build task rows in the active run. Build execution then updates those materialized rows rather than treating `tasks.json` as the user-facing task store.

The existing Ralph executor can continue using `tasks.json` as its execution input. Synchronization hooks should update WorkflowRun task status from Ralph progress, then mirror to stage-state if required.

**Alternatives considered:** Stop using `tasks.json` for Build execution immediately; rejected because it would force a larger Ralph executor rewrite. Keep Build tasks visible only from `tasks.json`; rejected because the run would not own all runtime tasks.

### D7: Runtime-added work is just a task with explanation metadata

Repair, rebase, retry, conflict resolution, and rerun work should call `upsertTask` / `appendTask` on the active WorkflowRun for the current StageRun. These tasks use the same status and ordering model as initial tasks. `reason` and `causedBy` explain why the task exists; UI must not expose origin as a user-facing category.

Use a bounded `causedBy.type` vocabulary matching product language: `check-failure`, `task-failure`, `branch-changed`, `conflict`, `retry`, `user-action`, `system-policy`. Existing internal labels such as health-gate failure or merge conflict should normalize into these values at the WorkflowRun boundary.

**Alternatives considered:** Add separate dynamic task tables or categories; rejected because it reintroduces the planned/dynamic/static split that users should not interpret. Model repair policy and decision as first-class records; rejected as out of scope for the first version.

### D8: UI reads run state but preserves current visual semantics

Update frontend types and hooks to load WorkflowRun-backed data. `PipelineView` and `TaskProgressPanel` should continue rendering one task list and one check list per stage. Approval remains separate state. Logs, sessions, check evidence, and diagnostic details remain supporting information.

During migration, the UI can either consume the compatibility `stage-state` shape backed by WorkflowRun or switch to a native WorkflowRun hook and locally adapt to the existing component props. Prefer a thin adapter in the frontend data layer so component rendering logic does not need to know whether data came from native WorkflowRun or compatibility projection.

**Alternatives considered:** Rewrite the Issue Detail pipeline components around the new schema immediately; rejected because #181 already stabilized the semantics and a large UI rewrite would risk reintroducing placeholder/check/log mixing.

## Risks / Trade-offs

- [Risk] Dual write paths can diverge between WorkflowRun and stage-state during migration. → Mitigation: make `WorkflowRunService` the primary write API and keep stage-state mirroring inside that service or a single adapter; add tests that compare compatibility output against native run data.
- [Risk] Starting or resuming an issue could accidentally create duplicate active runs. → Mitigation: enforce one non-terminal active run per issue in repository queries and start-flow tests; make start idempotent for existing active runs.
- [Risk] Build task materialization can become stale if `tasks.json` changes after initial materialization. → Mitigation: materialize after Plan validation, upsert by task id, and rerun materialization whenever Plan regenerates `tasks.json` before Build starts.
- [Risk] Repeated check attempts may need more history than one current `workflow_checks` row. → Mitigation: keep current effective check state in WorkflowRun and use `stage_executions` for attempt evidence in this version; revisit check-attempt rows only if UI requirements demand first-class attempt history.
- [Risk] Existing runner-local task/check definitions remain duplicated with WorkflowRun seeds. → Mitigation: place seed definitions in `WorkflowRunService` or a small `workflow-run-definitions` module and have runners reference the same names where practical.
- [Risk] Status mapping between issue status, run status, and stage status can be inconsistent. → Mitigation: centralize lifecycle updates in `WorkflowRunService.setCurrentStage`, `setRunStatus`, and approval helpers rather than scattering direct updates.

## Migration Plan

1. Add database migrations for WorkflowRun tables and indexes. Include an active-run lookup index on `issue_id` and stage/task/check indexes by `workflow_run_id` and `stage_run_id`.
2. Add TypeScript runtime types, `WorkflowRunRepo`, and `WorkflowRunService` with nested read models and idempotent `startRun` behavior.
3. Wire `AgentRunnerService` / issue start handling to create or reuse the active WorkflowRun before invoking `WorkflowEngine`.
4. Add WorkflowRun to `StageContext` so `BaseStageRunner` and concrete runners can update run state without looking it up repeatedly.
5. Update `BaseStageRunner` task/check/approval/status mirroring to write WorkflowRun first, then mirror to stage-state for compatibility.
6. Materialize Plan seed tasks/checks during run creation and materialize Build tasks from `tasks.json` after Plan produces and validates it.
7. Update dynamic repair/rebase/retry/conflict code paths to append explained WorkflowRun tasks with normalized `causedBy` metadata.
8. Add `GET /api/issues/:number/workflow-run` and adapt `/api/issues/:number/stage-state` to project from WorkflowRun when present.
9. Update frontend API types/hooks and Issue Detail data flow to prefer WorkflowRun-backed data while preserving current task/check rendering behavior.
10. Add tests for start creation, idempotent start/resume, Plan seed data, Build task materialization, dynamic task append metadata, API response shape, UI consistency, and separation from checkpoints/log evidence.

Rollback strategy: because this change adds new tables and keeps existing stage-state/checkpoint/log structures, disabling WorkflowRun reads and returning to the existing `/stage-state` projection should restore previous behavior. New WorkflowRun tables can remain unused until the issue is fixed forward.

## Open Questions

- Should WorkflowRun ids be deterministic per issue start (`wr_<issueNumber>_<createdAt>`) or opaque UUIDs with a display alias? The implementation only requires stability and uniqueness.
- Should native WorkflowRun check data expose only current effective checks in v1, or also include repeated attempts by reading `stage_executions` into an evidence field?
- What explicit user action should create a new WorkflowRun after a completed or failed run: reopen, rerun whole issue, or a future separate command?
