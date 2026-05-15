## Review Findings

### 1. Config-driven `rebase-branch` never triggers fact-driven invalidation

- Severity: Error
- Evidence:
  - `packages/cli/src/workflow/config-driven-stage-runner.ts:866-880` wraps `rebase-branch` as a `service-call` task and returns only `result.output` from `executeRebaseBranchTask(...)`.
  - `packages/cli/src/workflow/task-runtime/service-call-task-handler.ts:54-61` then stores that under `output.result`, producing a persisted task output shaped like `{ kind: 'service-call-task', ..., result: { shaChanged: ... } }`.
  - `packages/cli/src/workflow/domain/index.ts:1141-1150` and `1153-1167` read branch facts only from the top-level task output (`shaChanged`, `beforeBaseSha`, `afterBaseSha`, `beforeHeadSha`, `afterHeadSha`).
  - `packages/cli/src/workflow/domain/index.ts:1175-1208` applies invalidation policy based on that top-level fact detection.
- Impact:
  - In the real config-driven path, a successful `rebase-branch` task will not invalidate `ai-review`, `review-passed`, `merge-ready`, or approval even when the branch snapshot changed.
  - This violates `workflow-engine/spec.md` “Rebase facts drive invalidation” and `workflow-run/spec.md` “Rebase changed snapshot invalidates dependent state”.
- Suggested fix:
  - Either preserve rebase facts at the top level when recording the task result, or teach `detectShaChanged()` / invalidation evaluation to unwrap service-call outputs before checking facts.
  - Add an integration test that executes `rebase-branch` through `ConfigDrivenStageRunner`, not only through direct `WorkflowRun.completeTask(...)` calls.

### 2. Config-driven Integrate merge loses freeze-point delivery metadata

- Severity: Error
- Evidence:
  - `packages/cli/src/workflow/config-driven-stage-runner.ts:515-562` returns merge delivery facts (`targetBranch`, `baseSha`, `candidateHeadSha`, `landedSha`, `rebased`) from the integrate merge service function.
  - `packages/cli/src/workflow/task-runtime/service-call-task-handler.ts:54-61` wraps that payload inside `output.result`.
  - `packages/cli/src/workflow/domain/index.ts:761-767` captures the freeze point from `result.output` when `integrate:merge` completes.
  - `packages/cli/src/workflow/domain/index.ts:1212-1221` extracts delivery metadata only from top-level fields, so the wrapped config-driven result produces an empty `freezePoint.delivery`.
- Impact:
  - The config-driven Integrate path no longer preserves merge delivery metadata on the stage freeze point.
  - This is a regression in the “preserve existing stage semantics” contract for Integrate and weakens downstream consumers that rely on freeze-point delivery evidence.
- Suggested fix:
  - Preserve merge delivery fields at the top level of the stored task output, or update `extractDeliveryMetadata()` to unwrap service-call task outputs before reading fields.
  - Add a regression test that runs `integrate:merge` through the config-driven runner and asserts `freezePoint.delivery.baseSha`, `candidateHeadSha`, and `landedSha` are populated.

### 3. The runner is still stage-specific rather than registry-driven

- Severity: Warning
- Evidence:
  - `packages/cli/src/workflow/config-driven-stage-runner.ts:436-566`, `605-647`, and `939-1032` hardcode Plan artifact prompting, Integrate service work, Check AI review, and review-snapshot convergence directly inside the generic runner.
- Impact:
  - The migration achieves functional consolidation, but the main maintainability goal from the proposal/design is only partially met because stage behavior is still embedded in one large runner instead of being hidden behind registries/policies.
- Suggested fix:
  - Move stage-specific task construction into dedicated task loader/handler/factory registrations so the runner only resolves and dispatches declared work.

## Validation

- Focused tests run:
  - `npm test -- --run tests/workflow-run-domain.test.ts tests/workflow/stage-runner-migration-regression.test.ts tests/workflow/rebase-workflow-regression.test.ts`
- Result:
  - PASS, but these tests do not cover the real config-driven rebase/integrate output-shape path that triggers the failures above.

## Spec Compliance

### ralph-task-execution/spec.md

- PASS: Build materialization and handler path are implemented and covered by migration tests.
- PASS: Build health repair remains ordinary task work in `WorkflowRun` (`packages/cli/src/workflow/domain/index.ts:832-846`).

### workflow-definition/spec.md

- PASS: `DEFAULT_STAGE_DEFINITIONS` exposes declarative policies for Plan/Build/Check/Integrate and preserves stage order (`packages/cli/src/workflow/domain/index.ts:485-656`).
- FAIL: The runtime still depends on stage-specific branching inside `ConfigDrivenStageRunner` instead of resolving all behavior through registries/policies (`packages/cli/src/workflow/config-driven-stage-runner.ts:436-566`, `939-1032`).

### workflow-engine/spec.md

- PASS: Legacy and config-driven runner paths coexist; legacy runners remain registered behind the unified runner fallback (`packages/cli/src/services/agent-runner-service.ts:1260-1268`).
- PASS: Aggregate single-work execution remains supported (`packages/cli/src/workflow/workflow-engine.ts:265-309`, `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:849-889`).
- FAIL: Fact-driven invalidation for config-driven `rebase-branch` is broken because branch facts are wrapped and never observed by `WorkflowRun` (`packages/cli/src/workflow/config-driven-stage-runner.ts:866-880`, `packages/cli/src/workflow/task-runtime/service-call-task-handler.ts:54-61`, `packages/cli/src/workflow/domain/index.ts:1141-1208`).

### workflow-run/spec.md

- PASS: `WorkflowRun` remains the authority for next-work selection and repair scheduling (`packages/cli/src/workflow/domain/index.ts:742-846`, `988-1009`).
- PASS: Approval remains separate from checks in `WorkflowRun` (`packages/cli/src/workflow/domain/index.ts:1037-1049`, `857-889`).
- FAIL: Rebase changed-snapshot invalidation does not work in the actual config-driven execution path for the reason above.

### Additional Quality Checks

- Correctness: FAIL because of the two output-shape regressions above.
- Complexity: Warning. `packages/cli/src/workflow/config-driven-stage-runner.ts` is still a very large, multi-responsibility module.
- Test Coverage: Warning. Existing tests passed, but they miss the shipped config-driven rebase/integrate output path.
- Security: PASS. No obvious secret exposure or injection issue found in the changed code.

Overall result: FAIL

<promise>FAIL</promise>
