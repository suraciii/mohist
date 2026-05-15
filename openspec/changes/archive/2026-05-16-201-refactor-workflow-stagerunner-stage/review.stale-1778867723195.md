## Findings

1. Error: Config-driven Check re-review no longer invalidates the persisted `review.md` artifact or the `ai-review` checkpoint after `fix-review-findings` succeeds.
File: `packages/cli/src/workflow/domain/index.ts:1170-1209`, `packages/cli/src/workflow/config-driven-stage-runner.ts:1049-1081`
Why it matters: `WorkflowRun.applyTaskCompletionInvalidation()` resets in-memory task/check/approval state, but it does not remove stale on-disk review evidence. The new config-driven `ai-review` path explicitly short-circuits from checkpoint or existing `review.md` (`config-driven-stage-runner.ts:1057-1068`) and therefore can report success without forcing a fresh review after code-changing repair work. That violates the Check stale-review invalidation contract and weakens the fact-driven re-review semantics required by `workflow-definition`, `workflow-engine`, and `workflow-run`.
Concrete evidence:
- Legacy behavior still renames `review.md` and deletes the `ai-review` checkpoint in `CheckStageRunner.invalidateReviewArtifactForRereview()` (`packages/cli/src/workflow/check-stage-runner.ts:127-145`).
- There is an existing regression test for that legacy requirement in `packages/cli/tests/workflow/check-stage-re-review-convergence.test.ts:166-227`.
- The config-driven invalidation test only checks domain state reset, not artifact/checkpoint invalidation (`packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:1161-1205`).
Suggested fix:
- When Check invalidation policy fires for `fix-review-findings` or `rebase-branch` with `shaChanged=true`, also invalidate the persisted review artifact and checkpoint, reusing the legacy helper behavior.
- Smallest safe change: extract `invalidateReviewArtifactForRereview()` into shared logic and invoke it from the config-driven path after successful invalidating tasks.

## Spec Compliance

### workflow-definition/spec.md

- PASS: Stage definitions are declarative and preserve stage order via `DEFAULT_STAGE_DEFINITIONS` in `packages/cli/src/workflow/domain/index.ts:485-656`.
- PASS: Static, Ralph, and runtime work sources plus task/check/approval/repair/invalidation policies are declared in the same definitions (`packages/cli/src/workflow/domain/index.ts:508-652`).
- FAIL: Check definition does not fully preserve stale review invalidation behavior because config-driven Check invalidation resets only in-memory state and not persisted review evidence; see finding 1.
- PASS: Integrate ordered tasks and post-task health check are represented declaratively (`packages/cli/src/workflow/domain/index.ts:626-654`).
- PASS: Build definition declares Ralph work source and health repair policy (`packages/cli/src/workflow/domain/index.ts:537-562`).

### workflow-engine/spec.md

- PASS: Requested task execution resolves through work sources and handler policy in `packages/cli/src/workflow/config-driven-stage-runner.ts:236-269,774-896`.
- PASS: Requested check execution resolves through the check registry in `packages/cli/src/workflow/config-driven-stage-runner.ts:175-212`.
- PASS: WorkflowEngine keeps legacy and config-driven runners side-by-side, with unified runner default and legacy rollback path preserved in `packages/cli/src/services/agent-runner-service.ts:1247-1263`.
- PASS: Aggregate single-work behavior is covered by focused tests in `packages/cli/tests/workflow-engine-aggregate.test.ts:108-320`.
- FAIL: Config-driven invalidation is incomplete for Check because persisted review/checkpoint state is not invalidated along with domain state; see finding 1.

### workflow-run/spec.md

- PASS: `WorkflowRun` remains the authority for task/check/approval/failure selection in `packages/cli/src/workflow/domain/index.ts:988-1009`.
- PASS: Multiple work sources materialize into one task list through `StageRun.materializeTasks()` and runner-driven materialization (`packages/cli/src/workflow/domain/index.ts:335-350,710-716`; `packages/cli/src/workflow/config-driven-stage-runner.ts:704-747`).
- PASS: Repair tasks are appended as ordinary tasks with `causedBy` metadata in `packages/cli/src/workflow/domain/index.ts:426-434,832-845`.
- PASS: Approval remains separate from checks in `packages/cli/src/workflow/domain/index.ts:1037-1049`.
- PASS: Rebase fact-driven invalidation behavior is implemented in `packages/cli/src/workflow/domain/index.ts:1141-1209` and covered by `packages/cli/tests/workflow/rebase-workflow-regression.test.ts:207-420`.
- FAIL: The Check stale-review invalidation path is not fully consistent across runtime state and persisted review evidence; see finding 1.

### ralph-task-execution/spec.md

- PASS: Build materialization happens before Build health selection via `WorkflowEngine.materializeCurrentStageWork()` and `ConfigDrivenStageRunner.materializeConfiguredStageTasks()` (`packages/cli/src/workflow/workflow-engine.ts:193-220,241-245`; `packages/cli/src/workflow/config-driven-stage-runner.ts:704-747`).
- PASS: Build task execution uses Ralph policy resolution with wildcard `ralph-task` execution in `packages/cli/src/workflow/domain/index.ts:549-553` and `packages/cli/src/workflow/config-driven-stage-runner.ts:246-269,808-817,895`.
- PASS: Duplicate Build materialization is prevented by existing task-id checks in `packages/cli/src/workflow/domain/index.ts:339-344` and `packages/cli/src/workflow/config-driven-stage-runner.ts:724-743`.
- PASS: Build health repair remains ordinary task work in `packages/cli/src/workflow/domain/index.ts:832-845` and is exercised in `packages/cli/tests/workflow-run-domain.test.ts:199-220`.

## Complexity

- Warning: `packages/cli/src/workflow/config-driven-stage-runner.ts` remains very large and contains several high-complexity methods and stage-specific branches, especially around lines `236-269`, `774-914`, and `989-1094`. This is maintainability risk, but not the primary correctness failure.

## Test Coverage

- PASS: Focused regression suites for migrated runner behavior exist in `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts`, `packages/cli/tests/workflow-run-domain.test.ts`, `packages/cli/tests/workflow-engine-aggregate.test.ts`, and `packages/cli/tests/workflow/rebase-workflow-regression.test.ts`.
- FAIL: There is no config-driven regression test proving that `fix-review-findings` invalidates on-disk `review.md` and deletes the `ai-review` checkpoint before re-running Check. Existing coverage only verifies domain-state invalidation.
- PASS: Focused test command run successfully: `npx vitest run tests/workflow-run-domain.test.ts tests/workflow-engine-aggregate.test.ts tests/workflow/rebase-workflow-regression.test.ts tests/workflow/stage-runner-migration-regression.test.ts`.

## Security

- PASS: No new secret exposure or obvious injection issue found in the reviewed workflow changes.

## Overall

- FAIL: The config-driven Check path regresses stale-review invalidation by allowing old `review.md` / checkpoint evidence to survive code-changing repair tasks.

<promise>FAIL</promise>
