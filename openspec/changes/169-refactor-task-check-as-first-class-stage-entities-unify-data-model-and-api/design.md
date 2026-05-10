## Context

Issue Detail currently has multiple definitions of task/check progress. `TaskProgressPanel` reads `tasks.json` through `/tasks` and `/build-status`, while `PipelineView` reads `stage_executions` through `/executions` and merges those results with hardcoded task templates. `check_suites` is a third partial model for a fixed set of checks. These paths use incompatible schemas and lifecycles: `tasks.json` is updated in place, `stage_executions` appends one row per stage attempt, and `check_suites` stores a fixed check map.

The main design constraint is to preserve workflow behavior and audit history while changing the durable current-state model and read APIs. `stage_executions` should remain useful for attempt history, but it must no longer be the source of truth for the current stage task/check state.

## Goals / Non-Goals

**Goals:**

- Provide one authoritative current-state model for stage tasks, checks, and approval state keyed by `(issue_id, stage)`.
- Normalize task/check status names so UI code does not translate `passes`, `completed`, `pass`, and related variants in multiple places.
- Let runners update the same task/check record across retries instead of making the UI reconstruct current state from multiple execution rows.
- Move static stage task definitions to backend-owned data and combine them with dynamic task results before returning state to the UI.
- Keep `stage_executions` as append-only audit history and preserve existing execution evidence.
- Migrate Issue Detail primary rendering to a single stage-state API.

**Non-Goals:**

- Do not change stage transition rules or workflow orchestration.
- Do not change plan/build/check/integrate execution behavior beyond where task/check state is recorded.
- Do not implement fallback or cross-stage dependency behavior.
- Do not remove execution history APIs if they are still used for audit/debug views.
- Do not require the frontend to aggregate execution attempts to infer current status.

## Decisions

### D1: Store Current Stage State Separately From Execution History

Create a current-state persistence layer with tables for stage state, stage tasks, and stage checks:

- `stage_states`: one row per `(issue_id, stage)` with stage status, timestamps, and approval metadata.
- `stage_tasks`: one row per `(issue_id, stage, task_id)` with title, normalized status, attempts, duration, artifacts, output, source, order, and timestamps.
- `stage_checks`: one row per `(issue_id, stage, check_name)` with normalized status, message, output, attempts or run count, and timestamps.

Use `UNIQUE(issue_id, stage)` for `stage_states` and unique keys on task/check identity for update-in-place semantics. Store large or variable fields such as artifacts and output as JSON text, following existing repository patterns.

This makes task/check current state directly queryable without scanning or reconciling `stage_executions`. Execution rows can still be created for every stage attempt and can still contain task/check snapshots for audit compatibility.

**Alternatives considered:** Keeping current state inside the latest `stage_executions` row was rejected because retries split state across rows and would keep audit and current-state concerns coupled. Storing all current state as JSON columns in a single `stage_states` table was rejected because task/check upserts, indexing, and dynamic task insertion are clearer with first-class rows.

### D2: Introduce `StageStateService` as the Write/Read Boundary

Add a backend service/repository boundary responsible for all current-state operations:

- `ensureStage(issueId, stage)` creates or refreshes the current stage row and seeds static task/check definitions.
- `upsertTask(issueId, stage, task)` updates the current task by identity.
- `upsertCheck(issueId, stage, check)` updates the current check by identity.
- `setApproval(issueId, stage, approval)` records approval state for the stage.
- `getIssueStageState(issueId)` returns all stage states needed by Issue Detail.
- `getStageState(issueId, stage)` returns a single stage state when a narrower query is useful.

The service hides migration/projection details from routes and UI. Stage runners call it alongside existing `StageExecutionRepo` writes, so the audit path and current-state path remain separate.

**Alternatives considered:** Letting API routes aggregate `tasks.json`, `stage_executions`, and `check_suites` on demand was rejected because it would move complexity into the read path and keep UI-visible behavior dependent on historical inconsistencies. Updating `StageExecutionRepo` to have current-state helpers was rejected because the name and table remain audit-oriented.

### D3: Normalize the Stage-State API Shape

Add a primary endpoint such as `GET /api/issues/:number/stage-state` returning all stages for the issue:

```ts
type StageTaskStatus = 'pending' | 'running' | 'completed' | 'failed' | 'skipped'
type StageCheckStatus = 'pending' | 'running' | 'passed' | 'failed' | 'error'

interface IssueStageStateResponse {
  issueId: string
  issueNumber: number
  stages: StageState[]
}

interface StageState {
  stage: Stage
  status: 'pending' | 'running' | 'awaiting-approval' | 'passed' | 'failed' | 'skipped'
  tasks: StageTaskState[]
  checks: StageCheckState[]
  approval: StageApprovalState | null
  updatedAt: string
}
```

Tasks use `completed`; checks use `passed`. Conversion from existing internal check result values (`pass`, `fail`, `error`, `pending`) happens once in `StageStateService`, not in UI components.

Keep `/executions` for audit/history consumers. Keep `/tasks` and `/build-status` only as compatibility endpoints during migration or reimplement them as projections from stage-state; Issue Detail should not use them for primary task progress.

**Alternatives considered:** Returning only the current issue stage was rejected because PipelineView displays multiple stages and needs a consistent timeline. Reusing `/executions` for the new shape was rejected because it would blur audit history and current state under one endpoint name.

### D4: Backend Owns Stage Task Definitions

Move static task definitions for plan, check, and integrate into backend code near the stage-state service or workflow configuration layer. Seed those definitions when a stage begins or when state is requested and the current-state rows are missing.

Build tasks remain sourced from `tasks.json` because they are generated by the plan artifact, but they are imported into `stage_tasks` so the UI sees the same normalized shape as every other stage. Dynamic fix tasks are inserted through the same `upsertTask` path and are not filtered by a static template.

**Alternatives considered:** Keeping `PLAN_TASK_DEFS`, `CHECK_TASK_DEFS`, and `INTEGRATE_TASK_DEFS` in the frontend was rejected because it duplicates workflow knowledge and hides dynamic backend-created tasks. Generating all definitions purely from execution history was rejected because pending tasks would be invisible until they complete.

### D5: Runners Write Both Audit Snapshots and Current State

When a stage starts, `BaseStageRunner.run()` creates a `stage_executions` row as it does today and also calls `StageStateService.ensureStage()`. During task/check execution:

- `appendTaskResult()` becomes a wrapper that records the result in `stage_executions` for audit and upserts normalized current task state.
- `persistCheckResults()` continues saving the execution snapshot and also upserts each current check result.
- Approval checks update both current check state and stage approval state.
- Retry/fix paths update the same task/check current rows and increment attempts or run count rather than creating duplicate current rows.

Build-stage task progress currently written to `tasks.json` should continue for artifact compatibility, but the runner or task executor must mirror changes into `stage_tasks`. The UI should no longer read `tasks.json` directly for current task status.

**Alternatives considered:** Replacing `stage_executions` writes entirely was rejected because execution history is an explicit requirement and useful for debugging. Deferring current-state writes until the end of a stage was rejected because the UI needs live progress and failed/interrupted stages need partial evidence.

### D6: Frontend Uses One Query for Primary Progress Rendering

Add a frontend API client method and React Query hook for stage state, for example `api.getIssueStageState(number)` and `useIssueStageState(number)`. `PipelineView` and `TaskProgressPanel` should both read from this hook.

`PipelineView` should stop calling `executions.find(e => e.stage === stage)` for current task/check rendering. It may keep execution data behind an explicit history/debug section if desired. `TaskProgressPanel` should stop preferring `/build-status` over `/tasks`; it should render the current build or current issue stage tasks from the stage-state response.

**Alternatives considered:** Keeping separate hooks and trying to synchronize query invalidation was rejected because it preserves the possibility of contradictory views. Putting all normalization in frontend selectors was rejected because the backend owns workflow semantics.

## Risks / Trade-offs

- [Risk] Existing active issues may have no `stage_states` rows when the new UI asks for stage state. → Mitigation: implement lazy projection in `StageStateService` from existing `tasks.json`, latest/known `stage_executions`, and active `check_suites` when rows are missing, then persist the projected state.
- [Risk] Projection from old `stage_executions` can still be incomplete because historical rows may split checks and tasks. → Mitigation: prefer current-state rows once created; for legacy projection, scan executions in chronological order and upsert by task/check identity so later attempts win.
- [Risk] Status normalization can break existing UI assumptions. → Mitigation: define shared backend/frontend types and keep conversion functions localized; update tests for all status mappings.
- [Risk] Mirroring `tasks.json` and stage-state during migration can temporarily create two write paths. → Mitigation: make stage-state the primary UI read path immediately, keep `tasks.json` writes only for artifact compatibility, and add tests that both are updated for build tasks.
- [Risk] Separate task/check tables add migrations and repository code. → Mitigation: keep the repository API narrow and store variable data as JSON text to avoid over-normalizing artifacts/output.
- [Risk] Repeated check attempts are no longer shown in the primary current-state list. → Mitigation: current state shows latest status and attempt count; detailed attempt chronology remains available through `stage_executions` audit history.

## Migration Plan

1. Add the new stage-state schema migration and repository/service classes.
2. Add static backend task definitions for plan, check, and integrate stages.
3. Wire `StageStateService` into stage runner context creation.
4. Update `BaseStageRunner` task/check/approval persistence helpers to write current state while preserving `stage_executions` writes.
5. Mirror generated build tasks from `tasks.json` into `stage_tasks` when tasks are read, created, or updated.
6. Add `GET /api/issues/:number/stage-state` and compatibility projections for `/tasks` and `/build-status` if those endpoints must remain available.
7. Add frontend types, API method, and `useIssueStageState` hook.
8. Migrate `PipelineView` and `TaskProgressPanel` to consume stage state; keep `/executions` only for audit/history UI.
9. Add tests for retry scenarios, dynamic fix tasks, status normalization, legacy projection, and Issue Detail consistency.

Rollback strategy: because `stage_executions`, `tasks.json`, and `check_suites` are preserved during migration, rollback can restore the previous UI/API reads while leaving unused stage-state tables in place. Avoid destructive migrations or deleting historical fields in this change.

## Open Questions

- Should execution history be exposed in the same Issue Detail section as an expandable audit view, or left only on the existing `/executions` API for later UI work?
- Should `/tasks` and `/build-status` be formally deprecated in this change, or kept indefinitely as stage-state projections for older clients?
