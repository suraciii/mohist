## Context

The issue detail page currently uses 5+ separate components (IssueTimeline, TaskList, CheckSuitePanel, CheckResultsPanel, approval sidebar) to display pipeline progress. The backend stores task results as opaque JSON in `stage_executions.task_results`, Plan/Check use `RoundConfig` internally, and Build uses `Task` from tasks.json — three different schemas for the same concept. Three separate SSE event types (`plan_round_start`, `ralph_task_update`, `plan_round_complete`) carry progress updates with inconsistent fields.

The `BaseStageRunner` abstraction (from #127) already creates `stage_executions` records and runs `Check[]` arrays. The `StageExecutionRepo` stores `task_results: unknown[]` and `check_results: unknown[]` as JSON columns. The `EventBus` is a typed pub/sub with `EventMap` defining all event payloads.

The frontend uses React + TanStack Query + SSE via `useSSE` hook. The existing RAF-based throttling pattern in `useAgentSession` can be reused.

## Goals / Non-Goals

**Goals:**
- Define `StageTask` / `StageTaskResult` as the canonical domain types for all stage work units
- Make `stage_executions.task_results` queryable per-task by storing `StageTaskResult[]`
- Unify SSE progress into a single `stage_task_update` event (additive, not replacing old events)
- Add `GET /api/issues/:number/executions` API for structured stage history
- Replace 5 fragmented frontend components with one Pipeline View (Stage Bar + Step List + Inline Approval)

**Non-Goals:**
- Changing the internal execution logic of any stage runner (Plan keeps ACP shared connection, Build keeps DAG sort, Check keeps its 2-round loop)
- Removing old SSE events — they continue to emit for backward compatibility
- Adding a new database table or column — reuse existing `stage_executions.task_results` JSON column
- Changing the Check model or Reaction system — that's stable from #127
- Rendering tool call details inside the Pipeline View — task-level status and artifacts only

## Decisions

### D1: StageTask as a view type, not an execution type

`StageTask` is a read model for the frontend, not a new execution primitive. Each stage runner keeps its internal config type (`RoundConfig` in Plan/Check → renamed to `TaskConfig`, `Task` from tasks.json in Build). The runners map their internal config to `StageTask` when emitting SSE events and recording results. This avoids a full refactor of execution logic while giving the frontend a unified type to render.

**Alternatives considered:**
- Make `StageTask` the execution type everywhere (replacing `RoundConfig` entirely): Too invasive. Plan/Check have `verifyArtifact` and `buildPrompt` closures that don't fit the `StageTask` shape. Would require restructuring the ACP shared-connection loop.
- Keep `RoundConfig` and only add `StageTaskResult`: Frontend would still need mapping logic per stage. The unified view demands a unified type.

### D2: Incremental task_results via append + full write

`BaseStageRunner.recordTaskResult(ctx, result)` reads the current `StageExecution.taskResults`, appends the new `StageTaskResult`, and writes the full array back. This is a read-modify-write on a single JSON column. No new DB column needed.

**Alternatives considered:**
- New `task_execution_results` table with one row per task: Cleaner query model, but adds migration complexity and a new repo. The JSON column is sufficient for our scale (5 tasks max in Plan, ~10 in Build).
- Write only at stage end (current behavior): Loses partial results on mid-stage failure. The spec requires incremental recording.

### D3: SSE event is additive (dual emission)

Each stage runner emits the new `stage_task_update` alongside its existing events (`plan_round_start`, `ralph_task_update`, etc.). The new event uses a unified payload with `stage`, `taskId`, `taskTitle`, `status`, `attempt`, `artifacts`. Old events are unchanged.

**Alternatives considered:**
- Replace old events with `stage_task_update` only: Breaks any existing SSE consumers (scripts, other tools). Additive is safer.
- Make `stage_task_update` a wrapper that includes old payload: Couples the new event to old schemas. Better to keep them independent.

### D4: Frontend fetches history from executions API, live from SSE

On page load, `usePipelineView` fetches `GET /api/issues/:number/executions` for all completed stage data. For the currently running stage, SSE `stage_task_update` events provide live updates. This hybrid avoids SSE replay complexity.

**Alternatives considered:**
- Reconstruct everything from workflow_log (current approach for IssueTimeline): Fragile, requires parsing raw events. The structured API is cleaner.
- SSE-only with replay buffer: Server doesn't buffer events. Would require persistent storage or replay endpoint.

### D5: PipelineView component hierarchy

```
PipelineView
├── StageBar (horizontal Plan→Build→Check→Done, click to select stage)
├── StepList
│   ├── TasksSection (list of task rows, expandable)
│   └── ChecksSection (list of check rows, inline approval)
└── usePipelineView hook (data fetching + SSE subscription + state)
```

Inline approval renders inside `ChecksSection` when a `user-approval` check has `awaiting` status. No separate sidebar needed.

### D6: Build stage records task results via onTaskCompleted callback

`BuildStageRunner` already has `onTaskCompleted` callback in the `RalphExecutor.execute()` call. We extend this callback to also call `recordTaskResult` and emit `stage_task_update`. RalphExecutor itself emits `ralph_task_update` — we add a parallel `stage_task_update` emit inside RalphExecutor's task loop where it already has the status/timing data.

## Risks / Trade-offs

[Read-modify-write on task_results column under concurrent access] → Mitigation: Only one stage runs at a time per issue. No concurrent writers to the same `stage_execution` row.

[Old components deleted, no gradual migration] → Mitigation: PipelineView is the replacement. Deploy backend + frontend together. The old SSE events still emit, so external consumers aren't affected.

[Frontend has two data sources (API for history, SSE for live)] → Mitigation: `usePipelineView` merges them. API data is the source of truth for completed stages. SSE updates only the currently running stage. On stage completion, refetch API to reconcile.

[StageTask for Build may have many tasks (20+)] → Mitigation: StepList renders a flat list. No performance concern at this scale. DAG visualization is a non-goal.

## Migration Plan

1. **Backend first** — Add `StageTask`/`StageTaskResult` types, `recordTaskResult` helper, `findByIssueId` repo method, `stage_task_update` event registration, and `GET /api/issues/:number/executions` endpoint. Deploy without frontend changes. Old events still emit. Existing frontend continues to work.

2. **Frontend second** — Build PipelineView components alongside old components. Switch `IssueDetailPage` to use PipelineView in one commit, delete old component files. This is safe because the old components are only used on IssueDetailPage.

3. **No rollback concern** — Backend changes are additive (new API endpoint, new SSE event, different JSON shape in existing column). If rollback is needed, revert frontend to old components; backend keeps working because old SSE events never stopped.

## Open Questions

- Should `StageTaskResult.duration` include retry time (total wall clock) or only last attempt? → Leaning toward total wall clock (time from first `started` to final `completed`/`failed`), as this is what users care about.
- Should `artifacts` in `StageTaskResult` store relative paths (from change dir) or absolute? → Relative to worktree root, matching how the frontend already constructs file paths.
