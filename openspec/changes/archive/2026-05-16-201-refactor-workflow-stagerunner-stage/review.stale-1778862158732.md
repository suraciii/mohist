## Findings

1. Error: Build task materialization is skipped whenever any task already exists in the StageRun, which breaks the required multi-source task model and can prevent Ralph tasks from ever loading after a runtime-added task such as `rebase-branch`.
File: `packages/cli/src/workflow/config-driven-stage-runner.ts:795-801`
Evidence: `stageNeedsTaskMaterialization()` returns `true` only when `stageRun.tasks.length === 0`. That means if Build already has a runtime-added task or repair task, `materializeConfiguredStageTasks()` never runs, so Ralph tasks from `tasks.json` are never appended into the same ordered task list.
Spec impact:
- FAIL `specs/workflow-run/spec.md#workflowrun-selects-work-across-configured-sources` Scenario: `Multiple work sources materialize into one StageRun task list`
- FAIL `specs/ralph-task-execution/spec.md#build-dynamic-tasks-execute-through-config-driven-work-source` Scenario: `Build materializes Ralph tasks before selection`
Suggested fix: Change materialization gating to detect whether the configured Ralph tasks have already been materialized, not whether the stage has zero tasks. For example, compare loaded Ralph task ids against existing StageRun task ids and append only the missing ones.

2. Error: The change adds a new mandatory `health:check` gate to the Check stage, changing stage semantics beyond the approved scope and making approval depend on an extra check that the legacy Check runner never required.
Files:
- `packages/cli/src/workflow/domain/index.ts:546-575`
- `packages/cli/src/workflow/domain/index.ts:1070-1084`
- `packages/cli/src/services/agent-runner-service.ts:1236-1239`
Evidence:
- `DEFAULT_STAGE_DEFINITIONS` now adds `health:check` to Check checks and repair policies.
- `buildApprovalOutput()` now refuses Check approval output unless `health:check` is also `passed`.
- The default registry wires a real `HealthGateCheck` for Check.
- The legacy baseline still defines Check post-task checks as only `review-passed`, `merge-ready`, and `user-approval`: `packages/cli/src/workflow/check-stage-runner.ts:38-42` and `packages/cli/tests/check-stage-ordering.test.ts:176-183`.
Spec impact:
- FAIL `specs/workflow-definition/spec.md#stage-definitions-preserve-existing-stage-semantics` Scenario: `Check definition preserves review contract`
- FAIL `specs/workflow-engine/spec.md#config-driven-checks-preserve-read-only-and-repair-policy-boundaries` Scenario: `Approval remains a user decision point`
Suggested fix: Remove `health:check` from the Check stage definition and approval gating unless there is a separately approved spec change for Check-stage health. If a Check health gate is desired, it should land in its own issue/spec update and preserve backward compatibility explicitly.

3. Warning: The config-driven Plan path no longer commits plan artifacts or clears the Plan checkpoint on stage success before approval, unlike the legacy runner.
Files:
- `packages/cli/src/workflow/plan-stage-runner.ts:510-517`
- `packages/cli/src/workflow/config-driven-stage-runner.ts:359-459`
Evidence:
- Legacy Plan executes `commitPlanArtifacts(...)` and `checkpointManager.delete(issue.number, 'plan')` after task completion.
- The config-driven Plan task path only marks per-task checkpoint steps; there is no equivalent stage-success cleanup in `ConfigDrivenStageRunner`.
Risk: resumed or approval-paused Plan runs can keep stale checkpoint state and lose the previous plan artifact commit behavior.
Suggested fix: Restore the legacy stage-success finalization for Plan in the config-driven path, or document and test the intended behavior change explicitly.

## Spec Compliance

### `specs/ralph-task-execution/spec.md`

- FAIL Requirement `Build dynamic tasks execute through config-driven work source`
Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:795-823` only materializes Ralph work when the stage has zero tasks.
- FAIL Scenario `Build materializes Ralph tasks before selection`
Evidence: a pre-existing runtime task blocks Ralph materialization entirely.
- PASS Scenario `Build task executes through Ralph handler`
Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:263-268` dispatches Build tasks through `ralph-task`.
- PASS Requirement `Build migration preserves Ralph resume and checkpoint behavior`
Evidence: no duplicate local Build loop remains; execution resolves through `WorkflowRun` requested work and handler dispatch.
- PASS Scenario `Build resumes from materialized task state`
Evidence: `resolveExecutableTask()` and aggregate resume use persisted `requestedWork`; no replacement Build loop was reintroduced.
- PASS Scenario `Aggregate single Build task execution remains supported`
Evidence: `run()` only executes `ctx.requestedWork`; see `packages/cli/src/workflow/config-driven-stage-runner.ts:66-80,110-139`.
- PASS Requirement `Build health repair remains ordinary task work`
Evidence: `fix-build-health` dispatches through the shared repair adapter at `packages/cli/src/workflow/config-driven-stage-runner.ts:201-208`.
- PASS Scenario `Build health failure schedules configured fix task`
Evidence: scheduling remains in `WorkflowRun.recordCheckResult()` via repair policy at `packages/cli/src/workflow/domain/index.ts:807-820`.
- PASS Scenario `Build health remains blocked by failed tasks`
Evidence: checks are blocked until all required tasks succeed at `packages/cli/src/workflow/domain/index.ts:767-769,1013-1014`.

### `specs/workflow-definition/spec.md`

- PASS Requirement `Stage definitions declare workflow behavior policies`
Evidence: built-in definitions include `workSources`, `taskExecutionPolicies`, `checkPolicies`, `approvalPolicy`, `repairPolicies`, and `invalidationPolicy` in `packages/cli/src/workflow/domain/index.ts`.
- PASS Scenario `Default stages expose declarative policies`
Evidence: stage order is unchanged and definitions remain ordered `plan -> build -> check -> integrate -> done`.
- PASS Scenario `Stage definition remains non-executing`
Evidence: `packages/cli/src/workflow/domain/index.ts` remains declarative data only.
- PASS Requirement `Stage definitions bind to task and check registries`
Evidence: registry wiring in `packages/cli/src/services/agent-runner-service.ts:1210-1254`.
- PASS Scenario `Static non-Build work resolves from definition`
Evidence: static loader maps declared task ids to executable tasks in `agent-runner-service.ts:1210-1219`.
- PASS Scenario `Checks resolve from check policy`
Evidence: config-driven check execution resolves by name through `resolveCheck(...)` in `packages/cli/src/workflow/config-driven-stage-runner.ts:148-151`.
- PASS Scenario `Plan definition preserves planning contract`
Evidence: Plan still declares proposal/specs/design/tasks/self-review tasks and checks in `packages/cli/src/workflow/domain/index.ts`.
- FAIL Scenario `Check definition preserves review contract`
Evidence: Check now requires a new `health:check`, changing the stage contract from AI review -> review/merge checks -> approval.
- FAIL Scenario `Build definition preserves Ralph contract`
Evidence: Ralph work is not guaranteed to materialize when runtime-added tasks already exist.
- PASS Scenario `Integrate definition preserves integration contract`
Evidence: ordered Integrate tasks and health gate remain declarative in the stage definition.

### `specs/workflow-engine/spec.md`

- PASS Requirement `Config-driven runner executes declared stage work`
Evidence: task and check execution go through registries in `packages/cli/src/workflow/config-driven-stage-runner.ts:66-317`.
- PASS Scenario `Runner executes requested task from registries`
Evidence: requested task path resolves executable work and reports it through `workflowApplicationService.completeTask(...)`.
- PASS Scenario `Runner executes requested check from registry`
Evidence: requested check path resolves via `resolveCheck(...)` and records via `recordCheckResult(...)`.
- PASS Scenario `Runner does not decide stage progression`
Evidence: runner only records task/check results; stage progression remains in `WorkflowRun.nextWork()` and `maybeCompleteStage()`.
- PASS Requirement `Legacy and config-driven runner paths coexist during migration`
Evidence: `packages/cli/src/services/agent-runner-service.ts:1256-1264` keeps legacy runners and allows env-based rollback.
- PASS Scenario `Unmigrated stage can use legacy runner path`
Evidence: legacy runner instances remain constructed and registered.
- PASS Scenario `Migrated stage uses config-driven path independently`
Evidence: unified runner is prepended while legacy runners remain in the list.
- PASS Scenario `Unified runner becomes default only after all stages migrate`
Evidence: default registration now prefers unified runner while legacy files remain present.
- PASS Requirement `Config-driven checks preserve read-only and repair policy boundaries`
Evidence: failed checks are scheduled in `WorkflowRun.recordCheckResult()`; check implementations do not run repairs.
- FAIL Scenario `Approval remains a user decision point`
Evidence: approval for Check now depends on an added non-specified `health:check`, changing the point at which approval is exposed.
- PASS Requirement `Config-driven invalidation applies branch and repair facts`
Evidence: invalidation policy is applied in `packages/cli/src/workflow/domain/index.ts:1141-1180`.
- PASS Scenario `Review repair invalidates stale review state`
Evidence: `fix-review-findings` invalidates `ai-review`, `review-passed`, `merge-ready`, and approval through policy.
- PASS Scenario `Rebase facts drive invalidation`
Evidence: `when: { shaChanged: true }` invalidation remains fact-driven in `packages/cli/src/workflow/domain/index.ts:589-597`.
- PASS Requirement `Aggregate single-work execution remains supported`
Evidence: `ConfigDrivenStageRunner.run()` requires and executes exactly `requestedWork`.
- PASS Scenario `Aggregate requested task executes once`
Evidence: task path only executes the requested task.
- PASS Scenario `Aggregate requested check executes once`
Evidence: check path only executes the requested check.

### `specs/workflow-run/spec.md`

- FAIL Requirement `WorkflowRun selects work across configured sources`
Evidence: Build materialization currently depends on an empty task list, so multiple sources are not merged reliably.
- FAIL Scenario `Multiple work sources materialize into one StageRun task list`
Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:795-823` skips Ralph loading when any runtime task already exists.
- PASS Scenario `Runtime-added task blocks later checks`
Evidence: `WorkflowRun.nextWork()` always selects pending tasks before checks at `packages/cli/src/workflow/domain/index.ts:979-983`.
- PASS Requirement `StageRun records source and policy-driven work consistently`
Evidence: repair tasks and runtime-added tasks still flow through ordinary `completeTask` semantics.
- PASS Scenario `Static and dynamic tasks share task semantics`
Evidence: all task results are persisted through `WorkflowRun.completeTask()`.
- PASS Scenario `Checks share check semantics`
Evidence: all check results are recorded through `WorkflowRun.recordCheckResult()`.
- PASS Requirement `Approval is separate from checks in WorkflowRun decisions`
Evidence: approval is stored separately on `stageRun.approval` and not scheduled as a repair task.
- PASS Scenario `Approval wait follows successful checks`
Evidence: `maybeCompleteStage()` requests approval only after required tasks and checks pass.
- PASS Scenario `Runtime task does not blindly erase approval evidence`
Evidence: `scheduleRebaseTask()` now reopens the stage without immediately clearing approval at `packages/cli/src/workflow/domain/index.ts:705-707`.
- PASS Requirement `Rebase task reports facts before invalidation decisions`
Evidence: invalidation remains driven by `applyTaskCompletionInvalidation()` and `when.shaChanged`.
- PASS Scenario `Rebase changed snapshot invalidates dependent state`
Evidence: policy entry on `rebase-branch` with `shaChanged: true`.
- PASS Scenario `Rebase unchanged snapshot preserves dependent state`
Evidence: invalidation condition gates on `shaChanged: true`, so unchanged snapshots do not invalidate.
- PASS Scenario `Rebase failure blocks workflow`
Evidence: failed tasks fail the stage via `completeTask()`.

## Test Coverage

- PASS Added regression coverage exists for aggregate execution, migration behavior, rebase invalidation, and WorkflowRun domain behavior.
- WARNING I did not execute the test suite in this review pass, so runtime green status is not independently verified here.

## Overall

Overall result: FAIL due to the Build materialization bug and the unapproved Check-stage semantic expansion.

<promise>FAIL</promise>
