## Context

The issue detail page (`IssueDetailPage.tsx`) renders progress through 6 independent components: `IssueTimeline` (timeline view), `TaskList` (build tasks), `CheckSuitePanel` (check suite), `CheckResultsPanel` (check results + approval), `PlanApprovalPanel` (plan approval), and an inline "Approval Required" div block. These components are scattered across a two-column grid layout — the left column has IssueTimeline, TaskList; the right column has CheckSuitePanel, CheckResultsPanel, PlanApprovalPanel, and the approval block.

On the backend, three stage runners exist: `PlanStageRunner` (5 rounds via `RoundConfig`), `BuildStageRunner` (dynamic tasks via `RalphExecutor`), and `CheckStageRunner` (2 rounds via `RoundConfig`). They share `BaseStageRunner` which manages check execution and stage lifecycle. The `stage_executions` table (from #127) stores `task_results: unknown[]` (currently raw return values) and `check_results: unknown[]`.

SSE events for progress are fragmented: `plan_round_start`/`plan_session_update` for Plan/Check, `ralph_task_update`/`ralph_loop_progress` for Build. The frontend maintains separate event listeners and data flows for each.

**Constraints:**
- No database schema changes — only JSON structure changes in existing `task_results` column
- Legacy SSE events must continue emitting for backward compatibility
- Existing check model (`Check`, `CheckResult`, reaction dispatch) is unchanged
- `BaseStageRunner.executeTasks()` abstract method signature stays the same (returns `Promise<unknown>`)
- Plan's ACP shared connection pattern and Build's DAG sorting remain internal to each runner

## Goals / Non-Goals

**Goals:**
- Unify the domain model: one `StageTask`/`StageTaskResult` type for all stages
- Replace 6 fragmented UI components with a single `PipelineView` (Stage Bar + Step List + Inline Approval)
- Add a unified `stage_task_update` SSE event emitted by all stages
- Add `GET /api/issues/:number/executions` endpoint
- Write structured per-task results incrementally to `stage_executions.task_results`

**Non-Goals:**
- Changing the `Check` model or reaction dispatch system
- Changing `BaseStageRunner.executeTasks()` signature
- Adding new database tables or columns
- Streaming agent text/tool-call content in the PipelineView (handled by existing session viewer)
- Handling Done stage tasks (system-level, no agent tasks)

## Decisions

### D1: Type definitions live in `stage-context.ts`

`StageTask` and `StageTaskResult` interfaces are defined in `stage-context.ts` alongside the existing `StageContext`, `StageRunResult`, `CheckResult`, and `ReactionConfig` types. This co-locates the domain model with the stage execution context that already imports it from all stage runners.

**Alternatives considered:**
- Separate `types/pipeline.ts` file — adds indirection for types only used by workflow internals
- `types/index.ts` — already contains `Stage` enum and `Issue` type; adding pipeline domain types would mix concerns

### D2: Incremental task result persistence via `StageExecutionRepo.appendTaskResult`

Rather than each stage runner reading-merging-writing the entire `task_results` array, add a single method `appendTaskResult(executionId, result: StageTaskResult)` that reads the current array, appends, and writes back. This encapsulates the read-modify-write in one place and avoids duplication across runners.

The method does:
1. `findById(id)` to get current `taskResults`
2. Append new `StageTaskResult`
3. `updateTaskResults(id, merged)` to write back

**Alternatives considered:**
- Pass `StageExecutionRepo` into each runner's task loop — already available via `ctx.stageExecutionRepo`
- Have `BaseStageRunner` intercept and persist — would require a callback/hook pattern that couples the base class to task-level timing
- Use a separate `task_results` table — rejected per "no schema changes" constraint

### D3: `stage_task_update` emitted alongside legacy events, not replacing them

Each emission point adds a `context.eventBus.emit('stage_task_update', ...)` call right next to the existing `ralph_task_update` or `plan_round_start` emit. The two events have different schemas and serve different consumers. Legacy events power existing components (IssueTimeline, TaskList) during the transition; the new unified event powers PipelineView.

Legacy events will be removed in a future cleanup once PipelineView is the sole consumer.

**Alternatives considered:**
- Replace legacy events immediately — risky, would break existing frontend during incremental rollout
- Emit only `stage_task_update` and derive legacy events from it — adds translation layer complexity

### D4: Plan/Check runners emit `stage_task_update` in the task loop, not in a shared base method

Each stage runner's `executeTasks()` loop emits `stage_task_update` directly. The reason: the timing and metadata differ per stage. Plan needs the `TaskConfig.type` as `taskId`; Build needs the tasks.json `Task.id`; Check needs the review task type. A shared base method would need a generic "task lifecycle" abstraction that's more complex than having each runner emit directly.

The shared piece is the `StageTaskResult` construction — each runner builds one from its internal data and calls `ctx.stageExecutionRepo?.appendTaskResult()`.

### D5: PipelineView as a single component with sub-components

`PipelineView` is the top-level component, rendered at the position where `IssueTimeline` currently sits (full-width, above the grid). It composes:

```
PipelineView
├── StageBar          — horizontal Plan→Build→Check→Done
├── StepList          — Tasks + Checks for selected stage
│   ├── TaskItem      — single task row (expandable)
│   ├── CheckItem     — single check row
│   └── InlineApproval — approve/reject panel (renders inside Checks)
└── SpecialStatePanel — backlog Start / blocked banner / interrupted Resume
```

All sub-components live in the same `PipelineView.tsx` file (or a `pipeline/` directory if they exceed ~200 lines each). State management uses a simple `useState<string>(selectedStage)` for which stage is active in the Step List.

**Alternatives considered:**
- Separate files per sub-component — premature splitting; they share significant types and state
- Context provider for shared state — over-engineering for a parent-child prop drill

### D6: PipelineView data from `useIssueExecutions` hook + SSE overlay

On mount, `PipelineView` fetches all executions via `GET /api/issues/:number/executions` using `useIssueExecutions` hook. The hook returns `StageExecution[]` with `taskResults: StageTaskResult[]` per stage.

For real-time updates, the component subscribes to `stage_task_update` SSE events. When an event arrives for the current issue, it invalidates the `useIssueExecutions` query key to trigger a refetch. This follows the existing pattern in `useSSE.tsx` where SSE events invalidate React Query caches.

For running-task elapsed time display, the component tracks `startedAt` from the task result and computes elapsed client-side with a `setInterval` (matching the existing `useLiveTask` pattern).

**Alternatives considered:**
- Optimistic local state updates from SSE without refetch — creates stale data risk if SSE events are missed or arrive out of order
- Polling only — too slow for real-time feel; SSE is already available
- Full state machine in the hook — over-engineering; query invalidation is sufficient

### D7: Stage status derivation from execution records

The Stage Bar derives each stage's status from the execution data:

```
stageStatus(stage, executions, issue):
  - If issue.stage === stage AND no execution → 'running' (just started)
  - If execution exists with status 'running' → 'running'  
  - If execution exists with status 'awaiting-approval' → 'awaiting-approval'
  - If execution exists with status 'passed' → 'completed'
  - If execution exists with status 'failed' → 'failed'
  - If stage is after current issue.stage → 'pending'
```

This is computed in the component from `useIssueExecutions` data + `issue.stage`. No new backend logic needed.

**Alternatives considered:**
- Backend returns derived status — adds coupling; frontend has all the data already
- Use `issue.stage` only — loses failed/awaiting nuance

### D8: `RoundConfig` → `TaskConfig` rename is cosmetic, not structural

The `TaskConfig` interface has the same fields as `RoundConfig` (`type`, `label`, `outputPath`, `verifyArtifact`, `buildPrompt`). Only the interface name and local variable names change (`rounds` → `taskConfigs`). The internal loop logic, ACP connection sharing, retry, and checkpoint mechanisms are untouched.

This avoids the risk of breaking the proven execution flow while achieving the naming alignment goal.

## Risks / Trade-offs

**[Risk: Incremental task_results write has race conditions]** → `appendTaskResult` reads and writes in a single call. Since pipeline stages run single-threaded (one stage at a time), there's no concurrent write risk within a single issue. The method is not safe for parallel writes to the same execution record, but the architecture guarantees this won't happen.

**[Risk: PipelineView replaces 6 components at once]** → The replacement is atomic: delete all old components, add PipelineView. Since the old components have no remaining consumers, there's no gradual migration path. Mitigate by implementing backend changes first (types, SSE, API), then doing the frontend swap in one commit.

**[Risk: Legacy SSE events double the event volume]** → Each task state transition now emits two events (legacy + `stage_task_update`). The volume is low (a few events per minute during Plan, one per task completion during Build). SSE bandwidth is not a concern. Remove legacy events in a follow-up cleanup.

**[Risk: `stage_executions.task_results` structure change breaks existing consumers]** → Currently no code reads `task_results` as structured data — the column stores raw `unknown` and no consumer parses it by field. The new `StageTaskResult[]` structure is purely additive. The `StageExecution.taskResults: unknown[]` type changes to `StageTaskResult[]` but the JSON column already accepts any structure.

**[Trade-off: No tool-call-level detail in PipelineView]** → The PipelineView Step List shows task-level status only (started/completed/failed). It does not stream agent text chunks or tool calls. Users who want that detail use the existing agent session viewer. This is intentional scope reduction — the PipelineView answers "what happened at task level?", not "what did the agent type?".

## Migration Plan

**Phase 1: Backend types and data layer** (unified-stage-task)
1. Add `StageTask` and `StageTaskResult` interfaces to `stage-context.ts`
2. Rename `RoundConfig` → `TaskConfig` in `plan-stage-runner.ts` and `check-stage-runner.ts`
3. Add `appendTaskResult` and `findByIssueId` to `StageExecutionRepo`
4. Modify each stage runner to emit `stage_task_update` and write `StageTaskResult` per task

**Phase 2: SSE and API** (stage-task-sse-events + stage-executions-api)
5. Add `stage_task_update` to `EventMap` in `event-bus.ts`
6. Add `'stage_task_update'` to `ALL_EVENT_TYPES` in `api/events.ts`
7. Add `GET /:number/executions` route to `createIssueRoutes` in `api/issues.ts`

**Phase 3: Frontend Pipeline View** (pipeline-view)
8. Add `getIssueExecutions` to `api.ts` and `useIssueExecutions` to `useQueries.ts`
9. Add `'stage_task_update'` to `eventTypes` in `useSSE.tsx` and `AGENT_DETAIL_EVENTS` in `agent-events.ts`
10. Create `PipelineView` component with `StageBar`, `StepList`, `InlineApproval`
11. Replace components in `IssueDetailPage.tsx` — remove IssueTimeline, TaskList, CheckSuitePanel, CheckResultsPanel, PlanApprovalPanel, and inline approval blocks; add PipelineView
12. Delete old component files

**Phase 4: Cleanup**
13. Remove `useIssueTimeline` hook and related utilities
14. Verify `npm run build && npm test` passes

**Rollback:** Each phase is independently revertible. Phase 1–2 changes are additive (new events, new API, new data structure in existing column). Phase 3 is the breaking change (UI replacement) — revert by restoring the deleted components and the old IssueDetailPage layout.

## Open Questions

- Should `PipelineView` show a "Duration" summary for the entire pipeline (total wall-clock time from first task to last check)? The Stage Bar shows per-stage durations, but an aggregate is not specified in the specs. Can defer to a follow-up.
- The `stage_task_update` event uses `stage: Stage` (enum value). For the Check stage, the internal executionId is `review-${issue.number}` and the SSE `plan_round_start` uses `roundType: 'review'`. The `stage_task_update` should use `stage: 'check'` consistently (matching the `Stage` enum), not `'review'`. This needs care in the Check stage runner implementation.
