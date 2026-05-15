## Findings

1. Error: Real config-driven Build execution is wired to fail because `createRalphTaskLoader()` and `createRalphTaskHandler()` disagree on `task.input` shape.
File: `packages/cli/src/workflow/task-runtime/ralph-task-loader.ts:17-32`
Evidence: the loader emits each Build task with `input` set to a `RalphTaskInput` object.
File: `packages/cli/src/workflow/task-runtime/ralph-task-handler.ts:62-64,111-113,138-149`
Evidence: the handler casts `task.input` to `string | undefined`, passes that value as `onlyTaskId`, then looks up `loopResult.taskResults.find(r => r.taskId === requestedTaskId)`.
File: `packages/cli/src/services/agent-runner-service.ts:1235-1264`
Evidence: production wiring registers `createRalphTaskLoader()` together with `createRalphTaskHandler()`, so this mismatch is on the real default path.
Impact: under the unified Build runner, `onlyTaskId` becomes an object instead of a task id string, so Ralph single-task execution and result matching can fail. This violates the Build migration requirements and can break aggregate single-task execution.
Suggested fix: update `createRalphTaskHandler()` to extract the requested id from `RalphTaskInput` (for example `const requestedTaskId = typeof task.input === 'string' ? task.input : (task.input as RalphTaskInput | undefined)?.taskId ?? task.taskId;`) and add a regression test that uses the real `createRalphTaskLoader()` output with the real handler.

## Correctness

- FAIL due to the Ralph loader/handler contract mismatch above.

## Complexity

- Warning: `packages/cli/src/workflow/config-driven-stage-runner.ts` is 658 lines and contains several high-branch helper methods. I did not find a concrete bug from size alone, but this exceeds the requested complexity target and raises maintenance risk.

## Test Coverage

- Partial PASS: targeted tests pass, but they miss the failing real-loader/real-handler Build path.
- Evidence: `tests/workflow/stage-runner-migration-regression.test.ts` covers Build, but its Ralph tests stub `input: 'T-001'` instead of using `createRalphTaskLoader()` output (`lines 631-636, 684-689`), so the production mismatch is not exercised.
- Commands run:
- `npm run build` in `packages/cli` -> PASS
- `npx vitest run tests/workflow-run-domain.test.ts tests/workflow-engine-aggregate.test.ts tests/workflow/stage-runner-migration-regression.test.ts` -> PASS

## Security

- PASS. No new secret exposure or obvious injection issue found in the reviewed path.

## Spec Compliance

### workflow-definition/spec.md

- PASS: Default stage definitions expose declarative policy data and preserve stage order.
Evidence: `packages/cli/src/workflow/domain/index.ts:572-746` defines Plan/Build/Check/Integrate with `workSources`, `taskExecutionPolicies`, `checkPolicies`, `approvalPolicy`, `repairPolicies`, and `invalidationPolicy`.
- PASS: Stage definition remains data-only.
Evidence: `packages/cli/src/workflow/domain/index.ts:98-110` defines the contract only; no runner imports or execution logic are embedded in the definitions themselves.
- PASS: Static non-Build work resolves from definition through registry-backed loader/handler flow.
Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:509-539,544-553,589-607`.
- PASS: Checks resolve from check policy and check registry.
Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:225-239`; `packages/cli/src/workflow/checks/check-registry.ts:5-47`.
- PASS: Plan/Check/Integrate semantics are represented in definitions and exercised by runner logic.
Evidence: Plan definitions at `domain/index.ts:574-621`, Check at `651-714`, Integrate at `716-745`.
- FAIL: Build definition does not fully preserve Ralph contract in executable behavior because the real handler path is broken.
Evidence: Build policy is declared at `domain/index.ts:624-649`, but executable path is inconsistent across `task-runtime/ralph-task-loader.ts:17-32` and `task-runtime/ralph-task-handler.ts:62-149`.

### workflow-engine/spec.md

- PASS: Config-driven runner executes requested tasks/checks via registries and reports back through workflow application service.
Evidence: task path `config-driven-stage-runner.ts:110-175,379-395`; check path `218-286`.
- PASS: Legacy and config-driven paths coexist.
Evidence: `packages/cli/src/services/agent-runner-service.ts:1289-1306` registers unified plus legacy runners, with env-controlled rollback.
- PASS: Unified runner is default after migration while legacy files remain present.
Evidence: `agent-runner-service.ts:1304-1306`; legacy runner files still exist under `packages/cli/src/workflow/`.
- PASS: Checks stay read-only and repairs are scheduled through WorkflowRun policy.
Evidence: `packages/cli/src/workflow/domain/index.ts:929-943`; `config-driven-stage-runner.ts:236-239,277-285` records check results only.
- PASS: Approval remains separate from repairable checks.
Evidence: `domain/index.ts:954-989,1207-1220`.
- PASS: Invalidation is policy-driven from task results.
Evidence: `domain/index.ts:1375-1415`.
- FAIL: Aggregate single task Build execution is not reliable on the default unified path because the Ralph handler sends the wrong `onlyTaskId` type.
Evidence: `task-runtime/ralph-task-handler.ts:63,111-113`.

### workflow-run/spec.md

- PASS: WorkflowRun remains the authority for next work selection across tasks/checks/approval/failure.
Evidence: `packages/cli/src/workflow/domain/index.ts:1156-1179`.
- PASS: Runtime-added tasks are represented in the same task list and block later checks.
Evidence: `domain/index.ts:808-829,1173-1178`.
- PASS: Repair tasks are appended as ordinary tasks with `causedBy` metadata.
Evidence: `domain/index.ts:929-943`.
- PASS: Approval is modeled separately and only invalidated by policy.
Evidence: `domain/index.ts:1207-1220,1375-1415`.
- PASS: Rebase invalidation uses reported facts instead of mere task presence.
Evidence: `domain/index.ts:1346-1415`.

### ralph-task-execution/spec.md

- PASS: Build work is materialized from Ralph source before selection.
Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:435-503` and `workflow-engine.ts:202-220,303-307`.
- FAIL: Selected Build task does not reliably execute through the Ralph handler on the real default path because the handler cannot consume the loader's `input` contract.
Evidence: `packages/cli/src/workflow/task-runtime/ralph-task-loader.ts:17-32` vs `packages/cli/src/workflow/task-runtime/ralph-task-handler.ts:62-149`.
- FAIL: Aggregate single Build task execution is therefore not proven end-to-end on the production wiring.
Evidence: same mismatch; test gap in `tests/workflow/stage-runner-migration-regression.test.ts:631-636`.
- PASS: Build health repair remains ordinary task work in the domain model.
Evidence: `packages/cli/src/workflow/domain/index.ts:629-646,929-943`.

## Overall

- FAIL: one error-level correctness issue blocks acceptance.

<promise>FAIL</promise>
