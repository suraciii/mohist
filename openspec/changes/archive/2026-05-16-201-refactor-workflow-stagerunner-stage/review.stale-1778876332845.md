## Findings

1. Error: default runner registration cannot mix config-driven and legacy stages independently, which violates the migration coexistence requirement.
File: `packages/cli/src/services/agent-runner-service.ts:1252-1268`
Evidence: the default runner list is either `[unifiedRunner, ...legacyRunners]` or `legacyRunners` based only on `MOHIST_USE_LEGACY_STAGE_RUNNERS`. `ConfigDrivenStageRunner.canHandle()` returns `true` for any stage with a definition (`packages/cli/src/workflow/config-driven-stage-runner.ts:74-76`), and `DEFAULT_STAGE_DEFINITIONS` contains Plan/Build/Check/Integrate (`packages/cli/src/workflow/domain/index.ts:495-666`). That means once the unified runner is enabled, it always wins for all four stages, and there is no stage-by-stage fallback path. This does not satisfy `workflow-engine/spec.md` scenario “Migrated stage uses config-driven path independently”.
Suggested fix: introduce per-stage registration or a stage-level feature flag so `WorkflowEngine` can route some stages to `ConfigDrivenStageRunner` and others to legacy runners at the same time.

2. Error: the generic runner still hardcodes stage-specific task construction and service behavior instead of resolving stage work through registries, so the stage differences are not actually moved out of the runner.
File: `packages/cli/src/workflow/config-driven-stage-runner.ts:417-547`, `:586-629`, `:765-1014`
Evidence: `createPlanTaskConfigs()`, `buildIntegrateServiceFn()`, `executeConvergeReviewSnapshotTask()`, `buildRuntimeTaskDefinition()`, `createPlanAgentSessionDispatchTask()`, and `createCheckAiReviewDispatchTask()` embed Plan/Check/Integrate-specific task behavior directly inside `ConfigDrivenStageRunner`. This conflicts with `workflow-definition/spec.md` lines 19-33 and `workflow-engine/spec.md` lines 7-24, which require stage work to be bound through task/check registries without stage-specific private branching in the runner.
Suggested fix: move these stage-specific bindings behind loader/handler/check registrations keyed by declarative policy ids, leaving the runner to only resolve and dispatch requested work.

## Correctness

- FAIL: registration/rollback semantics are incomplete for mixed migration states.
- PASS: focused regression tests passed for domain flow, aggregate progression, and migration regression coverage.
Tests run: `npm test -- tests/workflow-run-domain.test.ts tests/workflow/stage-runner-migration-regression.test.ts tests/workflow-engine-aggregate.test.ts`

## Complexity

- WARN: `ConfigDrivenStageRunner` remains a large, multi-responsibility module with substantial stage-specific branching. Examples include `buildIntegrateServiceFn()` (`packages/cli/src/workflow/config-driven-stage-runner.ts:457-547`) and `createPlanAgentSessionDispatchTask()` (`:931-979`). This increases change amplification and undercuts the intended deep-module design.

## Test Coverage

- PASS: targeted suites covering workflow-run domain behavior, aggregate engine behavior, and stage-runner migration regressions all passed.
- WARN: current passing tests do not prove stage-by-stage mixed registration, because the shipped registration path only supports all-config-driven or all-legacy selection.

## Security

- PASS: no obvious secret exposure or injection issue found in the reviewed change set.

## Spec Compliance

### ralph-task-execution/spec.md

- PASS: Build materializes Ralph tasks before selection.
Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:631-699`; aggregate materialization hook in `packages/cli/src/workflow/workflow-engine.ts:214-218,241-245`; tests in `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:670-757,791-820`.
- PASS: Build tasks execute through the Ralph handler.
Evidence: Build task execution policy `'*' -> 'ralph-task'` in `packages/cli/src/workflow/domain/index.ts:559-563`; handler registration in `packages/cli/src/services/agent-runner-service.ts:1195-1199`; Ralph dispatch in `packages/cli/src/workflow/task-runtime/ralph-task-handler.ts:11-124`.
- PASS: Build resume/materialization avoids duplicate task rows.
Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:651-670,701-703`; tests in `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:728-820`.
- PASS: Build health repair is scheduled as ordinary task work.
Evidence: repair policy in `packages/cli/src/workflow/domain/index.ts:564-569,849-863`; tests in `packages/cli/tests/workflow-run-domain.test.ts:503-519`.

### workflow-definition/spec.md

- PASS: default stage definitions expose declarative policy data and preserve stage order.
Evidence: `packages/cli/src/workflow/domain/index.ts:495-666`, `:704-706`.
- PASS: stage definitions are data-only structures.
Evidence: `StageDefinition` shape in `packages/cli/src/workflow/domain/index.ts:98-110`.
- FAIL: stage work is not fully bound through registries without stage-specific private branching.
Deviation: `packages/cli/src/workflow/config-driven-stage-runner.ts:417-547,586-629,765-1014` contains embedded stage/task-specific execution logic.
- PASS: Plan/Check/Build/Integrate semantics are largely represented in definitions and policy.
Evidence: `packages/cli/src/workflow/domain/index.ts:497-665` plus focused tests in `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts`.

### workflow-engine/spec.md

- PASS: requested tasks/checks execute through registries and report back into WorkflowRun.
Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:129-194,237-305,398-415`; `packages/cli/src/workflow/checks/check-registry.ts:28-47`; `packages/cli/src/workflow/task-runtime/task-loader-registry.ts:16-29`.
- FAIL: legacy and config-driven paths do not coexist independently per stage in the shipped default registration.
Deviation: `packages/cli/src/services/agent-runner-service.ts:1252-1268` only supports all-config-driven or all-legacy selection.
- PASS: checks remain read-only and repairs are scheduled via WorkflowRun.
Evidence: `packages/cli/src/workflow/domain/index.ts:849-863`; `packages/cli/src/workflow/config-driven-stage-runner.ts:255-305`.
- PASS: invalidation applies via policy and reported facts.
Evidence: `packages/cli/src/workflow/domain/index.ts:609-633,1189-1229`; tests in `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:1156-1294`.
- PASS: aggregate single-work execution remains supported.
Evidence: `packages/cli/src/workflow/workflow-engine.ts:223-320`; tests in `packages/cli/tests/workflow-engine-aggregate.test.ts:196-252` and migration regression suite.

### workflow-run/spec.md

- PASS: WorkflowRun remains the authority for next-work selection across configured sources.
Evidence: `packages/cli/src/workflow/domain/index.ts:720-749,803-872,1005-1100`.
- PASS: StageRun records static, dynamic, runtime-added, and repair work with shared task semantics.
Evidence: `packages/cli/src/workflow/domain/index.ts:335-349,432-469`; persistence mapping in `packages/cli/src/db/workflow-run-repo.ts:250-285`.
- PASS: approval is separate from checks.
Evidence: `packages/cli/src/workflow/domain/index.ts:1056-1067,1103-1133`.
- PASS: rebase invalidation is fact-driven and failure blocks later work.
Evidence: `packages/cli/src/workflow/domain/index.ts:728-749,783-794,1160-1229`; tests in `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:1156-1294`.

## Overall

- FAIL: the implementation is close functionally and focused tests pass, but it does not fully meet the migration/architecture requirements because mixed per-stage legacy/config-driven coexistence is missing and substantial stage-specific execution logic still lives inside the generic runner.

<promise>FAIL</promise>
