## Why

The issue detail page shows progress through a fragmented collection of components (IssueTimeline, TaskList, CheckSuitePanel, CheckResultsPanel, approval sidebar) that users must mentally stitch together. Underneath, "Task" means four different things across Plan/Build/Check stages — `RoundConfig` in Plan/Check vs `Task` in Build vs `task_results` as opaque JSON — forcing the frontend to maintain separate rendering logic for each stage's conceptually identical work units. A unified Task domain model + CI/CD-style Pipeline View replaces this fragmentation with a single, scannable visualization that answers "what's happening now, what's done, and what's next" within 5 seconds.

## What Changes

- **BREAKING**: Unify Plan `RoundConfig` and Check `RoundConfig` into a shared `StageTask` / `StageTaskResult` type — rename rounds to tasks across Plan and Check stage runners
- **BREAKING**: Replace `stage_executions.task_results` (opaque `unknown` JSON) with structured `StageTaskResult[]` — each task writes its own result record on completion
- Add `stage_task_update` unified SSE event emitted by all three stage runners (Plan, Build, Check), replacing the need for three separate event schemas (`plan_round_start`, `ralph_task_update`, `plan_round_complete`)
- Add `GET /api/issues/:number/executions` API endpoint returning structured stage execution data with task results and check results
- Replace IssueTimeline, TaskList, CheckSuitePanel, CheckResultsPanel, and approval sidebar with a single Pipeline View composed of: Stage Bar (horizontal Plan→Build→Check→Done), Step List (Tasks + Checks per stage), and Inline Approval
- Support all issue states (backlog, active, blocked, interrupted, completed, closed) with appropriate Pipeline View rendering
- Old SSE events (`plan_round_start`, `ralph_task_update`, `plan_round_complete`, `plan_session_update`) continue to emit for backward compatibility

## Capabilities

### New Capabilities

- `unified-stage-task-model` — `StageTask` and `StageTaskResult` interfaces as the canonical domain types for all stage work units; Plan/Check rounds and Build tasks map to this model
- `pipeline-view` — CI/CD-style frontend Pipeline View component (Stage Bar + Step List + Inline Approval) replacing the fragmented issue detail page components

### Modified Capabilities

- `pipeline-session-events` — add `stage_task_update` unified SSE event alongside existing events
- `http-api` — add `GET /api/issues/:number/executions` endpoint
- `session-timeline-ui` — Pipeline View absorbs and replaces SessionTimeline, TaskList, CheckSuitePanel, CheckResultsPanel, and approval sidebar; SessionTimeline's round-based rendering logic is replaced by Step List's unified task rendering

## Impact

**Backend (packages/cli/src/)**:
- `workflow/plan-stage-runner.ts` — `RoundConfig` → `TaskConfig` rename; emit `stage_task_update`; write individual `StageTaskResult` records
- `workflow/check-stage-runner.ts` — same `RoundConfig` → `TaskConfig` rename and event emission
- `workflow/build-stage-runner.ts` (ralph executor) — emit `stage_task_update` alongside existing `ralph_task_update`; write `StageTaskResult` records
- `workflow/base-stage-runner.ts` — add helper for `StageTaskResult` writing into `stage_executions.task_results`
- `db/stage-execution-repo.ts` — `taskResults` type narrows from `unknown[]` to `StageTaskResult[]`; add `findByIssueId()` for API
- `api/issues.ts` — new `GET /:number/executions` route
- `types/index.ts` — add `StageTask`, `StageTaskResult` interfaces
- SSE event registration (`events.ts`) — register `stage_task_update`

**Frontend (packages/cli/web/src/)**:
- New components: `PipelineView`, `StageBar`, `StepList`, `InlineApproval`
- Deleted components: `IssueTimeline`, `TaskList`, `CheckSuitePanel`, `CheckResultsPanel`
- `IssueDetailPage.tsx` — integrate PipelineView, remove old component imports
- `hooks/useSessionTimeline.ts` and `hooks/useIssueTimeline.ts` — replaced by `hooks/usePipelineView.ts`
- `lib/types.ts` — add Pipeline View types, remove old timeline-specific types
- SSE hook updates — handle `stage_task_update` event

**Existing specs affected**: `pipeline-model`, `pipeline-session-events`, `http-api`, `session-timeline-ui`, `event-bus`, `ralph-task-execution`
