## Findings

1. Error: Build task materialization can duplicate already-materialized Ralph tasks when reading persisted workflow state.
File: `packages/cli/src/workflow/config-driven-stage-runner.ts:655-661`, `packages/cli/src/workflow/config-driven-stage-runner.ts:670-680`
Evidence: `ctx.workflowRun` is typed as persisted `WorkflowRunWithStageRuns` in `packages/cli/src/workflow/stage-context.ts:68-72`, whose task objects use `taskId`/`taskOrder` fields (`packages/cli/src/db/workflow-run-repo.ts:49-69`), but materialization dedup builds `existingTaskIds` from `task.id`. For persisted runs this is the row primary key, not the workflow task id. The subsequent `materializeTasks()` call upserts by `(stage_run_id, task_id)` (`packages/cli/src/db/workflow-run-repo.ts:818-876`), so the dedup check is wrong and the runner will repeatedly attempt to materialize the same Ralph tasks whenever it resumes at a Build check boundary.
Impact: Violates `Build resumes from materialized task state` and `it SHALL NOT duplicate tasks that were already materialized from tasks.json`.
Suggested fix: In both materialization paths, read persisted task ids with `task.taskId` when present and fall back to `task.id` only for in-memory/domain-shaped test doubles.

2. Error: Plan self-review now commits with hooks enabled, which can fail the stage and breaks preserved Plan semantics.
File: `packages/cli/src/workflow/config-driven-stage-runner.ts:1042-1063`
Evidence: legacy Plan committed with `git commit ... --no-verify` (`packages/cli/src/workflow/plan-stage-runner.ts:606`), but the new shared `commitPlanArtifacts()` removed `--no-verify`. `finalizeSuccessfulTask()` turns any commit failure into a failed stage (`packages/cli/src/workflow/config-driven-stage-runner.ts:197-205`). The targeted regression test already logs `fatal: not a git repository` during this path, but does not assert on hook compatibility.
Impact: Regresses the accepted requirement to retain Plan checkpoint/approval behavior and legacy compatibility while migrating to the config-driven runner. In repositories with pre-commit hooks, Plan can now fail after a successful `self-review` task for reasons unrelated to artifact generation.
Suggested fix: Keep commit behavior compatible with the legacy runner, or explicitly move commit semantics into spec/tests before changing them. Minimal fix: restore `--no-verify` here.

## Spec Compliance

### ralph-task-execution/spec.md

- PASS: Build materializes Ralph tasks before selection.
Evidence: `packages/cli/src/workflow/workflow-engine.ts:241-245`, `packages/cli/src/workflow/config-driven-stage-runner.ts:665-692`, test `packages/cli/tests/workflow-engine-aggregate.test.ts:254-320`.
- PASS: Build task executes through Ralph handler.
Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:401-434`, `720-745`, test `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:536-594`.
- FAIL: Build resumes from materialized task state without duplication.
Evidence: duplicate-dedup bug above at `packages/cli/src/workflow/config-driven-stage-runner.ts:655-680`; persisted tasks expose `taskId`, not workflow id field `id`, per `packages/cli/src/db/workflow-run-repo.ts:49-69`.
- PASS: Aggregate single Build task execution remains supported.
Evidence: runner executes only requested work in `packages/cli/src/workflow/config-driven-stage-runner.ts:83-99`, `127-189`; aggregate coverage in `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:718-827`.
- PASS: Build health repair remains ordinary task work.
Evidence: repair scheduling in `packages/cli/src/workflow/domain/index.ts:832-846`; repair execution via shared runtime in `packages/cli/src/workflow/config-driven-stage-runner.ts:418-433`, `879-905`; domain test `packages/cli/tests/workflow-run-domain.test.ts:199-220`.

### workflow-definition/spec.md

- PASS: Default stages expose declarative policies and stage order remains unchanged.
Evidence: `packages/cli/src/workflow/domain/index.ts:485-656`, stage order from definitions at `694-696`.
- PASS: Stage definition remains non-executing.
Evidence: type-only structure in `packages/cli/src/workflow/domain/index.ts:98-110`, defaults are plain data at `485-656`.
- PASS: Static non-Build work resolves from definition.
Evidence: static loader binding in `packages/cli/src/services/agent-runner-service.ts:1200-1227`; task resolution in `packages/cli/src/workflow/config-driven-stage-runner.ts:720-745`.
- PASS: Checks resolve from check policy.
Evidence: check policy lookup in `packages/cli/src/workflow/config-driven-stage-runner.ts:247-260`; ordering in `packages/cli/src/workflow/domain/index.ts:359-373`.
- FAIL: Plan definition preserves planning contract.
Evidence: config-driven Plan now changes commit semantics and can fail on hook execution in `packages/cli/src/workflow/config-driven-stage-runner.ts:197-205`, `1042-1063`, diverging from legacy `packages/cli/src/workflow/plan-stage-runner.ts:596-607`.
- PASS: Check definition preserves review contract.
Evidence: `packages/cli/src/workflow/domain/index.ts:565-623`, invalidation in `1170-1209`, tests `packages/cli/tests/workflow-run-domain.test.ts:222-315, 514-619`.
- FAIL: Build definition preserves Ralph contract.
Evidence: same duplication defect violates checkpoint/materialization semantics.
- PASS: Integrate definition preserves integration contract.
Evidence: `packages/cli/src/workflow/domain/index.ts:626-654`, integrate service tasks `packages/cli/src/workflow/config-driven-stage-runner.ts:476-566`, tests `packages/cli/tests/workflow-run-domain.test.ts:399-461`.

### workflow-engine/spec.md

- PASS: Runner executes requested task from registries.
Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:127-189`, `401-434`.
- PASS: Runner executes requested check from registry.
Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:240-278`.
- PASS: Runner does not decide stage progression.
Evidence: task/check reporting flows through `workflowApplicationService.completeTask/recordCheckResult` at `109-124`, `299-307`; next work comes from `WorkflowRun.nextWork()`.
- PASS: Legacy and config-driven paths coexist during migration.
Evidence: legacy runner files remain; registration keeps rollback path in `packages/cli/src/services/agent-runner-service.ts:1260-1268`.
- PASS: Failed check schedules configured repair task.
Evidence: `packages/cli/src/workflow/domain/index.ts:832-846` and `packages/cli/tests/workflow-run-domain.test.ts:199-220, 473-493`.
- PASS: Approval remains a user decision point.
Evidence: `packages/cli/src/workflow/domain/index.ts:1037-1048`; tests `packages/cli/tests/workflow-run-domain.test.ts:338-397, 593-600`.
- PASS: Config-driven invalidation applies repair and rebase facts.
Evidence: `packages/cli/src/workflow/domain/index.ts:1170-1209`; tests `packages/cli/tests/workflow-run-domain.test.ts:222-315, 514-590`.
- PASS: Aggregate single-work execution remains supported.
Evidence: `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:718-859`.

### workflow-run/spec.md

- FAIL: Multiple work sources materialize into one StageRun task list consistently.
Evidence: the Build materialization dedup bug means StageRun materialization is not consistent across persisted sources; `packages/cli/src/workflow/config-driven-stage-runner.ts:655-680`.
- PASS: Runtime-added task blocks later checks.
Evidence: `packages/cli/src/workflow/domain/index.ts:352-365`, `988-1008`; rebase tests `packages/cli/tests/workflow-run-domain.test.ts:495-591`.
- PASS: Static and dynamic tasks share task semantics.
Evidence: unified `TaskRun` model `packages/cli/src/workflow/domain/index.ts:240-279`, task materialization and failure behavior `335-350`, `742-787`.
- PASS: Checks share check semantics.
Evidence: unified `CheckState` model `281-301`, `789-855`.
- PASS: Approval is separate from checks in WorkflowRun decisions.
Evidence: `1037-1048`; tests `338-397, 548-600`.
- PASS: Rebase changed snapshot invalidates dependent state.
Evidence: `1170-1209`; test `514-529`.
- PASS: Rebase unchanged snapshot preserves dependent state.
Evidence: `1170-1209`; tests `531-590`.
- PASS: Rebase failure blocks workflow.
Evidence: `769-780`, `988-1003`; test `495-512`.

## Complexity

- Warning: `packages/cli/src/workflow/config-driven-stage-runner.ts` still contains several high-complexity methods over 50 lines, especially `runRequestedTask`, `executeTaskWork`, and the integrate task dispatcher block. This is maintainability risk, not a blocking correctness issue by itself.

## Test Coverage

- PASS: targeted suites passed with `pnpm vitest run packages/cli/tests/workflow-engine-aggregate.test.ts packages/cli/tests/workflow-run-domain.test.ts packages/cli/tests/workflow/stage-runner-migration-regression.test.ts`.
- Warning: tests do not cover persisted Build-resume materialization against real `WorkflowRunWithStageRuns` rows, which is why the `task.id` vs `task.taskId` defect escaped.
- Warning: tests also do not assert legacy-compatible Plan commit behavior with hooks enabled.

## Security

- Warning: no direct secret exposure found in reviewed changes.
- Warning: the new direct `git commit` path in config-driven Plan changes execution behavior and should remain aligned with established repo safety expectations.

## Overall

- Result: FAIL due to the Build materialization duplication bug and the Plan commit behavior regression.

<promise>FAIL</promise>
