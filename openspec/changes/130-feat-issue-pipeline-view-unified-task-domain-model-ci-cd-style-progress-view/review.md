# Review: Issue Pipeline View — unified Task domain model + CI/CD style progress view

## Result: FAIL

## Build & Tests

- `npm run build` — PASS (tsc + vite build, 0 errors)
- `npm test` — PASS (75 files, 1254 passed, 6 skipped)

## Dimensions

### Correctness: FAIL

Two errors found that break runtime behavior:

**E1: Build stage `stage_task_update` uses issue number instead of UUID as `issueId`**

`packages/cli/src/openspec/ralph-executor.ts:533-535`

The Build stage's `stage_task_update` emission uses `sseIssueId` which is `String(context.issueNumber ?? '')` (the issue number, e.g. `'42'`), while Plan and Check stages use `issue.id` (the UUID, e.g. `'abc-123-def'`). This causes two failures:

1. **useSSE.tsx:232** — The `stage_task_update` handler tries `data.find((i) => i.id === d.issueId)`. For Build events, `d.issueId` is `'42'` but `i.id` is a UUID — no match found, so the executions query is never invalidated. Build stage data stays stale until manual page refresh.
2. **PipelineView.tsx:694** — The live-task listener filters `evt.issueId !== issue.id`. For Build events, `'42' !== 'uuid'` is true, so the event is discarded. Live elapsed timers don't work for Build tasks.

Fix: Change `ralph-executor.ts:534` from `issueId: sseIssueId,` to `issueId: context.issueId ?? sseIssueId,`

**E2: CheckItem uses `'passed'`/`'failed'` but backend stores `'pass'`/`'fail'`**

`packages/cli/web/src/components/PipelineView.tsx:361,358`

The `CheckItem` component checks `check.status === 'passed'` and `check.status === 'failed'`, but the `stage_executions.check_results` column stores the backend's `CheckResult` objects which use `status: 'pass' | 'fail'`. The API endpoint returns these raw values without transformation. As a result, all check items render with the default `EmptyCircleIcon` (pending) — checkmarks and crosses never appear.

Fix: Change `PipelineView.tsx:361` from `'passed'` to `'pass'` and line 358 from `'failed'` to `'fail'`.

### Complexity: PASS

All functions are under 50 lines. The PipelineView at 794 lines is large but composed of discrete sub-components (StageBar, StepList, TaskItem, CheckItem, InlineApproval, SpecialStatePanel) each under 100 lines. No cyclomatic complexity issues detected.

### Test Coverage: PASS

Existing test suite (75 files, 1254 tests) all pass. The `base-stage-runner.test.ts` covers the shared runner logic. `ralph-executor.test.ts` covers Build task execution. No new tests were added for the PipelineView frontend component or the executions API endpoint — this is acceptable given the existing integration test coverage but would benefit from targeted tests in a follow-up.

### Security: PASS

- API endpoint validates project ID and issue existence before returning data
- No user input is passed unsanitized to SQL queries (parameterized queries via `DatabaseManager`)
- No secrets or credentials exposed

### Spec Compliance: FAIL

See detailed spec compliance table below. Two specs have FAIL/PARTIAL entries due to E1 and E2.

## Warnings

### W1: Duplicated `emitStageTaskUpdate` helper

`plan-stage-runner.ts:352-381`, `check-stage-runner.ts:334-363` — Both files define an identical `emitStageTaskUpdate` function. Should be extracted to a shared utility.

### W2: Silent error swallowing in `appendTaskResult`

`packages/cli/src/workflow/base-stage-runner.ts:23` — `catch {}` silently swallows exceptions. Per the spec, errors should be "caught and logged." A `log.warn` would aid debugging.

### W3: `useIssueExecutions` has no independent refetch mechanism

`packages/cli/web/src/hooks/useQueries.ts:569-575` — The hook depends entirely on the SSE layer for invalidation. If the SSE `issueId` lookup fails (as in E1), no refetch occurs.

## Spec Compliance

### unified-stage-task/spec.md

| Requirement | Status | Evidence |
|---|---|---|
| StageTask interface | PASS | `stage-context.ts:100-112` — all fields match spec |
| StageTaskResult interface | PASS | `stage-context.ts:114-121` — all fields match spec |
| RoundConfig → TaskConfig rename (Plan) | PASS | `plan-stage-runner.ts:23` — `interface TaskConfig`, array named `tasks` at line 52 |
| RoundConfig → TaskConfig rename (Check) | PASS | `check-stage-runner.ts:15` — `interface TaskConfig`, array named `tasks` at line 66 |
| Plan static tasks (5 entries) | PASS | `plan-stage-runner.ts:52-88` — proposal, specs, design, tasks, self-review with source=static |
| Build dynamic tasks | PASS | `ralph-executor.ts:564-585` — `appendStageTaskResult` writes per-task |
| Check static tasks (2 entries) | PASS | `check-stage-runner.ts:66-81` — review, review-self-check |
| StageExecutionRepo.findByIssueId | PASS | `stage-execution-repo.ts:120-128` — ordered by `created_at ASC` |
| StageExecutionRepo.appendTaskResult | PASS | `stage-execution-repo.ts:86-91` — read-append-write pattern |
| task_results stores StageTaskResult[] | PASS | `stage-execution-repo.ts:13` — typed as `StageTaskResult[]` |
| Incremental task result writes | PASS | Plan: `plan-stage-runner.ts:131-139,159-167,184-191,253-260`; Check: same pattern; Build: `ralph-executor.ts:564-585` |

### stage-task-sse-events/spec.md

| Requirement | Status | Evidence |
|---|---|---|
| stage_task_update in EventMap | PASS | `event-bus.ts:55` |
| stage_task_update in ALL_EVENT_TYPES | PASS | `events.ts:44` |
| stage_task_update in AGENT_DETAIL_EVENTS | PASS | `agent-events.ts:43` |
| stage_task_update in useSSE eventTypes | PASS | `useSSE.tsx:300` |
| Plan emits stage_task_update (started/completed) | PASS | `plan-stage-runner.ts:173,252` |
| Build emits stage_task_update | **FAIL** | `ralph-executor.ts:533-535` — wrong `issueId` format (E1) |
| Check emits stage_task_update (started/completed) | PASS | `check-stage-runner.ts:171,250` |
| Fire-and-forget (errors caught) | PASS | All emission points wrapped in try/catch with logging |
| Legacy events continue emitting | PASS | `plan_round_start`, `ralph_task_update` still emitted alongside |

### stage-executions-api/spec.md

| Requirement | Status | Evidence |
|---|---|---|
| GET /api/issues/:number/executions | PASS | `issues.ts:287-310` |
| Returns 404 for missing issue | PASS | `issues.ts:298` — checks `issueService.getByNumber`, returns 404 |
| Returns 200 with empty array for draft | PASS | `stage-execution-repo.ts:120-128` — returns `[]` when no rows |
| Response includes taskResults + checkResults | PASS | `stage-execution-repo.ts:8-17` — `StageExecution` includes both |
| getIssueExecutions in api.ts | PASS | `api.ts:265-266` |
| useIssueExecutions hook | PASS | `useQueries.ts:569-575` |
| SSE invalidates query | **PARTIAL** | Works for Plan/Check (UUID), broken for Build (E1) |

### pipeline-view/spec.md

| Requirement | Status | Evidence |
|---|---|---|
| PipelineView replaces 6 components | PASS | `IssueDetailPage.tsx:11,221` — imports and renders PipelineView |
| Deleted components don't exist | PASS | IssueTimeline, TaskList, CheckSuitePanel, CheckResultsPanel, PlanApprovalPanel all confirmed deleted |
| Stage Bar horizontal (Plan→Build→Check→Done) | PASS | `PipelineView.tsx:9` — `PIPELINE_STAGES` array, StageBar component renders horizontally |
| Stage status icons with color coding | PASS | `PipelineView.tsx:170-183` — StageStatusIcon with completed/running/failed/awaiting-approval/pending |
| Stage Bar click selects stage | PASS | `PipelineView.tsx:759-765` — `handleSelectStage`, `selectedStage` state |
| Default selection is active stage | PASS | `PipelineView.tsx:674-680` — `getDefaultStage` returns current issue stage |
| Step List shows Tasks + Checks | PASS | `PipelineView.tsx:519-583` — StepList with Tasks section and Checks section |
| Task items display status/title/timing | PASS | `PipelineView.tsx:276-354` — TaskItem with icon, title, duration |
| Check items display status/name | **PARTIAL** | CheckItem renders but status icons won't match (E2) |
| Inline Approval in Step List | PASS | `PipelineView.tsx:387-517` — InlineApproval with Approve/Send back/feedback |
| Backlog shows Start button | PASS | `PipelineView.tsx:614-626` — SpecialStatePanel |
| Blocked shows failure banner | PASS | `PipelineView.tsx:628-639` |
| Interrupted shows Resume button | PASS | `PipelineView.tsx:642-661` |
| Completed shows all green | PASS | `PipelineView.tsx:670-680` — isCompleted defaults to Done stage |
| Closed is read-only (dimmed) | PASS | `PipelineView.tsx:672` — `readOnly = isClosed` |
| Real-time updates via SSE | **PARTIAL** | Works for Plan/Check, broken for Build (E1) |

### ralph-task-execution/spec.md

| Requirement | Status | Evidence |
|---|---|---|
| Build emits stage_task_update per task state | PASS (with E1 caveat) | `ralph-executor.ts:533-545` |
| Build writes StageTaskResult per task | PASS | `ralph-executor.ts:564-585,741,833` |

### session-timeline-ui/spec.md

| Requirement | Status | Evidence |
|---|---|---|
| SessionTimeline removed | PASS | File deleted, `useIssueTimeline.ts` deleted, `useIssueTimeline.test.ts` deleted |

### pipeline-session-events/spec.md

| Requirement | Status | Evidence |
|---|---|---|
| plan_round_start in ALL_EVENT_TYPES | PASS | `events.ts:24` |
| plan_session_update in ALL_EVENT_TYPES | PASS | `events.ts:25` |
| stage_task_update in ALL_EVENT_TYPES | PASS | `events.ts:44` |

### http-api/spec.md

| Requirement | Status | Evidence |
|---|---|---|
| API provides executions endpoint | PASS | `issues.ts:287-310` |
| Returns 404 for non-existent issue | PASS | `issues.ts:297-299` |
| Records ordered by createdAt ASC | PASS | `stage-execution-repo.ts:123` — `ORDER BY created_at ASC` |

<promise>FAIL</promise>
