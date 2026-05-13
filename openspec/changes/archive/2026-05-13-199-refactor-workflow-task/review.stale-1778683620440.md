## Review

### Correctness

#### ERROR: 3 test regressions in `integrate-stage-runner.test.ts`

T-006 (commit `8930de04af`) removed `stageExecutionRepo` from `createMockContext` at `packages/cli/tests/workflow/integrate-stage-runner.test.ts:63-80` but did not update tests that depend on it, causing 3 test failures:

1. **"fails spec-sync and emits integration_failed with failingStep integrate:spec-sync when post-sync validation fails"** (line 1275) — Test builds its own `stageExecutionRepo` by spreading `baseCtx.stageExecutionRepo` (now `undefined`), producing an object missing the `create` method. `BaseStageRunner.run()` catches the `TypeError` when calling `create()`, so `stageExecutionId` is never set and `appendTaskResult` is never called. The test expects `appendedTaskResults` to contain an `integrate:spec-sync` entry, but it's empty.

2. **"persists integrate:spec-sync as transient output without durable artifacts"** (line 1334) — Same root cause: `...baseCtx.stageExecutionRepo` spreads `undefined`, `appendTaskResult` is never called, `appendedTaskResults` is empty, assertion at line 1368 fails.

3. **"API GET /api/issues/:number/executions returns stage executions for integrate stage"** (line 1376) — Test uses the base `createMockContext` without overriding `stageExecutionRepo`, so `ctx.stageExecutionRepo` is `undefined`. Accessing `.create` on `undefined` throws `TypeError` at line 1401.

**Fix**: Restore `stageExecutionRepo` to `createMockContext`, or add complete `stageExecutionRepo` mocks to each failing test:

```ts
// In createMockContext, before ...overrides:
stageExecutionRepo: {
  create: vi.fn().mockReturnValue({ id: 'exec-1', issueId: `issue-${issueNumber}`, stage: Stage.Integrate, status: 'running', taskResults: [], checkResults: [], createdAt: new Date().toISOString(), updatedAt: new Date().toISOString() }),
  appendTaskResult: vi.fn(),
  updateStatus: vi.fn(),
  updateCheckResults: vi.fn(),
  findByIssueId: vi.fn().mockReturnValue([
    { id: 'exec-1', issueId: `issue-${issueNumber}`, stage: Stage.Integrate, status: 'passed', taskResults: [], checkResults: [], createdAt: new Date().toISOString(), updatedAt: new Date().toISOString() },
  ]),
} as any,
```

#### NOTE: Pre-existing test failure in `workflow-integration.test.ts`

"emits approval_requested event when pipeline pauses" (line 439) fails identically on master — not introduced by this PR. The test spies on `ctx.eventBus.emit` but the `requestApproval` code path calls `ctx.emit` (the test's own helper). This is a pre-existing issue where the spy target doesn't match the actual code path.

### Complexity

All source functions are under 50 lines. Handler implementations are clean and focused:
- `createAgentSessionTaskHandler` returns a single closure handling success/failure/exception paths (143 lines total including interface, but the handler function itself is well-structured)
- `createServiceCallTaskHandler` is 100 lines with a clean try/catch pattern
- `StaticTaskLoader.load` is 14 lines
- `repair-fix-adapter` dispatch is straightforward conditional branching
- Cyclomatic complexity is low across all new code

### Test Coverage

**New test files** — all 50 tests pass:
- `stage-context-emit-log.test.ts`: 12 tests covering emit/log failure swallowing, payload preservation
- `agent-session-task-handler.test.ts`: 5 tests covering success, failure, retry-after-missing-artifact, event ordering, session cleanup
- `service-call-task-handler.test.ts`: 5 tests covering success, failure, event ordering, timing, boundary ownership
- `static-task-loader.test.ts`: 10 tests covering Plan/Check/Integrate definitions, ordering, missing resolvers, immutability
- `repair-fix-adapter.test.ts`: 14 tests covering all 4 health-fix task IDs, plan-artifact repair, review-fix, merge repair, unknown task rejection, edge cases
- `task-handler-registry.test.ts`: 4 tests covering get/register/boundary

**Regression**: 3 tests broken in `integrate-stage-runner.test.ts` (see Correctness section above).

### Security

No injection risks. No secrets exposed. `serviceFn` in `ServiceCallTaskInput` accepts arbitrary functions but this is internal infrastructure, not user-facing input. The `emit` and `log` helpers properly swallow errors with fire-and-forget semantics.

### Spec Compliance

| Acceptance Criterion | Status | Evidence |
|---|---|---|
| Runners no longer maintain private `emitSafe`/`writeLog` | **PASS** | `base-stage-runner.ts:455` uses `ctx.emit`; `build-stage-runner.ts:61-72` uses `ctx.emit`/`ctx.log`; `integrate-stage-runner.ts:107-113,121-126` uses `ctx.emit`. Legacy `emitStageTaskUpdate` function in `stage-context.ts:226` is retained for `stage_task_update` events which are task-level, not stage-level. |
| Shared `emit`/`log` on `StageContext` | **PASS** | `stage-context.ts:74-75` defines `emit` and `log` fields. `workflow-engine.ts:91-105` constructs safe implementations. Tests at `stage-context-emit-log.test.ts` prove failure swallowing. |
| Unified `TaskHandler` interface | **PASS** | `types.ts:39-42` defines `(task: ExecutableTask, ctx: StageContext) => Promise<StageTaskResult>`. Registry at `types.ts:54-57`. |
| Repair/Fix adapter covers current task IDs | **PASS** | `repair-fix-adapter.ts:6-12` covers `fix-plan-health`, `fix-build-health`, `fix-check-health`, `fix-integrate-health`, `repair-plan-artifacts`, `fix-review-findings`, `repair-merge`. Tests at `repair-fix-adapter.test.ts:86-122` iterate all health-fix IDs. |
| Compatibility exports preserved | **PASS** | `health-fix-task.ts` unchanged — `runHealthFixTask` still exported. `build-stage-runner.ts:301` still calls it directly. Adapter is additive. |
| `StaticTaskLoader` for Plan/Check/Integrate | **PASS** | `static-task-loader.ts:12-31`. Tests at `static-task-loader.test.ts:192-290` express Plan (5 tasks), Check (2 tasks), Integrate (3 tasks) definitions. |
| `AgentSessionTaskHandler` | **PASS** | `agent-session-task-handler.ts:11-141`. Covers success, failure, artifact verification, retry-after-missing-artifact, session cleanup. |
| `ServiceCallTaskHandler` | **PASS** | `service-call-task-handler.ts:8-98`. Covers success, failure, timing normalization. |
| `WorkflowEngine` registration unchanged | **PASS** | `workflow-engine.ts` has no changes to runner registration. No single `StageRunner` introduced. |
| No RalphExecutor split | **PASS** | No `RalphTaskLoader` or `RalphTaskHandler` introduced. `build-stage-runner.ts` still uses `RalphExecutor` directly. |
| No event name changes | **PASS** | All event names preserved: `approval_requested`, `stage_task_update`, `integration_started`, `integration_completed`, `integration_step_updated`, `integration_failed`, `build_stage_started`, etc. |
| Plan/Check/Integrate/repair/fix tests pass | **PARTIAL FAIL** | 50 new focused tests pass. 3 regression tests in `integrate-stage-runner.test.ts` fail due to missing `stageExecutionRepo` mock. |

<promise>FAIL</promise>
