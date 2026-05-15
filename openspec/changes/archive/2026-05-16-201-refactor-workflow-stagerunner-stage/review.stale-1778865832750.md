## Findings

1. Error: runtime-added `rebase-branch` can bypass required approval when it is scheduled from `awaiting-approval` and completes with no invalidation facts.
File: `packages/cli/src/workflow/domain/index.ts:701-703,1009-1017`
Why it fails: `scheduleRebaseTask()` moves the stage from `awaiting-approval` to `running` but intentionally preserves `stageRun.approval`. Later, `maybeCompleteStage()` only requests approval when `stageRun.approval` is null. If the rebase task completes with `shaChanged=false` and no invalidation policy fires, all tasks and checks are already satisfied, so `maybeCompleteStage()` falls through to `completeStage()` and returns `nextWork: { kind: 'complete' }` without another approval gate.
Concrete evidence: reproduced against the built code with a `node -e` script after `npm run build`; both Plan and Check stages returned `nextWork: { kind: 'complete' }` immediately after `rebase-branch` completed unchanged, while `stageRun.status` remained `running` and `stageRun.approval.status` remained `awaiting`.
Spec impact:
- FAIL `specs/workflow-run/spec.md` Requirement `Approval is separate from checks in WorkflowRun decisions`, Scenario `Runtime task does not blindly erase approval evidence`
- FAIL `specs/workflow-run/spec.md` Requirement `Rebase task reports facts before invalidation decisions`, Scenario `Rebase unchanged snapshot preserves dependent state`
- FAIL `specs/workflow-engine/spec.md` Requirement `Config-driven checks preserve read-only and repair policy boundaries`, Scenario `Approval remains a user decision point`
Suggested fix:
- In `packages/cli/src/workflow/domain/index.ts:1009-1017`, gate on approval state explicitly, not only on `approved`. If `requiresApproval` is true and `approval?.status === 'awaiting'`, restore `stageRun.status = 'awaiting-approval'` and return `await-approval` instead of completing the stage.
- Optionally add a focused regression test covering `scheduleRebaseTask()` from `awaiting-approval` followed by `rebase-branch` completion with `shaChanged=false` for both Plan and Check.

## Spec Compliance

### ralph-task-execution/spec.md

- PASS Build materialization before selection: `packages/cli/src/workflow/config-driven-stage-runner.ts:820-863` materializes missing Ralph tasks; `packages/cli/src/workflow/workflow-engine.ts:193-220,241-245,292-295` retries selection after materialization.
- PASS Build executes through Ralph handler: `packages/cli/src/workflow/config-driven-stage-runner.ts:284-289,890-909` resolves Build work from loader and dispatches to `ralph-task` handler.
- PASS Resume/no-duplication behavior: `packages/cli/src/workflow/config-driven-stage-runner.ts:828-859` filters already-materialized task ids before appending.
- PASS Aggregate single Build task support: `packages/cli/src/workflow/config-driven-stage-runner.ts:69-146` executes only `ctx.requestedWork`; covered by `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts`.
- PASS Health repair as ordinary task work: repair scheduling remains in `packages/cli/src/workflow/domain/index.ts:803-817`; runner executes fix task as ordinary task in `packages/cli/src/workflow/config-driven-stage-runner.ts:222-247`.

### workflow-definition/spec.md

- PASS Declarative stage policy data exists for Plan/Build/Check/Integrate in `packages/cli/src/workflow/domain/index.ts:470-627`.
- PASS Stage order remains `plan -> build -> check -> integrate -> done` via `DEFAULT_STAGE_DEFINITIONS` order in `packages/cli/src/workflow/domain/index.ts:470-627`.
- PASS Definitions remain non-executing data contracts: type declarations only in `packages/cli/src/workflow/domain/index.ts:25-110`.
- PASS Static task/check binding exists through registries in `packages/cli/src/services/agent-runner-service.ts:1195-1245` and runner resolution in `packages/cli/src/workflow/config-driven-stage-runner.ts:890-917,162-173`.
- PASS Stage semantics are mostly preserved for Plan, Build, Check, Integrate, with one approval-state regression noted in Findings.

### workflow-engine/spec.md

- PASS Config-driven runner executes requested task/check from registries: `packages/cli/src/workflow/config-driven-stage-runner.ts:69-189,280-325`.
- PASS Legacy and config-driven runners coexist: default runner registration keeps legacy runners in list at `packages/cli/src/services/agent-runner-service.ts:1247-1263`.
- PASS Unified runner is default after migration: same registration block prefers `unifiedRunner` unless `MOHIST_USE_LEGACY_STAGE_RUNNERS=1`.
- PASS Checks remain read-only; repairs are scheduled by domain policy, not by checks: `packages/cli/src/workflow/domain/index.ts:803-817`.
- FAIL Approval remains a user decision point: see Finding 1.
- PASS Invalidation uses task result facts: `packages/cli/src/workflow/domain/index.ts:1119-1179`.
- PASS Aggregate single-work execution remains supported: `packages/cli/src/workflow/config-driven-stage-runner.ts:69-84` plus tests in `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts` and `packages/cli/tests/workflow-engine-aggregate.test.ts`.

### workflow-run/spec.md

- PASS Multiple work sources share one StageRun task list and runtime-added tasks block later checks: task materialization/scheduling in `packages/cli/src/workflow/domain/index.ts:681-710,959-980`.
- PASS Static/dynamic/runtime/repair tasks share task semantics and checks share check semantics: `packages/cli/src/workflow/domain/index.ts:239-468,760-826`.
- FAIL Approval separation for runtime-added tasks: unchanged `rebase-branch` after approval wait can complete the stage without returning to approval; see Finding 1.
- PASS Rebase with `shaChanged=true` invalidates dependent state through policy: `packages/cli/src/workflow/domain/index.ts:572-595,1136-1179`.
- PASS Rebase failure blocks workflow: `packages/cli/src/workflow/domain/index.ts:740-751,963-974`.

## Complexity

- Warning: `packages/cli/src/workflow/config-driven-stage-runner.ts` is still very large and contains several high-branch methods, especially `executeTaskWork()` and stage-specific task execution helpers. This is maintainability debt, but I did not find a spec violation from size alone.

## Test Coverage

- PASS Targeted suites pass: `npm test -- --run tests/workflow-engine-aggregate.test.ts tests/workflow-run-domain.test.ts tests/workflow/rebase-workflow-regression.test.ts tests/workflow/stage-runner-migration-regression.test.ts`
- PASS Build passes: `npm run build`
- FAIL Coverage gap: existing regression tests do not cover the no-op rebase path while a stage is already `awaiting-approval`, which allowed Finding 1 to slip through.

## Security

- PASS No new secret exposure or obvious injection issue found in reviewed changes.

<promise>FAIL</promise>
