## Findings

1. Error: `rebase-branch` always invalidates Check review artifacts in the config-driven path, even when `shaChanged=false`.
File: `packages/cli/src/workflow/config-driven-stage-runner.ts:191-195,210-217,219-238`
Why it fails: `finalizeSuccessfulTask()` calls `invalidateReviewArtifactForRereview()` whenever `invalidatesReviewEvidence()` returns true. `invalidatesReviewEvidence()` only checks whether a matching invalidation entry exists for the task id and whether that entry invalidates `ai-review`; it does not evaluate the policy `when` condition. In `DEFAULT_STAGE_DEFINITIONS`, the Check-stage `rebase-branch` invalidation is explicitly conditional on `when: { shaChanged: true }` at `packages/cli/src/workflow/domain/index.ts:612-621`. As implemented, a successful config-driven `rebase-branch` run renames `review.md` and deletes the `ai-review` checkpoint even when the rebase reported no snapshot change.
Spec impact:
- FAIL `specs/workflow-engine/spec.md` Requirement `Config-driven invalidation applies branch and repair facts`, Scenario `Rebase facts drive invalidation`
- FAIL `specs/workflow-run/spec.md` Requirement `Rebase task reports facts before invalidation decisions`, Scenario `Rebase unchanged snapshot preserves dependent state`
Suggested fix: In `invalidatesReviewEvidence()` / `finalizeSuccessfulTask()`, evaluate the matched invalidation policy's `when` predicate against the completed task result output before invalidating artifacts/checkpoints. Reuse the same `shaChanged` detection semantics as `WorkflowRun.applyTaskCompletionInvalidation()`.

## Spec Compliance

### workflow-definition/spec.md

- PASS Requirement `Stage definitions declare workflow behavior policies`
Evidence: `packages/cli/src/workflow/domain/index.ts:485-656` defines Plan/Build/Check/Integrate with `workSources`, `taskExecutionPolicies`, `checkPolicies`, `approvalPolicy`, `repairPolicies`, and `invalidationPolicy`. Stage order remains `Plan -> Build -> Check -> Integrate` via `DEFAULT_STAGE_DEFINITIONS` order and `WorkflowRun.stageOrder()` at `packages/cli/src/workflow/domain/index.ts:485-656,694-696`.
- PASS Requirement `Stage definitions bind to task and check registries`
Evidence: `packages/cli/src/services/agent-runner-service.ts:1195-1258` wires `TaskHandlerRegistry`, `TaskLoaderRegistry`, `CheckRegistry`, and `ConfigDrivenStageRunner`; task resolution occurs in `packages/cli/src/workflow/config-driven-stage-runner.ts:724-780`, check resolution in `packages/cli/src/workflow/config-driven-stage-runner.ts:247-261`.
- PASS Requirement `Stage definitions preserve existing stage semantics`
Evidence: Plan tasks/checks/policy in `packages/cli/src/workflow/domain/index.ts:487-535`; Build Ralph source/policy in `:537-563`; Check AI review/repair/invalidation in `:565-624`; Integrate ordered tasks/check in `:626-655`. Migration coverage exists in `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:596-770,778-1087,1454-1490,1493-1565`.

### workflow-engine/spec.md

- PASS Requirement `Config-driven runner executes declared stage work`
Evidence: task dispatch through registries in `packages/cli/src/workflow/config-driven-stage-runner.ts:127-189,401-434,724-780`; check dispatch in `:240-278`; engine aggregate loop resumes from WorkflowRun decisions in `packages/cli/src/workflow/workflow-engine.ts:223-310`.
- PASS Requirement `Legacy and config-driven runner paths coexist during migration`
Evidence: legacy runners are still constructed and kept in the runner list at `packages/cli/src/services/agent-runner-service.ts:1260-1268`; rollback coverage in `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:1493-1565`.
- PASS Requirement `Config-driven checks preserve read-only and repair policy boundaries`
Evidence: checks call `recordCheckResult()` only and repairs are scheduled by WorkflowRun policy, not by check implementations, at `packages/cli/src/workflow/domain/index.ts:789-855`; runner test coverage at `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:924-966`.
- FAIL Requirement `Config-driven invalidation applies branch and repair facts`
Evidence: although WorkflowRun applies fact-driven invalidation correctly at `packages/cli/src/workflow/domain/index.ts:1153-1210`, the config-driven runner unconditionally invalidates persisted review artifacts for `rebase-branch` in `packages/cli/src/workflow/config-driven-stage-runner.ts:191-195,210-238`, ignoring `when.shaChanged`. Actual behavior is artifact invalidation on any successful `rebase-branch` task.
- PASS Requirement `Aggregate single-work execution remains supported`
Evidence: engine executes only the requested work item in aggregate mode at `packages/cli/src/workflow/workflow-engine.ts:247-310`; focused tests pass in `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:778-1087`.

### workflow-run/spec.md

- PASS Requirement `WorkflowRun selects work across configured sources`
Evidence: task-before-check selection in `packages/cli/src/workflow/domain/index.ts:988-1010`; Build materialization before health-check selection in `packages/cli/src/workflow/workflow-engine.ts:193-221,241-245` and `packages/cli/tests/workflow-engine-aggregate.test.ts:254-319`.
- PASS Requirement `StageRun records source and policy-driven work consistently`
Evidence: dynamic/static/runtime tasks and checks are materialized and persisted through `packages/cli/src/workflow/domain/index.ts:335-350,426-459`; repo-backed snapshots use `taskId`, `taskOrder`, and `checkName` fields via `packages/cli/src/workflow/workflow-engine.ts:136-164`.
- PASS Requirement `Approval is separate from checks in WorkflowRun decisions`
Evidence: approval wait is modeled distinctly in `packages/cli/src/workflow/domain/index.ts:1037-1049`; runtime rebase reopens stage without blindly clearing approval in `:718-739` and invalidation only clears approval when policy says so in `:1202-1207`.
- FAIL Requirement `Rebase task reports facts before invalidation decisions`
Evidence: WorkflowRun domain logic honors `shaChanged` in `packages/cli/src/workflow/domain/index.ts:1141-1179`, but config-driven runner side effects do not: `packages/cli/src/workflow/config-driven-stage-runner.ts:210-217,219-238` invalidates persisted review state before checking reported facts. Actual value ignored: `when.shaChanged === true` from `packages/cli/src/workflow/domain/index.ts:612-621`.

### ralph-task-execution/spec.md

- PASS Requirement `Build dynamic tasks execute through config-driven work source`
Evidence: Build stage definition declares `workSources: [{ kind: 'ralph' }, { kind: 'runtime' }]` and wildcard Ralph execution policy at `packages/cli/src/workflow/domain/index.ts:545-553`; materialization and execution path in `packages/cli/src/workflow/config-driven-stage-runner.ts:650-718,724-749`; regression coverage in `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:596-770`.
- PASS Requirement `Build migration preserves Ralph resume and checkpoint behavior`
Evidence: engine materializes before selection and avoids duplication via persisted task ids in `packages/cli/src/workflow/config-driven-stage-runner.ts:655-693,720-722`; tests in `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:717-771` and `packages/cli/tests/build-workflowrun-tasks.test.ts:89-190,192-247,335-360`.
- PASS Requirement `Build health repair remains ordinary task work`
Evidence: `health:build` repair policy at `packages/cli/src/workflow/domain/index.ts:542-559`; fix scheduling in `packages/cli/src/workflow/domain/index.ts:832-845`; repair task execution through shared runtime in `packages/cli/src/workflow/config-driven-stage-runner.ts:418-434`; domain coverage in `packages/cli/tests/workflow-run-domain.test.ts:206-218,323-323`.

## Verification

- PASS Focused tests: `pnpm vitest run packages/cli/tests/workflow/stage-runner-migration-regression.test.ts packages/cli/tests/workflow-engine-aggregate.test.ts packages/cli/tests/workflow/rebase-workflow-regression.test.ts`
- PASS Build/typecheck: `npm run build` in `packages/cli`

## Overall

Overall result: FAIL due to the config-driven `rebase-branch` invalidation regression above.

<promise>FAIL</promise>
