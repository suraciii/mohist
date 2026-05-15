## Findings

1. High: default config-driven Check stage cannot execute `health:check` because the production `checkRegistry` never registers it.
File: `packages/cli/src/services/agent-runner-service.ts:1271-1287`, `packages/cli/src/workflow/domain/index.ts:657-689`, `packages/cli/src/workflow/config-driven-stage-runner.ts:225-239`
Why it fails:
- The Check stage definition requires `health:check`, `review-passed`, and `merge-ready` as post-task checks.
- The default `checkRegistry` registers `health:plan`, `health:build`, and `health:integrate`, but not `health:check`.
- `ConfigDrivenStageRunner.runRequestedCheck()` resolves checks strictly through the registry and then executes them without a fallback path.
- `agent-runner-service` enables config-driven execution for all runnable stages by default via `CONFIG_DRIVEN_STAGES` and `configDrivenStagesFromEnv()`.
User-visible impact:
- A normal workflow that reaches Check under the unified runner will fail once `WorkflowRun.nextWork()` selects `health:check`.
- This violates the Check-stage migration contract and the default-runner switch requirement.
Suggested fix:
- Add `'health:check'` to the default `checkRegistry` in `packages/cli/src/services/agent-runner-service.ts`, typically using `new HealthGateCheck({ worktreePath, policy: healthGatePolicies.check, stage: 'check' })` or the correct Check-stage health policy source.
- Add a regression test that constructs the production registry path and verifies the unified runner can execute Check's declared checks, especially `health:check`.

## Spec Compliance

### ralph-task-execution/spec.md

- PASS: Build materializes Ralph tasks before selection.
Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:435-478`, `packages/cli/src/workflow/domain/index.ts:802-808`, tests `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:683-858`.
- PASS: Build tasks execute through the Ralph handler and aggregate single-task execution is preserved.
Evidence: `packages/cli/src/workflow/task-runtime/ralph-task-handler.ts:54-170`, `packages/cli/src/workflow/config-driven-stage-runner.ts:379-396`, tests `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:549-675`.
- PASS: Build health repair remains ordinary task work.
Evidence: `packages/cli/src/workflow/domain/index.ts:934-947`, `packages/cli/src/workflow/domain/index.ts:641-646`, tests `packages/cli/tests/workflow-run-domain.test.ts` and `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts` coverage around scheduled fix tasks.

### workflow-definition/spec.md

- PASS: Stage definitions expose declarative policies and preserve stage order.
Evidence: `packages/cli/src/workflow/domain/index.ts:572-748`, `packages/cli/src/workflow/domain/index.ts:786-788`.
- PASS: Definitions remain non-executing data contracts.
Evidence: `packages/cli/src/workflow/domain/index.ts:98-110`, `packages/cli/src/workflow/domain/index.ts:572-748`.
- PASS: Static task/check binding is declarative.
Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:509-567`, `packages/cli/src/workflow/task-runtime/task-loader-registry.ts:4-29`, `packages/cli/src/workflow/checks/check-registry.ts:5-47`.
- FAIL: Check definition does not preserve executable review contract under default registration.
Evidence: Check definition declares `health:check` in `packages/cli/src/workflow/domain/index.ts:657-689`, but production registry omits it in `packages/cli/src/services/agent-runner-service.ts:1271-1287`.

### workflow-engine/spec.md

- PASS: Config-driven runner executes requested tasks from registries.
Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:110-175`, `packages/cli/src/workflow/config-driven-stage-runner.ts:379-396`.
- PASS: Legacy and config-driven paths coexist; legacy runners remain present.
Evidence: `packages/cli/src/services/agent-runner-service.ts:1298-1306`, legacy files still exist under `packages/cli/src/workflow/*-stage-runner.ts`.
- PASS: Aggregate single-work execution remains supported.
Evidence: `packages/cli/src/workflow/workflow-engine.ts:232-321`, tests `packages/cli/tests/workflow-engine-aggregate.test.ts:108-320`.
- FAIL: Config-driven runner cannot execute all declared Check-stage checks under default engine wiring.
Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:225-239` requires registry resolution; `packages/cli/src/services/agent-runner-service.ts:1271-1287` omits `health:check`; default enablement is `packages/cli/src/services/agent-runner-service.ts:64-70,1295-1306`.

### workflow-run/spec.md

- PASS: WorkflowRun remains authority for task/check/approval/failure selection.
Evidence: `packages/cli/src/workflow/domain/index.ts:1161-1185`, `packages/cli/src/workflow/workflow-engine.ts:303-321`.
- PASS: Runtime-added tasks and repair tasks share ordinary task semantics.
Evidence: `packages/cli/src/workflow/domain/index.ts:496-522`, `packages/cli/src/workflow/domain/index.ts:810-832`, `packages/cli/src/workflow/domain/index.ts:934-947`.
- PASS: Approval stays separate from checks and invalidation is policy-driven.
Evidence: `packages/cli/src/workflow/domain/index.ts:1212-1225`, `packages/cli/src/workflow/domain/index.ts:1402-1442`.
- PASS: Rebase facts drive invalidation rather than task presence alone.
Evidence: `packages/cli/src/workflow/domain/index.ts:703-714`, `packages/cli/src/workflow/domain/index.ts:1373-1442`, tests `packages/cli/tests/workflow/rebase-workflow-regression.test.ts`.

## Complexity

- Warning: `ConfigDrivenStageRunner` and `WorkflowRun` both contain several long methods and broad responsibilities.
Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts`, `packages/cli/src/workflow/domain/index.ts`.
Suggested improvement:
- Extract registry wiring validation and stage-specific side effects into smaller collaborators to keep the runner path easier to audit.

## Test Coverage

- PASS: Focused regression suites for migration and aggregate workflow pass.
Evidence: `npm test -- --run tests/workflow/stage-runner-migration-regression.test.ts`, `npm test -- --run tests/workflow-engine-aggregate.test.ts`, `npm test -- --run tests/workflow/workflow-engine.test.ts`.
- Warning: current regression coverage misses the production registry gap for `health:check`.
Evidence: `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:2060-2068` only asserts default health registry coverage for plan/build/integrate.

## Security

- PASS: No new secret exposure or obvious command-injection issue found in the reviewed path.

Overall: FAIL due to the missing `health:check` registration on the default config-driven path.

<promise>FAIL</promise>
