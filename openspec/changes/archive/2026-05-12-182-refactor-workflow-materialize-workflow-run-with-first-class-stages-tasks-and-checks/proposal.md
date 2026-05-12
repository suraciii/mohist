## Why

Mohist currently answers “where is this issue run and what happened?” by stitching together issue stage fields, stage-state projections, stage executions, logs, `tasks.json`, checkpoints, and runner-local definitions. A first-class WorkflowRun is needed now so issue start, UI progress, resume, rerun, repair, approval, and audit flows share one runtime source of truth while logs and checkpoints keep their narrower evidence and cursor roles.

## What Changes

- Create a thin `WorkflowRun` runtime root when an issue is started, with a stable run id, issue id/number binding, run status, current stage, timestamps, starter metadata, and ordered `StageRun` children.
- Materialize default `StageRun` records for `plan`, `build`, `check`, and `integrate` under the same run instead of treating per-stage rows as disconnected issue projections.
- Materialize initial Plan tasks and checks into the WorkflowRun at start: `proposal`, `specs`, `design`, `tasks`, `self-review`, and the Plan validation/approval checks.
- Materialize Build tasks from the approved `tasks.json` into the same WorkflowRun when Plan produces the task file.
- Append runtime-added repair, rebase, retry, and conflict-resolution work as ordinary WorkflowRun tasks in the current stage, with `reason` and `causedBy` metadata rather than a user-visible planned/dynamic/static category.
- Expose WorkflowRun data through the backend API so clients can query the current run, stage runs, tasks, checks, and approval snapshot for an issue.
- Update Issue Detail progress rendering to use WorkflowRun as the source of truth while preserving the existing page semantics of one task list and one check list per stage.
- Keep `stage_executions`, `workflow_log`, and session logs as evidence/audit data, not primary current-state data.
- Keep checkpoint data as the resume cursor, not as WorkflowRun state or the canonical progress model.
- Do not introduce `workflow.yaml`, a full pipeline DSL, first-class policy/decision records, parallel DAG semantics, or agent session internals as visible tasks.

## Capabilities

### New Capabilities

- workflow-run

### Modified Capabilities

- workflow-engine
- pipeline-model
- http-api
- web-ui

## Impact

- Database schema and repositories: add WorkflowRun/StageRun/Task/Check persistence while preserving existing `stage_states`, `stage_tasks`, `stage_checks`, `stage_executions`, `workflow_log`, session logs, and `pipeline_checkpoint` compatibility during the first version.
- Issue start and runner orchestration: `mo issue start` / `POST /api/issues/:number/start`, `AgentRunnerService`, and `WorkflowEngine` must create or resolve the active WorkflowRun and pass it through stage execution.
- Stage runners and task/check mirroring: Plan, Build, Check, Integrate, `BaseStageRunner`, `StageStateService`, and `ChangeArtifactsManager.syncTasksToStageState` need to write task/check status to WorkflowRun rather than relying on runner-local definitions or stage-state projection as the canonical model.
- Build task materialization: Ralph/task execution integration must copy approved `tasks.json` items into the existing WorkflowRun and keep subsequent task status synchronized.
- Dynamic work tracking: health fixes, review repairs, merge repairs, rebases, retries, and conflict resolution must append explained task instances to the active WorkflowRun.
- API surface: add WorkflowRun query support and adapt existing stage-state responses or introduce a replacement endpoint so clients can read run status, current stage, StageRuns, tasks, checks, and approval snapshots from one model.
- Web UI: `PipelineView`, `TaskProgressPanel`, related hooks/API client types, and consistency tests should consume WorkflowRun-backed data while keeping tasks, checks, approval, and diagnostic evidence visually separate.
- Tests and migration coverage: add repository, start-flow, materialization, API, and UI tests covering stable run id creation, Plan seed tasks/checks, Build task materialization, dynamic task append metadata, and continued separation of evidence logs and checkpoints.
