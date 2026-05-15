## Findings

1. FAIL: `ConfigDrivenStageRunner` can leave `stage_executions` in the wrong terminal state for repairable check failures. In `packages/cli/src/workflow/config-driven-stage-runner.ts:344-354`, `stageExecutionStatusAfterCheck()` marks any `fail` or `error` result as `failed` before `WorkflowRun` has a chance to schedule a repair task. That diverges from the spec requirement that failed checks remain ordinary repairable work scheduled through `WorkflowRun` and only fail the stage when unrepaired. Concretely, a repairable `review-passed` or `health:build` failure records a failed stage execution even though `WorkflowRun.recordCheckResult()` will append a fix task and keep the stage runnable (`packages/cli/src/workflow/domain/index.ts:832-845`). Suggested fix: after recording a check result, derive stage execution status from the updated workflow decision/state, not from the raw check result alone. At minimum, if the failed check has a matching repair policy with attempts remaining, keep the stage execution `running` instead of `failed`.

2. FAIL: the new config-driven Plan artifact commit path still forces `git commit --no-verify`, which violates the required safety boundary and bypasses repository hooks. The new path calls `commitPlanArtifacts()` from `packages/cli/src/workflow/config-driven-stage-runner.ts:202-208`, and that helper executes `git commit ... --no-verify` at `packages/cli/src/workflow/config-driven-stage-runner.ts:1034-1039`. This is not a spec-compliance bug by itself, but it is a release-blocking quality issue because the implementation introduces/retains unsafe hook bypass in the migrated default runner path. Suggested fix: remove `--no-verify` and handle hook failures as ordinary task failures.

## Spec Compliance

### workflow-definition/spec.md

- PASS: Default stages expose declarative policies. `DEFAULT_STAGE_DEFINITIONS` now declares `workSources`, `taskExecutionPolicies`, `checkPolicies`, `approvalPolicy`, `repairPolicies`, and `invalidationPolicy` for Plan/Build/Check/Integrate while preserving order `plan -> build -> check -> integrate` in `packages/cli/src/workflow/domain/index.ts:485-656`.
- PASS: Stage definition remains non-executing. `StageDefinition` is still a pure data contract with policy fields only in `packages/cli/src/workflow/domain/index.ts:98-111`.
- PASS: Static non-Build work resolves from definition. Static tasks are loaded from stage definition work sources in `packages/cli/src/services/agent-runner-service.ts:1200-1227` and resolved in `packages/cli/src/workflow/config-driven-stage-runner.ts:696-711`.
- PASS: Checks resolve from check policy. `runRequestedCheck()` validates declared `checkPolicies` and resolves through `CheckRegistry` in `packages/cli/src/workflow/config-driven-stage-runner.ts:240-261`.
- PASS: Plan definition preserves planning contract. Plan tasks/checks/approval are declared in `packages/cli/src/workflow/domain/index.ts:487-534`; plan task execution and approval output are covered by tests in `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:301-360` and `packages/cli/src/workflow/domain/index.ts:1088-1098`.
- PASS: Check definition preserves review contract. Check stage declares `ai-review`, review/merge checks, approval, repairs, and invalidation in `packages/cli/src/workflow/domain/index.ts:565-623`; regression coverage exists in `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:363-417, 985-1252`.
- PASS: Build definition preserves Ralph contract. Build uses `ralph` work source and wildcard Ralph task execution policy in `packages/cli/src/workflow/domain/index.ts:537-562`; materialization/execution tests exist in `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:536-715`.
- PASS: Integrate definition preserves integration contract. Integrate service-call tasks and health check are declared in `packages/cli/src/workflow/domain/index.ts:626-654` and exercised in `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:269-299, 719-787`.

### workflow-engine/spec.md

- PASS: Runner executes requested task from registries. Task resolution and handler dispatch occur in `packages/cli/src/workflow/config-driven-stage-runner.ts:377-409, 696-752`, and results are reported through `workflowApplicationService.completeTask()` in `packages/cli/src/workflow/config-driven-stage-runner.ts:100-124`.
- PASS: Runner executes requested check from registry. `runRequestedCheck()` resolves and executes one check, then reports it via `workflowApplicationService.recordCheckResult()` in `packages/cli/src/workflow/config-driven-stage-runner.ts:240-307`.
- PASS: Runner does not decide stage progression. Progression remains in `WorkflowRun.nextWork()/maybeCompleteStage()/completeStage()` in `packages/cli/src/workflow/domain/index.ts:988-1081`.
- PASS: Legacy and config-driven paths coexist during migration. Default registration prepends the unified runner but keeps all legacy runners available, with `MOHIST_USE_LEGACY_STAGE_RUNNERS=1` rollback, in `packages/cli/src/services/agent-runner-service.ts:1252-1269`.
- FAIL: Config-driven checks preserve read-only and repair policy boundaries. Repair scheduling is delegated to `WorkflowRun.recordCheckResult()` (`packages/cli/src/workflow/domain/index.ts:832-845`), but the config-driven runner prematurely marks the stage execution as `failed` on any failed check in `packages/cli/src/workflow/config-driven-stage-runner.ts:344-354`. This breaks the required semantics for repairable checks.
- PASS: Approval remains a user decision point. `WorkflowRun.nextWork()` returns `await-approval` separately and `WorkflowEngine` stops on that state in `packages/cli/src/workflow/domain/index.ts:1004-1009` and `packages/cli/src/workflow/workflow-engine.ts:261-263`.
- PASS: Config-driven invalidation applies branch and repair facts. Invalidation policies are declared in `packages/cli/src/workflow/domain/index.ts:599-623` and applied in `applyTaskCompletionInvalidation()` at `packages/cli/src/workflow/domain/index.ts:1170-1210`.
- PASS: Aggregate single-work execution remains supported. Config-driven runner requires `requestedWork` and only executes that work in `packages/cli/src/workflow/config-driven-stage-runner.ts:83-98, 127-189, 240-278`; focused tests pass in `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:718-827`.

### workflow-run/spec.md

- PASS: Multiple work sources materialize into one StageRun task list. `WorkflowRun.materializeTasks()` and `StageRun.materializeTasks()` merge dynamic work into the stage task list in `packages/cli/src/workflow/domain/index.ts:335-350, 710-715`.
- PASS: Runtime-added task blocks later checks. `scheduleRebaseTask()` appends an ad-hoc task and `nextWork()` always prefers tasks before checks in `packages/cli/src/workflow/domain/index.ts:718-739, 1004-1009`; tests pass in `packages/cli/tests/workflow/rebase-workflow-regression.test.ts:146-171`.
- PASS: Static and dynamic tasks share task semantics. Static, Ralph, repair, and runtime-added tasks all use the same `TaskRun` model in `packages/cli/src/workflow/domain/index.ts:240-279, 426-450`.
- PASS: Checks share check semantics. `CheckState` is shared and `recordCheckResult()` interprets outputs centrally in `packages/cli/src/workflow/domain/index.ts:281-302, 789-855`.
- PASS: Approval is separate from checks. Approval is modeled independently in `packages/cli/src/workflow/domain/index.ts:461-469, 857-890, 1037-1048`.
- PASS: Rebase changed snapshot invalidates dependent state. Fact-driven invalidation is implemented in `packages/cli/src/workflow/domain/index.ts:1141-1210` and covered by tests in `packages/cli/tests/workflow/rebase-workflow-regression.test.ts:207-260` and `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:1097-1198`.
- PASS: Rebase failure blocks workflow. `completeTask()` fails the stage on failed/skipped tasks in `packages/cli/src/workflow/domain/index.ts:769-780`; coverage exists in `packages/cli/tests/workflow/rebase-workflow-regression.test.ts:174-204`.

### ralph-task-execution/spec.md

- PASS: Build materializes Ralph tasks before selection. Config-driven materialization loads Ralph tasks and sends them through `workflowApplicationService.materializeTasks()` in `packages/cli/src/workflow/config-driven-stage-runner.ts:626-694`; coverage exists in `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:596-715` and `packages/cli/tests/workflow-engine-aggregate.test.ts:254-319`.
- PASS: Build task executes through Ralph handler. Execution resolves the Ralph policy and dispatches through the registered `ralph-task` handler in `packages/cli/src/workflow/config-driven-stage-runner.ts:377-409, 713-720`; smoke coverage exists in `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:536-594`.
- PASS: Build resumes from materialized task state without duplication. `StageRun.materializeTasks()` reuses existing task ids instead of duplicating them in `packages/cli/src/workflow/domain/index.ts:335-349`; missing-task materialization regression coverage exists in `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:654-715`.
- PASS: Aggregate single Build task execution remains supported. Requested task execution is single-work only in `packages/cli/src/workflow/config-driven-stage-runner.ts:83-98, 127-189`; aggregate coverage exists in `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:718-787`.
- PASS: Build health failure schedules configured fix task. `WorkflowRun.recordCheckResult()` schedules `fix-build-health` from repair policy in `packages/cli/src/workflow/domain/index.ts:832-845`; direct domain coverage exists in `packages/cli/tests/workflow-run-domain.test.ts:199-220`.
- PASS: Build health remains blocked by failed tasks. `recordCheckResult()` rejects checks before all tasks are terminal/successful in `packages/cli/src/workflow/domain/index.ts:793-800`.

## Complexity

- WARNING: `packages/cli/src/workflow/config-driven-stage-runner.ts` remains a very large module with multiple functions well over 50 lines and high branching density. The issue goal is functionally met in many places, but maintainability is still risky.

## Test Coverage

- PASS: Focused regression suites pass with `npx vitest run tests/workflow-run-domain.test.ts tests/workflow-engine-aggregate.test.ts tests/workflow/rebase-workflow-regression.test.ts tests/workflow/stage-runner-migration-regression.test.ts`.
- FAIL: the initially attempted command in this review used an invalid Vitest flag/path combination (`--runInBand`, `packages/cli/tests/...`) and failed before execution. This is not a code defect, but it means the implementation should not claim evidence from that failed invocation.
- PASS: `npm run build` passed in `packages/cli`.

## Security

- FAIL: hook bypass remains in the default migrated Plan path via `git commit --no-verify` in `packages/cli/src/workflow/config-driven-stage-runner.ts:1034-1039`.

## Overall

- Overall result: FAIL

<promise>FAIL</promise>
