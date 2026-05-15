## Findings

1. Error: `ConfigDrivenStageRunner` invalidates Check-stage review artifacts before `WorkflowRun` accepts the task result, violating the required decision boundary and risking state drift.
File: `packages/cli/src/workflow/config-driven-stage-runner.ts:191-207`, `packages/cli/src/workflow/config-driven-stage-runner.ts:254-273`
Evidence: `finalizeSuccessfulTask()` calls `invalidateReviewArtifactForRereview()` immediately after handler success, before `appendTaskResult()` reports the task to `workflowApplicationService.completeTask()`. The spec requires invalidation to be applied by `WorkflowRun` after task results are reported and based on policy facts (`specs/workflow-engine/spec.md`, invalidation requirement; `specs/workflow-run/spec.md`, rebase/reporting requirement). If `completeTask()` later rejects or fails, `review.md` has already been renamed and the checkpoint deleted, so filesystem/checkpoint state can diverge from workflow state.
Suggested fix: Remove the eager invalidation from `ConfigDrivenStageRunner.finalizeSuccessfulTask()`. Let `WorkflowRun.completeTask()` remain the only place that decides invalidation, and perform any file/checkpoint cleanup only after the workflow decision confirms the invalidation-triggering task was accepted.

2. Error: the config-driven Plan path runs `git commit --no-verify`, which explicitly bypasses repository hooks without user approval.
File: `packages/cli/src/workflow/config-driven-stage-runner.ts:1098-1102`
Evidence: `commitPlanArtifacts()` executes `git commit ... --no-verify`. This is a behavior change in the new runner path and skips hooks unconditionally. That is a quality and policy regression, and it is not required by the stage-runner specs.
Suggested fix: remove `--no-verify`, or make hook skipping an explicit, user-controlled opt-in outside this refactor.

## Spec Compliance

### workflow-definition/spec.md

- PASS: Default stage definitions expose declarative policy data and preserve stage order.
Evidence: `packages/cli/src/workflow/domain/index.ts:495-666`

- PASS: Stage definition remains data-only and does not execute work directly.
Evidence: `packages/cli/src/workflow/domain/index.ts:98-111`, `packages/cli/src/workflow/domain/index.ts:495-666`

- PASS: Static non-Build work resolves from definition through loader/handler policy.
Evidence: `packages/cli/src/services/agent-runner-service.ts:1200-1227`, `packages/cli/src/workflow/config-driven-stage-runner.ts:759-817`, `packages/cli/src/workflow/config-driven-stage-runner.ts:858-1068`

- PASS: Checks resolve from check policy via registry.
Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:275-296`, `packages/cli/src/services/agent-runner-service.ts:1234-1250`

- PASS: Plan/Check/Build/Integrate semantics are represented in definitions and exercised by regression tests.
Evidence: `packages/cli/src/workflow/domain/index.ts:495-666`, `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts`

### workflow-engine/spec.md

- PASS: Config-driven runner executes requested task/check from registries.
Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:83-189`, `packages/cli/src/workflow/config-driven-stage-runner.ts:436-469`, `packages/cli/src/workflow/config-driven-stage-runner.ts:275-343`

- PASS: Legacy and config-driven runners coexist, with unified runner default and legacy rollback path preserved.
Evidence: `packages/cli/src/services/agent-runner-service.ts:1252-1268`

- PASS: Aggregate single-work execution remains supported.
Evidence: `packages/cli/src/workflow/workflow-engine.ts:223-299`, `packages/cli/tests/workflow-engine-aggregate.test.ts:196-252`

- FAIL: Config-driven invalidation is not fully owned by `WorkflowRun`; runner performs side effects before `WorkflowRun` decision.
Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:191-207`, `packages/cli/src/workflow/config-driven-stage-runner.ts:254-273`
Deviation: invalidation side effects happen before `workflowApplicationService.completeTask()` has reported the task result and before `WorkflowRun` has accepted the policy decision.

### workflow-run/spec.md

- PASS: WorkflowRun remains the authority for selecting ordered task/check work across static, dynamic, runtime-added, and repair tasks.
Evidence: `packages/cli/src/workflow/domain/index.ts:720-862`, `packages/cli/src/workflow/domain/index.ts:1005-1028`

- PASS: StageRun records task/check metadata consistently, including causedBy for repair/runtime tasks.
Evidence: `packages/cli/src/workflow/domain/index.ts:186-225`, `packages/cli/src/workflow/domain/index.ts:432-469`

- PASS: Approval is modeled separately from checks and is only invalidated by policy-driven facts.
Evidence: `packages/cli/src/workflow/domain/index.ts:728-749`, `packages/cli/src/workflow/domain/index.ts:1056-1068`, `packages/cli/src/workflow/domain/index.ts:1191-1228`

- PASS: Rebase changed/unchanged/failure semantics are covered in domain logic and tests.
Evidence: `packages/cli/src/workflow/domain/index.ts:796-800`, `packages/cli/src/workflow/domain/index.ts:1191-1228`, `packages/cli/tests/workflow/rebase-workflow-regression.test.ts:174-260`

### ralph-task-execution/spec.md

- PASS: Build materializes Ralph tasks before selection.
Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:685-753`, `packages/cli/src/workflow/workflow-engine.ts:193-220`, `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts` cases around lines `670-844`

- PASS: Build task executes through Ralph handler and aggregate single task remains supported.
Evidence: `packages/cli/src/workflow/task-runtime/ralph-task-handler.ts:11-124`, `packages/cli/src/workflow/config-driven-stage-runner.ts:436-469`, `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts`

- PASS: Build health repair remains ordinary task work and is blocked by failed tasks.
Evidence: `packages/cli/src/workflow/domain/index.ts:803-871`, `packages/cli/tests/workflow-run-domain.test.ts:226-247`

## Complexity

- Warning: `ConfigDrivenStageRunner` is very large and contains multiple multi-branch methods; this increases review and maintenance cost.
Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts` (~1100 lines), with methods such as `executeTaskWork()` and dispatch builders carrying several responsibilities.

## Test Coverage

- PASS: substantial regression coverage was added for migration, aggregate execution, rebase semantics, and workflow-run domain behavior.
Evidence: `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts`, `packages/cli/tests/workflow-engine-aggregate.test.ts`, `packages/cli/tests/workflow-run-domain.test.ts`, `packages/cli/tests/workflow/rebase-workflow-regression.test.ts`

- Warning: I did not run the test suite in this review, so passing status is based on code inspection only.

## Security

- FAIL: the new Plan path bypasses git hooks via `--no-verify`.
Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:1098-1102`

## Overall

Implementation is close, but the review invalidation side effect is in the wrong layer and the Plan artifact commit path skips hooks. These are blocking issues.

<promise>FAIL</promise>
