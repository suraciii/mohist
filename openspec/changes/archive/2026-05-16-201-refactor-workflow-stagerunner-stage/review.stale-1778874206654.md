## Findings

1. Error - Static stage work does not resolve through the task loader registry
File: `packages/cli/src/workflow/config-driven-stage-runner.ts:759-775`
Evidence: `resolveExecutableTask()` handles `workSource.kind === 'static'` by reading `stageDefinition.tasks` directly and constructing the executable task inline. It never calls `this.taskLoaderRegistry.get('static')` for Plan/Check/Integrate static work.
Why this fails the spec: `specs/workflow-definition/spec.md` requires static non-Build work to resolve through the static task loading path, and `specs/workflow-engine/spec.md` requires requested tasks to resolve from stage definition work sources and the task loader registry.
Suggested fix: In `resolveExecutableTask()`, route static work through the registered static loader and then select the requested task from that loader output, instead of building static tasks inline.

2. Error - Check phases are declared but pre-task phase is not executable
File: `packages/cli/src/workflow/domain/index.ts:359-365`
File: `packages/cli/src/workflow/config-driven-stage-runner.ts:282-295`
Evidence: `StageRun.nextCheck()` always returns `null` until `allRequiredTasksTerminal()` is true, so no check can run before tasks. `runRequestedCheck()` looks up `checkPolicy`, but it does not use `phase` to alter execution order.
Why this fails the spec: `specs/workflow-definition/spec.md` says pre-task, post-task, and approval checks must run in the order and phase declared by stage definitions. The current domain/runner path only supports post-task checks plus separate approval waiting.
Suggested fix: Teach `WorkflowRun.nextWork()` / `StageRun.nextCheck()` to select `pre-task` checks before runnable tasks, keep `post-task` checks after tasks, and reserve approval handling for `approvalPolicy`.

## Review Dimensions

- Correctness: FAIL
  Evidence: static task resolution bypasses the registry and check phase handling ignores declared `pre-task` policy.
- Complexity: WARN
  Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts` is still a very large module with multiple responsibilities (task resolution, task dispatch, plan/check/integrate special cases, git commit side effects).
- Test Coverage: WARN
  Evidence: targeted tests passed: `workflow-run-domain.test.ts`, `workflow-engine-aggregate.test.ts`, `workflow/stage-runner-migration-regression.test.ts`, `workflow/rebase-workflow-regression.test.ts`. But there is no coverage proving `static` work goes through `TaskLoaderRegistry`, and no coverage for `pre-task` check ordering.
- Security: WARN
  Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:1098-1101` commits with `git commit --no-verify`, which bypasses repository hooks.
- Spec Compliance: FAIL

## Spec Compliance Matrix

- `specs/ralph-task-execution/spec.md`: PASS
  Evidence: Build work sources and Ralph task execution are declared in `packages/cli/src/workflow/domain/index.ts:545-559`; Ralph tasks are materialized in `packages/cli/src/workflow/config-driven-stage-runner.ts:700-753`; Ralph execution goes through `packages/cli/src/workflow/task-runtime/ralph-task-handler.ts:11-124`; targeted Build/rebase migration tests passed.
- `specs/workflow-definition/spec.md`: FAIL
  Evidence: declarative policies exist in `packages/cli/src/workflow/domain/index.ts:485-655`, but static work does not resolve through the static loader registry (`packages/cli/src/workflow/config-driven-stage-runner.ts:759-775`) and check phase ordering does not support declared `pre-task` checks (`packages/cli/src/workflow/domain/index.ts:359-365`).
- `specs/workflow-engine/spec.md`: FAIL
  Evidence: legacy/config-driven coexistence and aggregate execution are covered by `packages/cli/src/services/agent-runner-service.ts:1252-1268` and `packages/cli/tests/workflow-engine-aggregate.test.ts:108-320`, but requested task resolution is not uniformly registry-backed for static work (`packages/cli/src/workflow/config-driven-stage-runner.ts:759-775`).
- `specs/workflow-run/spec.md`: FAIL
  Evidence: runtime-added and repair tasks are represented in ordered stage task lists (`packages/cli/src/workflow/domain/index.ts:426-442`, `710-739`, `789-855`), but `pre-task` checks cannot be selected before tasks because `StageRun.nextCheck()` hard-blocks all checks until tasks are terminal (`packages/cli/src/workflow/domain/index.ts:359-365`).

## Verification

- PASS: `npm test -- workflow-run-domain.test.ts workflow-engine-aggregate.test.ts workflow/stage-runner-migration-regression.test.ts workflow/rebase-workflow-regression.test.ts`

<promise>FAIL</promise>
