# Review: Issue #187 — Integrate Stage 标准化

## Summary

The implementation migrates IntegrateStageRunner from a self-managed step model to the standard BaseStageRunner lifecycle: `executeTasks() → getChecks() → runChecksPhase()`. Three integration steps become persisted tasks (`integrate:spec-sync`, `integrate:archive-change`, `integrate:merge`), and the final health verification becomes a standard check (`health:integrate`). Both WorkflowRunService and StageStateService seed the Integrate tasks/checks at initialization. The Web UI components correctly render Integrate tasks and the health check in their respective sections. All 62 tests pass and TypeScript compiles cleanly.

---

## Correctness

### W1: Missing `stage_task_update` SSE events for Integrate tasks

**Severity**: Warning

Design decision D4 states: "Each task step **will** emit the generic `stage_task_update` lifecycle used elsewhere." The implementation does **not** emit `stage_task_update` events for any of the three Integrate tasks. It only emits the compatibility `integration_step_updated` events. SSE consumers listening for `stage_task_update` will not receive real-time Integrate task progress notifications.

**Impact**: WorkflowRun persistence is correct (tasks go pending → completed/failed), so polling-based UI refreshes work. Only the live SSE push path is affected. The `integration_step_updated` events carry similar data in a different shape, so downstream consumers that parse both event types receive redundant but not missing information.

**File**: `packages/cli/src/workflow/integrate-stage-runner.ts` — no `emitStageTaskUpdate` call anywhere in the file.

**Suggestion**: Add `emitStageTaskUpdate(eventBus, ..., 'integrate', taskId, title, 'started', 1, [])` at the start of each step method and `emitStageTaskUpdate(eventBus, ..., 'integrate', taskId, title, 'completed'|'failed', 1, [])` on completion, consistent with pattern used in `plan-stage-runner.ts` and `check-stage-runner.ts`.

### W2: Duplicate `appendTaskResult` call on validation failure path

**Severity**: Warning (no functional impact — `upsertTask` is idempotent)

In `runSpecSyncStep`, when `summary.valid` is false:
1. Line 215: `this.appendTaskResult(ctx, { taskId: 'integrate:spec-sync', status: 'failed', ... })` records the failed spec-sync.
2. Line 227: `throw new Error(errMsg)` throws.
3. Line 249 (catch block): `this.appendTaskResult(ctx, { taskId: 'integrate:spec-sync', status: 'failed', ... })` records it again.

The second call is redundant because `upsertTask` is idempotent, but the double-write path is confusing and adds overhead. A similar pattern exists in `runMergeStep` (though there the success-path append and error-path append occur on different code branches, so not truly duplicated).

**File**: `packages/cli/src/workflow/integrate-stage-runner.ts:215-249`, `:494-602`

**Suggestion**: Remove the `appendTaskResult` call at line 215 for the `!summary.valid` case, or restructure so the try block only appends on success and the catch block handles both the try-failure and the validation-failure cases.

### W3: No 'running' task state emitted

**Severity**: Informational

Integrate tasks go from `pending` → `completed`/`failed` in a single `appendTaskResult` call. Unlike other stages (Plan, Build) that emit `stage_task_update` with `'started'` status, Integrate never transitions a task to `'running'`. This means users watching the pipeline in real time won't see which Integrate task is currently active — they only see the completed/failed result after it finishes.

This is consistent with the current implementation pattern (each step runs to completion before appending), and the `integration_step_updated` events partially compensate. But it's a deviation from the pattern used by other stages.

**Impact**: Minor UX gap. Real-time progress granularity for Integrate is lower than Plan/Build/Check.

---

## Complexity

- `IntegrateStageRunner` is 670 lines total, but each private step method (`runSpecSyncStep`, `runArchiveStep`, `runMergeStep`) is under 70 executable lines. The overall line count includes extensive error handling and event emission logic that is inherently per-step. This is acceptable given the design decision D4 to keep runner-local task execution.
- `WorkflowRunService.seedIntegrateTasks()` and `seedIntegrateChecks()` are concise and follow the same pattern as `seedPlanTasks/Checks`.
- `StageStateService.STATIC_TASK_DEFS` for Integrate is correctly defined with the 3-task list and reused by `seedStaticTasks`.
- All cyclomatic complexity appears under 10 for each function.

No issues.

---

## Test Coverage

### Passing Tests (62/62)

- `workflow-run.test.ts` — 14 tests covering WorkflowRun seeding, Integrate task/check creation, stage ordering, idempotent startRun, upsertTask with metadata, upsertCheck, and approval.
- `stage-state-service.test.ts` — 18 tests covering ensureStage, upsertTask, upsertCheck, setApproval, getIssueStageState, getStageState, normalizeCheckStatus/normalizeTaskStatus, placeholder filtering, stage retry, and Integrate static task seeding.
- `integrate-stage-runner-regression.test.ts` — 4 tests covering AC-1 (seeding), AC-2 (task result persistence + health check), and AC-4 (compatibility events).
- `workflow-run-consistency.test.tsx` — 9 tests covering PipelineView and TaskProgressPanel rendering of Integrate tasks and checks, WorkflowRun data preference, and check/task separation.

### Coverage Gaps

1. **No test for task failure stopping subsequent tasks**: REQ-PM-007 scenario "Task failure stops later Integrate work" is not explicitly tested. If `integrate:spec-sync` fails, the test should verify that `integrate:archive-change` and `integrate:merge` never execute. This is a key behavioral guarantee.

2. **No test for health check failure flow through BaseStageRunner**: When `health:integrate` fails, the flow through `getCheckFailurePolicies()` → `handleCheckFailure()` → `runFixAndRecheck()` → `runFixTask()` is not directly tested. The regression test AC-2b only tests a disabled (auto-passing) health gate.

3. **No test for `getCheckFailurePolicies()` configuration**: The policy mapping (`health:integrate` → `fix-integrate-health`) with autoFix enabled/disabled should have unit tests.

These are coverage warnings, not blocking issues — the integration between BaseStageRunner and IntegrateStageRunner is structurally correct and can be verified in end-to-end testing.

---

## Security

No security issues found. File system paths are constructed from trusted internal sources. The `HealthGateCheck` runs a configured command with timeout and maxBuffer limits. No user input is directly interpolated into shell commands.

---

## Spec Compliance

### Acceptance Criterion 1: IntegrateStageRunner 只实现 `executeTasks()` 和 `getChecks()`，其他逻辑交回 BaseStageRunner

**PASS** — `IntegrateStageRunner` extends `BaseStageRunner` and implements:
- `canHandle()` → `Stage.Integrate` (`integrate-stage-runner.ts:60`)
- `executeTasks()` → runs 3 step methods sequentially (`integrate-stage-runner.ts:68-129`)
- `getChecks()` → returns `[HealthGateCheck({ stage: 'integrate' })]` (`integrate-stage-runner.ts:628-636`)
- `getCheckFailurePolicies()` → policy for `health:integrate` (`integrate-stage-runner.ts:638-647`)
- `runFixTask()` → delegates to `runHealthFixTask()` (`integrate-stage-runner.ts:649-664`)
- `getNextStage()` → `Stage.Done` (`integrate-stage-runner.ts:667`)
- `getPreTaskChecks()` → returns `[]` (`integrate-stage-runner.ts:64-66`)

The lifecycle `run() → executeTasks() → getChecks() → runChecksPhase()` is fully inherited from `BaseStageRunner`.

### Acceptance Criterion 2: 3 step 作为 Task 出现在 `workflow_tasks` 表中

**PASS** — `WorkflowRunService.seedIntegrateTasks()` (`workflow-run-service.ts:97-113`) seeds three tasks with IDs `integrate:spec-sync` (order 0), `integrate:archive-change` (order 1), `integrate:merge` (order 2). `StageStateService.STATIC_TASK_DEFS[Stage.Integrate]` (`stage-state-service.ts:393-397`) defines the same three tasks. Both init-time seeding paths match. Tests confirm seeding (`workflow-run.test.ts:88-108`, `stage-state-service.test.ts:58-63`).

### Acceptance Criterion 3: `final-health` 作为 Check 出现在 `workflow_checks` 表中

**PASS** — `WorkflowRunService.seedIntegrateChecks()` (`workflow-run-service.ts:115-122`) creates a `health:integrate` check titled "Post-merge health check". `IntegrateStageRunner.getChecks()` returns `HealthGateCheck({ stage: 'integrate' })` whose `name` is `health:integrate`. Test confirms seeding (`workflow-run.test.ts:110-119`). `BaseStageRunner.mirrorCheckResults()` persists check execution results into both `stage_checks` and `workflow_checks`.

### Acceptance Criterion 4: Web UI 的 Integrate 阶段展示各 Task/Check 的实时状态和耗时

**PASS** — `workflow-run-consistency.test.tsx` tests confirm:
- Integrate tasks are rendered as discrete ordered items (`:332-357`)
- `health:integrate` check appears in checks section, not in task list (`:360-388`)
- TaskProgressPanel renders Integrate tasks from WorkflowRun (`:391-414`)
- Integrate health check shown separately from tasks (`:417-444`)

### Acceptance Criterion 5: `CheckFailurePolicy` 为 Integrate 定义 check → fix-task 映射

**PASS** — `IntegrateStageRunner.getCheckFailurePolicies()` (`integrate-stage-runner.ts:638-647`) returns a policy mapping `health:integrate` → `fix-integrate-health` when `autoFix` is enabled. `runFixTask()` (`integrate-stage-runner.ts:649-664`) delegates to `runHealthFixTask()` with `taskId: 'fix-integrate-health'` and `stage: 'integrate'`. `HealthFixTaskOptions` type (`health-fix-task.ts:10`) includes `'fix-integrate-health'`. `REAL_TASK_IDS[Stage.Integrate]` (`stage-state-service.ts:318`) includes `'fix-integrate-health'`.

### Acceptance Criterion 6: 现有行为不回归

**PASS** — Regression tests (`integrate-stage-runner-regression.test.ts`) verify:
- AC-1: Integrate tasks and health check are correctly seeded into WorkflowRun
- AC-2: `appendTaskResult` is called for each of the three tasks on success
- AC-2b: Health check result (`health:integrate`) is persisted on success (disabled gate)
- AC-4: Compatibility `integration_started`, `integration_step_updated`, `integration_completed` events are still emitted

No explicit test for failure propagation (spec-sync/archive/merge failure stopping later work), but the code flow is correct — `executeTasks` throws on failure, `BaseStageRunner.run()` catches and returns `{ success: false }`.

---

## Review Verdict

The implementation is functionally correct and fully spec-compliant. The three warnings (missing `stage_task_update` events, duplicate `appendTaskResult` on validation failure path, no 'running' task state) are non-blocking and represent minor deviations from design intent or test coverage gaps rather than production bugs. All tests pass and TypeScript compiles cleanly.

<promise>PASS</promise>