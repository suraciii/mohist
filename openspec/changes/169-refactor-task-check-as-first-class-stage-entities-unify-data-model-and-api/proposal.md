## Why

Issue Detail currently renders task and check progress from multiple incompatible state sources, so the same issue can show contradictory progress depending on whether the UI reads `tasks.json`, `stage_executions`, or `check_suites`. This is urgent because stage retries append new execution rows while the UI often reads the first execution snapshot, causing current task/check state to be stale or missing for retried issues.

## What Changes

- Make Task and Check current state first-class stage data keyed by issue and stage, rather than deriving current UI state from execution history.
- Add a unified stage-state read model that exposes each stage's current tasks, checks, approval state, status, attempts, durations, artifacts, and output using one normalized status schema.
- Update stage task/check write paths to update the current Task or Check in place across retries while preserving `stage_executions` as append-only attempt history.
- Consolidate existing task/check APIs so Issue Detail can use one stage-state query path instead of independently reading `/tasks`, `/build-status`, `/executions`, and `checkSuite` for overlapping progress data.
- Move stage task definitions out of front-end hardcoded templates and have the backend provide static and dynamic stage tasks in the same response.
- Update PipelineView and TaskProgressPanel to render from the same backend stage-state source, including dynamically introduced fix tasks such as `fix-check-health`.
- Preserve auditability by keeping stage execution history available separately from the current-state model.
- **BREAKING**: Consumers that rely on `stage_executions.task_results` or `stage_executions.check_results` as the authoritative current task/check state must switch to the stage-state API/read model.

## Capabilities

### New Capabilities


### Modified Capabilities

- pipeline-model
- http-api
- web-ui

## Impact

- Backend storage: add or refactor persistence for per-issue, per-stage current Task and Check state; keep `stage_executions` as audit history rather than the source of current UI state.
- Backend repositories and services: update `StageExecutionRepo` usage patterns, stage runner task/check result recording, check-suite interactions, and any current-state aggregation code that reads task/check data.
- Workflow runners: adjust plan, build, check, integrate, repair, and health-gate result writes from append-only current-state semantics to update-in-place current stage entities while still recording execution attempts.
- HTTP API: introduce or standardize a stage-state endpoint and migrate Issue Detail clients away from overlapping `/tasks`, `/build-status`, `/executions`, and check-suite progress reads for primary progress rendering.
- Frontend UI: update `PipelineView`, `TaskProgressPanel`, related hooks, and shared types to consume one normalized task/check schema and remove hardcoded `PLAN_TASK_DEFS`, `CHECK_TASK_DEFS`, and `INTEGRATE_TASK_DEFS` as the source of truth.
- Data compatibility: existing `tasks.json`, `check_suites`, and `stage_executions` data may need migration or projection into the new current-state model so active issues retain visible progress.
- Testing: add coverage for multi-execution retry scenarios, dynamic fix tasks, normalized task/check statuses, and consistency between PipelineView and TaskProgressPanel.
