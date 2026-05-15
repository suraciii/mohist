## Findings

1. Error: Build work is still materialized after `WorkflowRun.nextWork()` selects `health:build`, which violates the new Build orchestration contract.

File: `packages/cli/src/workflow/domain/index.ts:358-360,959-980`
File: `packages/cli/src/workflow/config-driven-stage-runner.ts:304-312,971-1013`

Evidence:
- `StageRun.nextCheck()` returns the first check as soon as all tasks are terminal; for an empty Build task list, `every()` is vacuously true, so `health:build` is immediately selectable.
- `WorkflowRun.nextWork()` therefore returns `{ kind: 'check', stage: Stage.Build, checkName: 'health:build' }` before any Ralph tasks exist in the stage run.
- The new runner then special-cases that check and materializes Build tasks inside `runRequestedCheck()`.

Why this is wrong:
- The spec requires Ralph tasks to be materialized into the Build `StageRun` before selection, and requires `WorkflowRun` to select Build tasks from that materialized list rather than selecting `health:build` first.
- Current behavior keeps a runner-local escape hatch for Build bootstrap, so Build task selection is not fully owned by `WorkflowRun` yet.

Suggested fix:
- Move initial Build task materialization to a pre-selection point, not a requested-check execution point.
- A minimal fix is to materialize Build work before calling `nextWork()` when the current stage is Build and its `StageRun` has no tasks, likely in the workflow application/resume path used by aggregate execution.
- After that change, remove the `health:build` special case in `ConfigDrivenStageRunner.runRequestedCheck()`.

## Spec Compliance

### ralph-task-execution/spec.md

- FAIL: Build materializes Ralph tasks before selection.
  Evidence: `packages/cli/src/workflow/domain/index.ts:358-360,959-980`; `packages/cli/src/workflow/config-driven-stage-runner.ts:304-312`.
- PASS: Build task executes through Ralph handler.
  Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:401-410,1052-1070`; loader/handler wiring in `packages/cli/src/services/agent-runner-service.ts:1185-1235`.
- PASS: Build resumes from materialized task state without duplicating rows.
  Evidence: `packages/cli/src/workflow/domain/index.ts:334-349`; materialization delegates through `workflowApplicationService.materializeTasks()` in `packages/cli/src/workflow/config-driven-stage-runner.ts:993-1013`.
- PASS: Aggregate single Build task execution remains supported.
  Evidence: requested task path in `packages/cli/src/workflow/config-driven-stage-runner.ts:266-295,401-410`; focused coverage in `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:506-535`.
- PASS: Build health failure schedules ordinary repair task work.
  Evidence: `packages/cli/src/workflow/domain/index.ts:803-817`; repair execution path in `packages/cli/src/workflow/config-driven-stage-runner.ts:352-359`; domain coverage in `packages/cli/tests/workflow-run-domain.test.ts:192-209`.
- FAIL: Build health remains blocked by failed or unmaterialized task state.
  Evidence: same as first failure; `health:build` can be selected before Build tasks are materialized.

### workflow-definition/spec.md

- PASS: Default stages expose declarative policies and stage order remains unchanged.
  Evidence: `packages/cli/src/workflow/domain/index.ts:468-627`.
- PASS: Stage definition remains non-executing data.
  Evidence: `packages/cli/src/workflow/domain/index.ts:97-110,468-627`.
- PASS: Static non-Build work resolves from definition.
  Evidence: `packages/cli/src/services/agent-runner-service.ts:1190-1212`; `packages/cli/src/workflow/config-driven-stage-runner.ts:1052-1079`.
- PASS: Checks resolve from configured policy/registry.
  Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:314-332`; definitions in `packages/cli/src/workflow/domain/index.ts:501-507,531-532,563-565,619-620`.
- PASS: Plan definition preserves planning contract.
  Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:175-237,501-602`.
- PASS: Check definition preserves review contract.
  Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:140-173,826-924`; invalidation policy in `packages/cli/src/workflow/domain/index.ts:572-595`.
- FAIL: Build definition preserves Ralph contract.
  Evidence: Build tasks are not materialized before selection; see first finding.
- PASS: Integrate definition preserves integration contract.
  Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:97-138,638-787`.

### workflow-engine/spec.md

- PASS: Runner executes requested task from registries.
  Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:266-295,401-458,1052-1079`.
- PASS: Runner executes requested check from registry.
  Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:297-333`.
- PASS: Runner does not decide stage progression for ordinary requested work.
  Evidence: requested task/check paths report through `workflowApplicationService` and aggregate progression resumes through `WorkflowEngine` in `packages/cli/src/workflow/workflow-engine.ts:257-269`.
- PASS: Unified runner is the default and legacy runner files remain present.
  Evidence: default registration in `packages/cli/src/services/agent-runner-service.ts:1229-1245`; legacy files still exist under `packages/cli/src/workflow/*stage-runner.ts`.
- PASS: Checks remain read-only and repair scheduling stays in WorkflowRun.
  Evidence: `packages/cli/src/workflow/domain/index.ts:760-817`; runner check path only calls `check.run()` in `packages/cli/src/workflow/config-driven-stage-runner.ts:314-332`.
- PASS: Approval remains a user decision point.
  Evidence: `packages/cli/src/workflow/domain/index.ts:1011-1017`; rebase invalidation behavior in `packages/cli/src/workflow/domain/index.ts:689-710,1126-1177`; regression coverage in `packages/cli/tests/workflow/rebase-workflow-regression.test.ts:109-143,321-339`.
- PASS: Config-driven invalidation applies branch and repair facts.
  Evidence: policy definitions in `packages/cli/src/workflow/domain/index.ts:572-595`; application in `packages/cli/src/workflow/domain/index.ts:753-757,1126-1177`.
- PASS: Aggregate single task/check execution remains supported.
  Evidence: `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:482-535`.

### workflow-run/spec.md

- FAIL: Multiple work sources are not always represented in one ordered task list before selection.
  Evidence: Build stage can select `health:build` before Ralph tasks are materialized; see first finding.
- PASS: Runtime-added task blocks later checks.
  Evidence: `packages/cli/src/workflow/domain/index.ts:351-360,689-710,959-980`; regression coverage in `packages/cli/tests/workflow/rebase-workflow-regression.test.ts:146-171`.
- PASS: Static and dynamic tasks share task semantics.
  Evidence: `packages/cli/src/workflow/domain/index.ts:239-277,334-349,409-424`.
- PASS: Checks share consistent semantics.
  Evidence: `packages/cli/src/workflow/domain/index.ts:280-301,760-817`.
- PASS: Approval is separate from checks and not blindly erased by runtime task scheduling.
  Evidence: `packages/cli/src/workflow/domain/index.ts:689-710,1011-1017,1126-1177`; tests in `packages/cli/tests/workflow/rebase-workflow-regression.test.ts:133-143,321-339`.
- PASS: Rebase facts drive invalidation and failure blocks workflow.
  Evidence: `packages/cli/src/workflow/domain/index.ts:740-757,1126-1177`; tests in `packages/cli/tests/workflow/rebase-workflow-regression.test.ts:174-205,207-339`.

## Complexity

- Warning: `packages/cli/src/workflow/config-driven-stage-runner.ts` remains very large and contains several stage-specific branches inside one class. I did not mark this as a failing issue because the main regression above is the concrete correctness problem.

## Test Coverage

- PASS: Focused suites passed with `npx vitest run tests/workflow/stage-runner-migration-regression.test.ts tests/workflow/rebase-workflow-regression.test.ts`.
- PASS: Package build passed with `npm run build`.
- Note: the initially attempted `npm test -- --runInBand ...` command is not valid for this repo's Vitest version.

## Security

- PASS: No new secret exposure or obvious injection issue found in the touched diff.

<promise>FAIL</promise>
