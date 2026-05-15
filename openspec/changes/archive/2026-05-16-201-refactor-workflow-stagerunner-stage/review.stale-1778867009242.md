## Findings

1. Error: `checkPolicies` and `approvalPolicy` are declared but not used to drive execution, so the config-driven path does not actually resolve checks and approvals from stage policy as required.
File refs: `packages/cli/src/workflow/domain/index.ts:358-360`, `packages/cli/src/workflow/domain/index.ts:1011-1018`, `packages/cli/src/workflow/config-driven-stage-runner.ts:162-189`.
Evidence: `StageRun.nextCheck()` selects the next check from the raw `checks` array only; `WorkflowRun.maybeCompleteStage()` still keys approval off legacy `requiresApproval`; `ConfigDrivenStageRunner.runRequestedCheck()` executes whichever check name `requestedWork` already contains and never consults `checkPolicies` phase metadata or `approvalPolicy`. This fails `workflow-definition/spec.md` “Checks resolve from check policy” and `workflow-engine/spec.md` “Approval remains a user decision point” as implemented through declarative policy.
Suggested fix: Move check selection/approval gating onto `checkPolicies` and `approvalPolicy` in `WorkflowRun`, and have the config-driven path derive approval waits from policy instead of legacy `requiresApproval` / raw check ordering.

2. Error: `ConfigDrivenStageRunner` still contains stage-specific task branching for Plan, Check, Integrate, repair tasks, and Build, so execution is not registry-driven in the way the spec requires.
File refs: `packages/cli/src/workflow/config-driven-stage-runner.ts:218-325`, `packages/cli/src/workflow/config-driven-stage-runner.ts:368-477`, `packages/cli/src/workflow/config-driven-stage-runner.ts:479-628`, `packages/cli/src/workflow/config-driven-stage-runner.ts:667-817`.
Evidence: the runner hardcodes `rebase-branch`, multiple `fix-*` tasks, `check:converge-review-snapshot`, Plan task behavior, Check `ai-review`, and Integrate service calls in private branches instead of resolving executable work solely through task loader / task handler / check registries. This violates `workflow-engine/spec.md` “Runner executes requested task from registries” and `workflow-definition/spec.md` “Stage definitions bind to task and check registries without stage-specific private branching”.
Suggested fix: register these task kinds behind handler/loader abstractions and reduce `executeTaskWork()` to registry lookup plus shared result reporting.

3. Error: Build stage execution policy is not declarative; Build tasks are routed by a special `if (ctx.issue.stage === Stage.Build)` branch instead of stage policy, and `DEFAULT_STAGE_DEFINITIONS` leaves Build `taskExecutionPolicies` empty.
File refs: `packages/cli/src/workflow/domain/index.ts:526-537`, `packages/cli/src/workflow/config-driven-stage-runner.ts:280-290`.
Evidence: Build dynamic tasks only execute because the runner special-cases `Stage.Build` and directly asks for the `'ralph-task'` handler. The stage definition does not expose the task execution policy needed to execute Build work, which is required by `workflow-definition/spec.md` “Default stages expose declarative policies” and `ralph-task-execution/spec.md` “Build task executes through Ralph handler”.
Suggested fix: introduce a declarative Build task execution policy keyed by work source/task kind and resolve Ralph execution through that policy instead of a Build-only branch.

## Spec Compliance

### ralph-task-execution/spec.md

- PASS: Build materializes Ralph tasks before selection.
Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:820-863`, `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:596-712`.
- FAIL: Build task executes through config-driven work source and Ralph handler without Build-only branching.
Deviation: execution still depends on the Build-only branch at `packages/cli/src/workflow/config-driven-stage-runner.ts:284-289` and empty Build `taskExecutionPolicies` at `packages/cli/src/workflow/domain/index.ts:530-537`.
- PASS: Build resume/materialization avoids duplicate task rows.
Evidence: `packages/cli/src/workflow/domain/index.ts:334-349`, `packages/cli/tests/build-workflowrun-tasks.test.ts:89-133`.
- PASS: Aggregate single Build task execution remains supported.
Evidence: `packages/cli/src/workflow/task-runtime/ralph-task-handler.ts:65-77`, `packages/cli/tests/build-workflowrun-tasks.test.ts:192-275`.
- PASS: Build health repair remains ordinary task work.
Evidence: `packages/cli/src/workflow/domain/index.ts:803-816`, `packages/cli/tests/workflow-run-domain.test.ts:199-220`.

### workflow-definition/spec.md

- FAIL: Default stages do not fully expose declarative execution policy that is actually consumed.
Deviation: Build has no declared task execution policy (`packages/cli/src/workflow/domain/index.ts:530-537`), and approval/check policy fields are not used by runtime selection (`packages/cli/src/workflow/domain/index.ts:358-360`, `1011-1018`).
- PASS: Stage order remains `plan -> build -> check -> integrate -> done`.
Evidence: `packages/cli/src/workflow/domain/index.ts:468-627`.
- PASS: StageDefinition remains data-only and non-executing.
Evidence: `packages/cli/src/workflow/domain/index.ts:97-109`, `468-627`.
- FAIL: Static/check work is not resolved purely through declarative registry binding.
Deviation: task execution still relies on runner-local stage branches in `packages/cli/src/workflow/config-driven-stage-runner.ts:218-325`.
- PASS: Plan/Check/Build/Integrate user-visible task/check sets are present.
Evidence: `packages/cli/src/workflow/domain/index.ts:470-625`.

### workflow-engine/spec.md

- FAIL: Config-driven runner does not execute declared stage work solely from registries.
Deviation: `packages/cli/src/workflow/config-driven-stage-runner.ts:218-325` hardcodes task ids and stage names.
- PASS: Runner reports task/check results back to WorkflowRun before later work selection.
Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:86-110`, `191-203`; aggregate progression in `packages/cli/src/workflow/workflow-engine.ts:292-305`.
- PASS: Legacy and config-driven paths coexist.
Evidence: `packages/cli/src/services/agent-runner-service.ts:1255-1263`, tests at `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:1208-1269`.
- PASS: Aggregate single task/check execution remains supported.
Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:69-84`, `113-189`; tests at `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:718-840`.
- PASS: Invalidation and repair decisions remain in WorkflowRun.
Evidence: `packages/cli/src/workflow/domain/index.ts:753-757`, `803-825`, `1138-1177`.
- FAIL: Approval behavior is not policy-driven.
Deviation: runtime still uses legacy `requiresApproval` instead of `approvalPolicy` at `packages/cli/src/workflow/domain/index.ts:1011-1018`.

### workflow-run/spec.md

- PASS: Multiple work sources materialize into one StageRun task list and runtime tasks block later checks.
Evidence: `packages/cli/src/workflow/domain/index.ts:334-349`, `351-360`, `689-710`, `959-980`; tests at `packages/cli/tests/workflow/rebase-workflow-regression.test.ts:146-205`.
- PASS: Static, dynamic, repair, and runtime-added tasks share task semantics.
Evidence: `packages/cli/src/workflow/domain/index.ts:239-277`, `409-424`, `681-757`.
- PASS: Approval remains separate from checks in WorkflowRun state.
Evidence: `packages/cli/src/workflow/domain/index.ts:444-451`, `1008-1021`; tests at `packages/cli/tests/workflow-run-domain.test.ts:338-397`.
- PASS: Rebase facts drive invalidation and failure through ordinary task semantics.
Evidence: `packages/cli/src/workflow/domain/index.ts:689-710`, `753-757`, `1109-1177`; tests at `packages/cli/tests/workflow/rebase-workflow-regression.test.ts:174-339`.

## Test Coverage

- PASS: Focused regression suites passed.
Command: `npm test -- --run tests/workflow/stage-runner-migration-regression.test.ts tests/workflow-run-domain.test.ts tests/workflow-engine-aggregate.test.ts tests/build-workflowrun-tasks.test.ts tests/workflow/rebase-workflow-regression.test.ts`
- PASS: 5 files, 92 tests passed.
- Warning: I did not run the full repository test suite, so overall repo-wide regression risk remains unverified.

## Complexity

- Warning: `packages/cli/src/workflow/config-driven-stage-runner.ts` is still very large and contains several high-branching methods, especially `executeTaskWork()` and stage-specific helpers. This increases change amplification and is consistent with the spec failures above.

## Security

- Warning: `commitPlanArtifacts()` shells out to `git commit --no-verify` at `packages/cli/src/workflow/config-driven-stage-runner.ts:947-951`. That is not a direct injection issue here, but it bypasses repository hooks and is risky operationally.

## Overall

- Result: FAIL.
- Reason: the implementation preserves a lot of behavior and tests, but it does not complete the core architectural migration promised by the spec because check/approval policies are not actually driving runtime behavior and task execution still depends on substantial stage-specific branching in the unified runner.

<promise>FAIL</promise>
