## Review

### Overall

FAIL.

### Error Findings

1. Runtime-added rebase work blindly clears approval before any invalidation policy or branch facts are applied.
File: `packages/cli/src/workflow/domain/index.ts:701-703`
Why it fails: `scheduleRebaseTask()` immediately flips the stage from `awaiting-approval` back to `running` and sets `approval = null`. The spec requires prior approval evidence to be invalidated only by configured invalidation policy plus reported task facts, not merely because a runtime task was appended.
Spec impact:
- `workflow-run/spec.md` Requirement `Approval is separate from checks in WorkflowRun decisions`, Scenario `Runtime task does not blindly erase approval evidence`: FAIL
- `workflow-engine/spec.md` Requirement `Config-driven invalidation applies branch and repair facts`, Scenario `Rebase facts drive invalidation`: FAIL
Suggested fix:
- Keep the stage runnable when adding runtime tasks, but preserve `stageRun.approval` until `completeTask()` applies an invalidation entry whose `invalidates.approval` matches reported facts.

2. Approval invalidation policy is never actually applied after task completion.
File: `packages/cli/src/workflow/domain/index.ts:1157-1159`
Why it fails: when an invalidation entry sets `invalidates.approval`, the code only emits another `approval-requested` event if approval is awaiting; it does not clear or reset `stageRun.approval`, and it does not move the stage out of stale approval state. As written, `shaChanged=true` or `fix-review-findings` do not invalidate approval state even though the policy declares they should.
Spec impact:
- `workflow-engine/spec.md` Requirement `Config-driven invalidation applies branch and repair facts`, Scenario `Review repair invalidates stale review state`: FAIL
- `workflow-run/spec.md` Requirement `Approval is separate from checks in WorkflowRun decisions`, Scenario `Runtime task does not blindly erase approval evidence`: FAIL
- `workflow-run/spec.md` Requirement `Rebase task reports facts before invalidation decisions`, Scenario `Rebase changed snapshot invalidates dependent state`: FAIL
Suggested fix:
- In `applyTaskCompletionInvalidation()`, explicitly clear or reset approval state when `invalidates.approval` is true, and ensure the stage becomes runnable again only through that policy-driven path.

3. The config-driven runner does not use `TaskLoaderRegistry`, and Build full-stage still executes a runner-local Ralph loop.
Files:
- `packages/cli/src/workflow/config-driven-stage-runner.ts:52`
- `packages/cli/src/workflow/config-driven-stage-runner.ts:965-1093`
- `packages/cli/src/services/agent-runner-service.ts:1206-1208`
Why it fails:
- The injected `taskLoaderRegistry` is discarded with `void options.taskLoaderRegistry`.
- The default runner is constructed with an empty registry.
- Build full-stage directly calls `runRalphLoop(...)` inside `ConfigDrivenStageRunner` instead of resolving executable work from stage definition work sources and executing the selected task through the shared registry/handler path.
This violates the stated migration architecture and leaves the main config-driven path bypassing the loader registry entirely.
Spec impact:
- `workflow-definition/spec.md` Requirement `Stage definitions bind to task and check registries`, Scenario `Static non-Build work resolves from definition`: FAIL
- `workflow-engine/spec.md` Requirement `Config-driven runner executes declared stage work`, Scenario `Runner executes requested task from registries`: FAIL
- `ralph-task-execution/spec.md` Requirement `Build dynamic tasks execute through config-driven work source`, Scenario `Build task executes through Ralph handler`: FAIL for full-stage/default path
Suggested fix:
- Wire `TaskLoaderRegistry` into `ConfigDrivenStageRunner` and materialize tasks through configured work sources.
- Replace `runBuildFullStage()`'s direct `runRalphLoop()` orchestration with the same `WorkflowRun.nextWork() -> loader -> handler -> report result` path used for requested tasks.
- Register real loaders in `AgentRunnerService` instead of `{ get: () => undefined, list: () => [] }`.

4. Plan config-driven execution commits with `--no-verify`, violating repo git safety expectations and changing behavior relative to requested workflow semantics.
File: `packages/cli/src/workflow/config-driven-stage-runner.ts:614-618`
Why it fails: the runner performs `git commit ... --no-verify` unconditionally. This skips hooks without any user request and introduces side effects inside the stage runner that are unrelated to `WorkflowRun` orchestration.
Spec impact:
- `workflow-definition/spec.md` Requirement `Stage definition remains non-executing`: warning on architecture drift
- Quality/Safety review dimension: FAIL
Suggested fix:
- Remove `--no-verify` and, if this commit behavior is required at all, route it through an explicit service/helper with normal hook behavior.

### Warnings

1. Focused workflow tests pass, but they currently encode one of the approval regressions instead of catching it.
Evidence:
- `packages/cli/tests/workflow/rebase-workflow-regression.test.ts:133-143` expects approval to be cleared on `scheduleRebaseTask()`.
- Focused run passed: `npm test -- --run tests/workflow-run-domain.test.ts tests/workflow/rebase-workflow-regression.test.ts tests/workflow/stage-runner-migration-regression.test.ts tests/workflow-engine-aggregate.test.ts`
Suggested fix:
- Update these assertions to require approval preservation until fact-driven invalidation occurs, then add a positive test that `shaChanged=true` or `fix-review-findings` actually clears approval.

### Spec Compliance

#### ralph-task-execution/spec.md

- Requirement `Build dynamic tasks execute through config-driven work source`: FAIL
Evidence: `ConfigDrivenStageRunner` bypasses loader registry (`config-driven-stage-runner.ts:52`) and full-stage Build directly runs `runRalphLoop` (`config-driven-stage-runner.ts:1036`).
- Requirement `Build migration preserves Ralph resume and checkpoint behavior`: PASS with warning
Evidence: checkpoint reuse and no duplicate materialization are covered by `materializeTasks()` de-dupe (`domain/index.ts:334-349`) and checkpoint use in Build (`config-driven-stage-runner.ts:994, 1037-1043`), but the execution path is still runner-local.
- Requirement `Build health repair remains ordinary task work`: PASS
Evidence: failed health checks append repair tasks via policy (`domain/index.ts:804-817`) and next work returns the fix task before re-check.

#### workflow-definition/spec.md

- Requirement `Stage definitions declare workflow behavior policies`: PASS
Evidence: `DEFAULT_STAGE_DEFINITIONS` includes `workSources`, `taskExecutionPolicies`, `checkPolicies`, `approvalPolicy`, `repairPolicies`, `invalidationPolicy` for Plan/Build/Check/Integrate (`domain/index.ts:468-627`).
- Requirement `Stage definitions bind to task and check registries`: FAIL
Evidence: checks are registry-backed (`config-driven-stage-runner.ts:302-305`), but task loader registry is unused (`config-driven-stage-runner.ts:52`).
- Requirement `Stage definitions preserve existing stage semantics`: FAIL
Evidence: approval invalidation semantics are wrong (`domain/index.ts:701-703`, `1157-1159`), and Build default path still uses runner-local Ralph orchestration (`config-driven-stage-runner.ts:965-1093`).

#### workflow-engine/spec.md

- Requirement `Config-driven runner executes declared stage work`: FAIL
Evidence: requested checks use the registry (`config-driven-stage-runner.ts:302-305`), but requested/full-stage task execution is not consistently resolved from configured work sources because the loader registry is unused and Build full-stage directly invokes Ralph.
- Requirement `Legacy and config-driven runner paths coexist during migration`: PASS
Evidence: legacy runner files remain and coexist tests exist (`tests/workflow/stage-runner-migration-regression.test.ts:884-935`).
- Requirement `Config-driven checks preserve read-only and repair policy boundaries`: PASS
Evidence: check failures schedule repair tasks in `WorkflowRun.recordCheckResult()` rather than inside check implementations (`domain/index.ts:804-817`).
- Requirement `Config-driven invalidation applies branch and repair facts`: FAIL
Evidence: policy entries exist, but approval invalidation is not applied, and approval is cleared too early on scheduling (`domain/index.ts:701-703`, `1157-1159`).
- Requirement `Aggregate single-work execution remains supported`: PASS
Evidence: aggregate path uses `requestedWork`, and focused aggregate tests passed.

#### workflow-run/spec.md

- Requirement `WorkflowRun selects work across configured sources`: PASS with warning
Evidence: one ordered stage task list is maintained and `nextWork()` selects tasks before checks (`domain/index.ts:351-360`, `960-980`), but task materialization is still not consistently driven by configured loaders in the runner.
- Requirement `StageRun records source and policy-driven work consistently`: PASS with warning
Evidence: tasks/checks carry stable ids/status/output/causedBy (`domain/index.ts:185-223`, `263-299`), but there is still no explicit `source` metadata field despite the spec wording.
- Requirement `Approval is separate from checks in WorkflowRun decisions`: FAIL
Evidence: approval is erased in `scheduleRebaseTask()` before any policy facts are reported (`domain/index.ts:701-703`).
- Requirement `Rebase task reports facts before invalidation decisions`: FAIL
Evidence: approval changes happen at scheduling time, and completion-time invalidation does not clear approval state (`domain/index.ts:701-703`, `1125-1161`).

### Test Coverage

- Focused suites passed:
  - `tests/workflow-run-domain.test.ts`
  - `tests/workflow/rebase-workflow-regression.test.ts`
  - `tests/workflow/stage-runner-migration-regression.test.ts`
  - `tests/workflow-engine-aggregate.test.ts`
- Command: `npm test -- --run tests/workflow-run-domain.test.ts tests/workflow/rebase-workflow-regression.test.ts tests/workflow/stage-runner-migration-regression.test.ts tests/workflow-engine-aggregate.test.ts`
- Gap: existing tests assert the premature approval clearing behavior, so they do not protect the spec-compliant behavior.

<promise>FAIL</promise>
