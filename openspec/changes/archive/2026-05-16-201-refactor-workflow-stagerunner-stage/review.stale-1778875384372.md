**Findings**

1. High: unified runner breaks the non-aggregate fallback path instead of coexisting with legacy runners.
File: `packages/cli/src/workflow/config-driven-stage-runner.ts:83-97`
File: `packages/cli/src/workflow/workflow-engine.ts:318-353`
File: `packages/cli/src/services/agent-runner-service.ts:1266-1289`
The new default registration prepends `ConfigDrivenStageRunner` ahead of legacy runners whenever `MOHIST_USE_LEGACY_STAGE_RUNNERS` is not set. In the non-aggregate `WorkflowEngine.run()` path, `buildContext()` does not set `requestedWork`, then the first matching runner is invoked with a plain stage context. `ConfigDrivenStageRunner.run()` hard-fails with `ConfigDrivenStageRunner requires WorkflowRun requestedWork`, so callers that do not provide `workflowApplicationService` can no longer execute any migrated stage through the existing fallback path. This violates `workflow-engine/spec.md` requirement “Legacy and config-driven runner paths coexist during migration”, especially the scenarios “Unmigrated stage can use legacy runner path” and “Migrated stage uses config-driven path independently”.
Suggested fix: only register the unified runner ahead of legacy runners when aggregate execution services are available, or make `WorkflowEngine`/`ConfigDrivenStageRunner.canHandle()` fall through to legacy runners when `requestedWork` is absent.

2. Medium: repair and rebase tasks still bypass the handler registry in the config-driven runner.
File: `packages/cli/src/workflow/config-driven-stage-runner.ts:408-430`
Even after resolving task policy, `executeTaskWork()` directly calls `executeRebaseBranchTask()` and `createRepairFixAdapter().dispatch(...)` before any registry lookup. That means `rebase-task` and `repair-task` are not actually executed “through the handler selected by the task execution policy”, and custom handler registration cannot affect these task kinds. This is a spec mismatch with `workflow-engine/spec.md` (“Runner executes requested task from registries”) and `workflow-definition/spec.md` (“Stage definitions bind to task and check registries”).
Suggested fix: remove the direct branches in `executeTaskWork()` and always build a dispatchable task, then route execution through `TaskHandlerRegistry` so every task kind follows the same registry-backed path.

**Spec Compliance**

`ralph-task-execution/spec.md`

- PASS: Build materializes Ralph tasks before selection. Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:647-715`, `packages/cli/src/workflow/workflow-engine.ts:214-219,241-245`; regression coverage in `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts` materialization cases.
- PASS: Build executes selected Ralph tasks through the Ralph handler. Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:721-747,815-843`, `packages/cli/src/workflow/task-runtime/ralph-task-handler.ts:11-124`; coverage in `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts` Ralph handler tests.
- PASS: Build resume/materialization avoids duplicate task rows. Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:655-689,717-718`; coverage in `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts` persisted-task rematerialization test.
- PASS: Build health repair remains ordinary task work. Evidence: `packages/cli/src/workflow/domain/index.ts:849-863,1005-1028`; coverage in `packages/cli/tests/workflow-run-domain.test.ts:226-247`.

`workflow-definition/spec.md`

- PASS: Default stage definitions expose declarative policies and preserve stage order. Evidence: `packages/cli/src/workflow/domain/index.ts:495-666,704-706`.
- PASS: Stage definitions remain non-executing data contracts. Evidence: `packages/cli/src/workflow/domain/index.ts:98-110,495-666` contains data only, no runner imports.
- PASS: Static non-Build work resolves from definitions. Evidence: `packages/cli/src/services/agent-runner-service.ts:1200-1227`, `packages/cli/src/workflow/config-driven-stage-runner.ts:721-737`; coverage in `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts` requested static task test.
- PASS: Checks resolve from configured check policy and registry. Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:244-257`, `packages/cli/src/services/agent-runner-service.ts:1234-1250`.
- PASS: Plan, Check, Build, and Integrate semantics are represented in definitions. Evidence: `packages/cli/src/workflow/domain/index.ts:497-665`.

`workflow-engine/spec.md`

- FAIL: Legacy and config-driven runner paths do not safely coexist for non-aggregate execution. Evidence: finding 1.
- FAIL: Requested task execution is not fully registry-backed for repair/rebase kinds. Evidence: finding 2.
- PASS: Requested checks execute through check registry and report results before later work. Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:237-305`; coverage in `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts` requested check tests.
- PASS: Aggregate single task/check execution remains supported. Evidence: `packages/cli/src/workflow/workflow-engine.ts:265-307`; coverage in `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts` AC-2 section and `packages/cli/tests/workflow-engine-aggregate.test.ts`.
- PASS: Config-driven invalidation is applied from WorkflowRun facts, not runner-local stage progression. Evidence: `packages/cli/src/workflow/domain/index.ts:797-800,1189-1229` and `nextWork()`/`maybeCompleteStage()` ownership at `1005-1100`.

`workflow-run/spec.md`

- PASS: WorkflowRun remains the authority for selecting ordered task/check/approval/failure work. Evidence: `packages/cli/src/workflow/domain/index.ts:720-726,752-872,1005-1100`.
- PASS: StageRun records static, dynamic, runtime-added, and repair tasks consistently. Evidence: `packages/cli/src/workflow/domain/index.ts:335-350,436-469`.
- PASS: Approval remains separate from checks and is invalidated only by policy facts. Evidence: `packages/cli/src/workflow/domain/index.ts:740-749,1056-1067,1221-1226`; coverage in `packages/cli/tests/workflow-run-domain.test.ts:629-680` and `packages/cli/tests/workflow/rebase-workflow-regression.test.ts`.
- PASS: Rebase facts drive invalidation, and unchanged snapshots preserve approval/check state. Evidence: `packages/cli/src/workflow/domain/index.ts:1160-1229`; coverage in `packages/cli/tests/workflow-run-domain.test.ts:541-671`.

**Review Dimensions**

- Correctness: FAIL because the default runner ordering now breaks non-aggregate execution when `workflowApplicationService` is absent.
- Complexity: WARN. `packages/cli/src/workflow/config-driven-stage-runner.ts` remains very large and mixes orchestration with stage-specific task shaping; several methods are well over the requested 50-line target.
- Test Coverage: WARN. The added regression coverage is strong for aggregate/config-driven execution, and the targeted suites passed, but there is no test covering the non-aggregate fallback path that finding 1 breaks.
- Security: PASS. I did not find secret exposure or new injection surfaces beyond existing controlled `git` command usage.

**Validation**

- Ran: `npm test -- tests/workflow-run-domain.test.ts tests/workflow-engine-aggregate.test.ts tests/workflow/stage-runner-migration-regression.test.ts`
- Result: 77 tests passed.
- Note: an initial `vitest` invocation failed because `--runInBand` is not a supported option in this repo's Vitest CLI.

<promise>FAIL</promise>
