## Findings

1. Error: config-driven stages no longer create or update `stage_executions`, breaking the compatibility projection promised by the design.
File: `packages/cli/src/workflow/config-driven-stage-runner.ts:62-123,249-260`
Evidence: the runner has a `stageExecutionId` field and appends task/check results conditionally, but never initializes it with `ctx.stageExecutionRepo.create(...)`, and `recordCheckResult()` writes only to `workflowApplicationService`. In contrast, the legacy path creates and updates the execution record in `packages/cli/src/workflow/base-stage-runner.ts:159-217`.
Impact: migrated Plan/Build/Check/Integrate stages can execute through the unified runner without producing `stage_executions` task/check/status data, which violates the migration requirement to keep legacy projections and rollback-compatible behavior available.
Suggested fix: initialize a stage execution record at the start of config-driven stage work, append task results to that execution, persist check results via `updateCheckResults`, and update the execution status to `passed` / `failed` / `awaiting-approval` in the same places the legacy runner does.

2. Error: Plan task completion is persisted before plan-artifact commit succeeds, so a failed commit can leave WorkflowRun advanced with an uncommitted Plan state.
File: `packages/cli/src/workflow/config-driven-stage-runner.ts:147-150,167-177,912-943`
Evidence: `runRequestedTask()` calls `appendTaskResult()` before `finalizeSuccessfulTask()`. For Plan `self-review`, `finalizeSuccessfulTask()` then runs `commitPlanArtifacts()` and throws if the commit fails. That means the task has already been reported to `workflowApplicationService.completeTask(...)` before the commit/checkpoint finalization succeeds. The legacy Plan path performs the commit before returning stage success in `packages/cli/src/workflow/plan-stage-runner.ts:510-515`.
Impact: if `git commit` fails, aggregate execution can stop with an exception while WorkflowRun already considers `self-review` completed and may select later checks on resume. This is a real correctness regression in checkpoint/approval flow.
Suggested fix: perform Plan post-task finalization before reporting task completion to WorkflowRun, or roll back the reported task state when commit finalization fails.

## Spec Compliance

### `workflow-definition/spec.md`

- PASS: Default stage definitions expose declarative policy data and preserve `plan -> build -> check -> integrate -> done` ordering. Evidence: `packages/cli/src/workflow/domain/index.ts:485-656`.
- PASS: Stage definitions remain data-only and do not execute work directly. Evidence: `packages/cli/src/workflow/domain/index.ts:98-111,485-656`.
- PASS: Static Plan/Check/Integrate tasks bind through policy and loader/handler resolution. Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:590-646,688-812`.
- PASS: Checks resolve from configured check policy and check registry. Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:210-230`.
- PASS: Plan/Check/Build/Integrate definitions preserve the intended task/check shapes. Evidence: `packages/cli/src/workflow/domain/index.ts:487-655`.

### `workflow-engine/spec.md`

- PASS: Requested tasks are resolved from stage definitions and executed through the handler registry. Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:126-158,271-304`.
- PASS: Requested checks are resolved from the check registry and reported back through WorkflowRun. Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:210-260`.
- PASS: The runner does not decide stage transitions; WorkflowRun remains authoritative. Evidence: `packages/cli/src/workflow/workflow-engine.ts:223-310`, `packages/cli/src/workflow/domain/index.ts:988-1082`.
- PASS: Legacy and config-driven runners coexist, and unified runner is default with rollback path retained. Evidence: `packages/cli/src/services/agent-runner-service.ts:1197-1250`, plus legacy runner files remain present.
- FAIL: compatibility projections are not fully preserved on the config-driven path because `stage_executions` are not created/updated. Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:62-123,249-260`; compare legacy `packages/cli/src/workflow/base-stage-runner.ts:159-217`.
- PASS: aggregate single task/check execution remains supported. Evidence: `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts`, targeted run passed.

### `workflow-run/spec.md`

- PASS: WorkflowRun selects work from one ordered StageRun task list and blocks checks behind pending runtime tasks. Evidence: `packages/cli/src/workflow/domain/index.ts:335-381,988-1009`.
- PASS: repair tasks are scheduled as ordinary tasks with `causedBy` metadata. Evidence: `packages/cli/src/workflow/domain/index.ts:422-433,832-846`.
- PASS: approval remains separate from checks, and runtime-added work only invalidates approval when policy says so. Evidence: `packages/cli/src/workflow/domain/index.ts:730-739,1037-1048,1170-1208`.
- PASS: rebase invalidation is fact-driven and preserves approval when `shaChanged=false`. Evidence: `packages/cli/src/workflow/domain/index.ts:599-623,1141-1208`; `packages/cli/tests/workflow-run-domain.test.ts`, targeted run passed.

### `ralph-task-execution/spec.md`

- PASS: Build Ralph tasks are materialized into StageRun before health-check selection. Evidence: `packages/cli/src/workflow/workflow-engine.ts:193-221,241-245`; `packages/cli/src/workflow/config-driven-stage-runner.ts:520-588`; `packages/cli/tests/workflow-engine-aggregate.test.ts`, targeted run passed.
- PASS: selected Build tasks execute through the Ralph handler rather than a new local Build loop. Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:281-304,711-712`; `packages/cli/src/workflow/task-runtime/ralph-task-handler.ts:11-122`.
- PASS: Build health repair stays ordinary task work. Evidence: `packages/cli/src/workflow/domain/index.ts:542-559,789-855`.

## Validation

- PASS: `npx vitest run tests/workflow-run-domain.test.ts tests/workflow-engine-aggregate.test.ts tests/workflow/rebase-workflow-regression.test.ts tests/workflow/stage-runner-migration-regression.test.ts`
- PASS: `npm run build`
- Note: `npm test -- --runInBand ...` is not valid for this repo's Vitest CLI and fails before running tests.

## Overall

Implementation quality is not acceptable yet because there are correctness/compatibility regressions in the config-driven runner.

<promise>FAIL</promise>
