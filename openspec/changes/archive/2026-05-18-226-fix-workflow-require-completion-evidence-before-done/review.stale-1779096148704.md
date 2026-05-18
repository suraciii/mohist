## Findings

1. Error: `ConfigDrivenStageRunner` can skip recording Build source failure evidence entirely, so missing/invalid/empty `tasks.json` is surfaced as generic `dynamic-source-not-evaluated` instead of the required specific reason.
File: `packages/cli/src/workflow/config-driven-stage-runner.ts:435-455,458-511`
Why: `materializeWork()` only calls `materializeConfiguredStageTasks()` when `stageNeedsTaskMaterialization()` returns `true` (`lines 59-62`). But `stageNeedsTaskMaterialization()` returns `false` when `detectOpenSpecChange()` is missing, when task loading throws, and when the loaded task list is empty (`lines 446-454`). Those are exactly the cases where `materializeConfiguredStageTasks()` would have recorded `missing`, `invalid`, or `empty` Build source state (`lines 471-503`). As a result, aggregate execution can proceed without persisting the authoritative source outcome required by the spec.
Impact: violates `workflow-engine` and `workflow-run` requirements for Build source outcome recording; users get the wrong blocked reason and runners do not materialize/report required work before completion.
Suggested fix: remove the prefilter, or make `stageNeedsTaskMaterialization()` return `true` for Build whenever source evaluation has not been recorded yet so `materializeConfiguredStageTasks()` always records one of `tasks|missing|invalid|empty`.

2. Warning: regression coverage misses the config-driven Build source failure path.
File: `packages/cli/tests/build-workflowrun-tasks.test.ts:201-321`, `packages/cli/tests/workflow-engine-aggregate.test.ts:255-320`
Why: tests cover legacy `BuildStageRunner` missing/invalid source handling and aggregate materialization of successful Build tasks, but there is no test exercising `ConfigDrivenStageRunner.materializeWork()` when `detectOpenSpecChange()` is absent, task loading throws, or the loaded task set is empty.
Suggested fix: add aggregate/config-driven tests asserting `workflowApplicationService.materializeTasks()` is called with `buildWorkSourceState: 'missing' | 'invalid' | 'empty'` before Build health checks are selected.

## Open Questions

- I assumed Build is expected to run through `ConfigDrivenStageRunner` in normal aggregate execution, because `WorkflowEngine` consults `runner.materializeWork()` before dispatch (`packages/cli/src/workflow/workflow-engine.ts:215-243,265-269`). If that path is intentionally disabled in production, the blocking impact would be lower, but the current code still violates the stated runner contract.

## Acceptance Criteria

1. PASS: `nextWork()` / stage completion logic does not treat an empty required task/check set as successful completion.
Evidence: `packages/cli/src/workflow/domain/index.ts:1250-1277,1331-1366` blocks on missing static task/check evidence and unevaluated Build sources instead of relying on empty arrays.

2. PASS: Static stage tasks/checks declared by `StageDefinition` must exist in `StageRun` before the stage can pass.
Evidence: `packages/cli/src/workflow/domain/index.ts:1332-1342` returns `missing-static-task`, `static-task-not-successful`, `missing-static-check`, or `static-check-not-passed`.

3. FAIL: Build with missing, invalid, or zero-task `tasks.json` does not advance as completed without a clear workflow reason.
Deviation: the domain guard has the right reasons (`packages/cli/src/workflow/domain/index.ts:1344-1350`), but the config-driven runner can fail to record them at all because `stageNeedsTaskMaterialization()` suppresses the call that would persist `missing|invalid|empty` (`packages/cli/src/workflow/config-driven-stage-runner.ts:435-455,471-503`).

4. PASS: Dynamic Build tasks generated from `tasks.json` are materialized into the Build `StageRun` and become required for that run.
Evidence: `packages/cli/src/workflow/domain/index.ts:394-409,872-889,1362-1364`; persistence repair also preserves them in `packages/cli/src/workflow/domain/persistence.ts:192-230`.

5. PASS: Runtime-added tasks such as `rebase-branch` are not static `StageDefinition.tasks`, but once appended to a `StageRun` they must complete successfully before the stage can pass.
Evidence: runtime task policies live under `workSources/taskExecutionPolicies` instead of static tasks for Build/Check (`packages/cli/src/workflow/domain/index.ts:702-709,738-748`); appended tasks are added by `appendAdHocTask()` / `scheduleRebaseTask()` (`550-555`, `893-914`) and blocked by `nextWork()` / completion guard (`1254-1264`, `1362-1364`).

6. PASS: Check cannot pass without a current authoritative review task/result and required review/merge checks.
Evidence: `packages/cli/src/workflow/domain/index.ts:1397-1424` requires completed `ai-review`, passed `health:check`, `review-passed`, `merge-ready`, and matching snapshot SHA before completion.

7. PASS: Integrate cannot complete the workflow without required Integrate tasks and delivery evidence.
Evidence: `packages/cli/src/workflow/domain/index.ts:1369-1395` requires completed `integrate:spec-sync`, `integrate:archive-change`, `integrate:merge`, `freezePoint.delivery.landedSha`, and passed `health:integrate`.

8. PASS: `WorkflowRun.completeStage()` or equivalent final completion path enforces the same completion guard as `nextWork()`.
Evidence: `maybeCompleteStage()` calls `evaluateStageCompletionGuard()` before `completeStage()` (`packages/cli/src/workflow/domain/index.ts:1305-1318`), and `approveStage()` repeats the same guard (`1051-1061`).

9. PASS: `WorkflowRunProjection` defensively refuses impossible passed snapshots, including passed workflows that did not reach the final stage.
Evidence: `packages/cli/src/services/workflow-run-projection.ts:97-137` rejects non-Integrate final stage, missing Integrate stage, non-passed Integrate stage, and missing delivery evidence.

10. PASS: A stale failed session on an otherwise successful later workflow run does not prevent `Done`.
Evidence: projection code does not consult session status when deciding `Done` (`packages/cli/src/services/workflow-run-projection.ts:53-81,97-137`); regression test covers the scenario in `packages/cli/tests/workflowrun-e2e.test.ts:153-181`.

11. FAIL: Regression tests cover empty static stage work, missing dynamic Build work, runtime-added pending work, stale session plus successful task evidence, and impossible passed projection snapshots.
Deviation: the suite covers many domain/projection cases, but it misses the config-driven Build source failure path where `materializeWork()` should emit `missing|invalid|empty` before checks (`packages/cli/tests/build-workflowrun-tasks.test.ts:201-321` only covers legacy `BuildStageRunner`; `packages/cli/tests/workflow-engine-aggregate.test.ts:255-320` only covers successful aggregate materialization).

## Verification

- `npm test -- tests/workflow-run-domain.test.ts tests/workflowrun-e2e.test.ts tests/workflow-engine-aggregate.test.ts tests/build-workflowrun-tasks.test.ts tests/integrate-workflowrun.test.ts tests/workflow-run-repo.test.ts` -> PASS
- `npm run build` -> PASS

<promise>FAIL</promise>
