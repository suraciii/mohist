## Why

The issue detail page presents progress through six fragmented components (IssueTimeline, TaskList, CheckSuitePanel, CheckResultsPanel, Review Report sidebar, Approval Required sidebar) that force users to mentally reconstruct what happened. Underneath, "Task" means four different things across Plan/Build/Check stages — `RoundConfig` in Plan/Check vs `Task` in Build vs the `task_results` aggregation column — and SSE events use three independent schemas (`plan_round_start`, `ralph_task_update`, `plan_round_complete`). This makes the frontend maintain three parallel rendering paths for what is conceptually the same thing: an Agent completing a unit of work. Now that #127 has delivered `BaseStageRunner` + `Check` model + `stage_executions` table, the foundation exists to unify the domain model and replace the fragmented UI with a single CI/CD-style Pipeline View.

## What Changes

- Introduce `StageTask` and `StageTaskResult` as the unified Task type across all stages — Plan's 5 rounds, Build's DAG tasks, and Check's 2 review rounds all map to this single interface
- Rename `RoundConfig` → `TaskConfig` in Plan and Check stage runners; treat each round as a Task conceptually
- **BREAKING**: Change `stage_executions.task_results` column from `unknown` aggregation to `StageTaskResult[]` — each task writes one structured record on completion
- Add unified SSE event `stage_task_update` emitted by all three stages; old events (`plan_round_start`, `ralph_task_update`, `plan_round_complete`) continue emitting for backward compatibility
- Add API endpoint `GET /api/issues/:number/executions` returning structured stage execution data with per-task results and check results
- Replace IssueTimeline, TaskList, CheckSuitePanel, CheckResultsPanel, Review Report sidebar, and Approval Required sidebar with a single Pipeline View composed of:
  - **Stage Bar**: horizontal Plan → Build → Check → Done with status icons and timing
  - **Step List**: expandable Tasks + Checks sections under the active/completed stage
  - **Inline Approval**: user-approval check rendered inline in the step list
- Handle special issue states (backlog, blocked, interrupted, completed, closed) in the Pipeline View

## Capabilities

### New Capabilities

- `unified-stage-task` — Unified `StageTask` / `StageTaskResult` domain model, `TaskConfig` rename, and structured `task_results` storage
- `stage-task-sse-events` — Unified `stage_task_update` SSE event emitted by all stages
- `stage-executions-api` — `GET /api/issues/:number/executions` endpoint exposing structured stage execution data
- `pipeline-view` — CI/CD-style Pipeline View UI component (Stage Bar + Step List + Inline Approval) replacing six fragmented components

### Modified Capabilities

- `pipeline-session-events` — Add `stage_task_update` to SSE event registrations; existing `plan_round_start` and `plan_session_update` remain unchanged
- `session-timeline-ui` — Replaced by `pipeline-view`; SessionTimeline component removed in favor of Step List
- `http-api` — New `GET /api/issues/:number/executions` endpoint added to issue routes
- `ralph-task-execution` — Build tasks emit `stage_task_update` in addition to existing `ralph_task_update`; `task_results` written as `StageTaskResult[]`

## Impact

**Backend (packages/cli/src/):**
- `workflow/base-stage-runner.ts` — `persistTaskResults` writes `StageTaskResult[]` instead of raw `unknown`
- `workflow/plan-stage-runner.ts` — `RoundConfig` → `TaskConfig`, emit `stage_task_update` per round, write per-task results
- `workflow/check-stage-runner.ts` — Same as plan: `RoundConfig` → `TaskConfig`, emit `stage_task_update`, per-task results
- `workflow/build-stage-runner.ts` — Map Ralph task completion to `StageTaskResult`, emit `stage_task_update`
- `workflow/stage-context.ts` — Add `StageTask` and `StageTaskResult` type definitions
- `db/stage-execution-repo.ts` — `taskResults` typed as `StageTaskResult[]`, add `findByIssueId` query method
- `api/issues.ts` — New `GET /:number/executions` route
- `services/event-bus.ts` — Register `stage_task_update` event type

**Frontend (packages/cli/web/src/):**
- New `PipelineView` component (Stage Bar + Step List + Inline Approval)
- `IssueDetailPage.tsx` — Replace IssueTimeline/TaskList/CheckSuitePanel/CheckResultsPanel with PipelineView
- Delete: `IssueTimeline.tsx`, `TaskList.tsx`, `CheckSuitePanel.tsx`, `CheckResultsPanel.tsx`
- SSE hooks — Add `stage_task_update` listener
- API client — Add `getIssueExecutions` method

**Database:** No schema changes — `stage_executions.task_results` JSON column structure changes, no column additions

**Dependencies:** No new external dependencies
