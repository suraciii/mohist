## Findings

1. High: `StageStateService` counts resolved, pre-existing, and out-of-scope blocking items as current blockers, so the API can overstate `blockingItemCount` and understate non-blocking follow-ups.
File references: `packages/cli/src/services/stage-state-service.ts:597-606`, `packages/cli/src/workflow/convergence.ts:295-317`.
Evidence: the stage-state projection treats every item with `severity === 'blocking'` as blocking, while the runtime convergence helper correctly excludes `status === 'resolved'`, `pre-existing`, and `out-of-scope`. This means API/UI convergence state can disagree with the authoritative workflow logic after direct repair or verification.
Suggested fix: reuse the same blocking/non-blocking classification rules in `StageStateService.computeConvergenceState()` that `workflow/convergence.ts` already applies, or delegate the projection to a single shared helper.

2. High: Structured convergence is still hard-coded to review-specific task and check IDs in core scheduling and projection paths, so non-review workflows cannot use the shared mechanism promised by the spec.
File references: `packages/cli/src/services/stage-state-service.ts:456-461`, `packages/cli/src/services/stage-state-service.ts:483-551`, `packages/cli/src/services/stage-state-service.ts:577-680`, `packages/cli/src/services/workflow-application-service.ts:229-299`, `packages/cli/src/workflow/check-stage-runner.ts:103-133`, `packages/cli/src/workflow/check-stage-runner.ts:141-150`, `packages/cli/src/workflow/check-stage-runner.ts:458-482`, `packages/cli/src/workflow/config-driven-stage-runner.ts:200-258`.
Evidence: convergence state, repair scheduling, verification-context persistence, and repair UI state all special-case `review-passed` and `fix-review-findings` instead of driving from generic `checkFailurePolicies` / `repairPolicies` metadata and structured outputs. The data model is generic, but the control flow is not.
Suggested fix: identify failed checks and repair tasks through declared workflow policies and generic structured reaction metadata, then compute convergence from those declarations instead of task/check name literals.

3. Medium: The issue UI still does not render visible non-blocking follow-up items; it only renders a count.
File references: `packages/cli/web/src/lib/types.ts:81-92`, `packages/cli/web/src/components/WorkflowConvergencePanel.tsx:67-75`, `packages/cli/web/src/components/workflow-convergence-panel.test.tsx:80-90`.
Evidence: the API/UI model exposes only `nonBlockingItemIds: string[]`, and the panel shows `Follow-up items: {nonBlockingItemIds.length}` plus helper text. The spec requires visible non-blocking follow-up items, not just their total.
Suggested fix: expose a generic follow-up item summary list from the API, then render those items in `WorkflowConvergencePanel` and cover that behavior in the component test.

## Spec Compliance

- PASS: Structured generic result types exist without introducing review-specific core entities.
Evidence: `packages/cli/src/types/workflow-results.ts:3-100`.
- PASS: AI judgment tasks declare result contracts and self-repair policy metadata.
Evidence: `packages/cli/src/workflow/domain/index.ts:26-32`, `packages/cli/src/workflow/domain/index.ts:712-719`, `packages/cli/src/workflow/domain/index.ts:834-845`.
- PASS: Shared promise-marker parser exists and enforces missing/duplicate/malformed marker errors from the declared source.
Evidence: `packages/cli/src/workflow/result-contracts.ts:23-229`, `packages/cli/tests/workflow/result-contracts.test.ts:21-306`.
- PASS: Review and self-review checks both use the shared parser/contract path.
Evidence: `packages/cli/src/workflow/checks/review-passed-check.ts:33-83`, `packages/cli/src/workflow/checks/self-review-passed-check.ts:9-50`.
- PASS: Failed check context includes blocking items, non-blocking items, snapshot metadata, and prior task outputs.
Evidence: `packages/cli/src/workflow/domain/index.ts:143-183`.
- PASS: Reaction outputs record attempted, resolved, unresolved, and newly observed item IDs.
Evidence: `packages/cli/src/types/workflow-results.ts:49-54`, `packages/cli/src/workflow/convergence.ts:67-133`.
- PASS: Verification mode persists known-item context and appends it to the re-review prompt before recheck.
Evidence: `packages/cli/src/workflow/convergence.ts:165-247`, `packages/cli/src/workflow/check-stage-runner.ts:573-605`, `packages/cli/src/workflow/check-stage-runner.ts:683-703`.
- PASS: Reaction prompts consume the full blocking item batch instead of scraping only prose when structured context exists.
Evidence: `packages/cli/src/workflow/task-runtime/repair-fix-adapter.ts:251-317`, `packages/cli/tests/workflow/reaction-structured-context.test.ts:173-351`.
- FAIL: API convergence projection misclassifies resolved / pre-existing / out-of-scope blocking items as current blockers.
Evidence: `packages/cli/src/services/stage-state-service.ts:597-606` vs. `packages/cli/src/workflow/convergence.ts:295-317`.
- FAIL: Core convergence scheduling/projection remains review-specific instead of policy-driven and generic.
Evidence: `packages/cli/src/services/stage-state-service.ts:456-461`, `packages/cli/src/services/workflow-application-service.ts:229-299`, `packages/cli/src/workflow/check-stage-runner.ts:103-133`, `packages/cli/src/workflow/config-driven-stage-runner.ts:200-258`.
- FAIL: UI does not show visible non-blocking follow-up items.
Evidence: `packages/cli/web/src/components/WorkflowConvergencePanel.tsx:67-75`, `packages/cli/web/src/lib/types.ts:81-92`.

## Acceptance Criteria

- PASS: Mohist workflow can represent structured `items[]` on task/check outputs without introducing review-specific core entities.
Evidence: `packages/cli/src/types/workflow-results.ts:3-100`.
- PASS: AI judgment tasks can declare a required structured verdict marker contract, defaulting to `<promise>PASS</promise>` / `<promise>FAIL</promise>`.
Evidence: `packages/cli/src/workflow/domain/index.ts:712-719`, `packages/cli/src/workflow/domain/index.ts:760-845`.
- PASS: Mohist uses a shared parser/contract to derive PASS/FAIL from the explicit marker instead of inferring from natural-language prose.
Evidence: `packages/cli/src/workflow/result-contracts.ts:137-229`, `packages/cli/src/workflow/checks/review-passed-check.ts:33-83`, `packages/cli/src/workflow/checks/self-review-passed-check.ts:9-50`.
- PASS: Missing, duplicate, or malformed verdict markers produce a clear check error.
Evidence: `packages/cli/src/workflow/checks/review-passed-check.ts:38-49`, `packages/cli/src/workflow/checks/self-review-passed-check.ts:18-20`, `packages/cli/tests/workflow/result-contracts.test.ts:79-169`.
- PASS: Verdict markers are parsed only from the task/check's declared output source, not from arbitrary logs or transcript text.
Evidence: `packages/cli/src/workflow/result-contracts.ts:137-229`, `packages/cli/tests/workflow/result-contracts.test.ts:171-225`.
- PASS: Review and self-review use the same generic verdict parsing contract.
Evidence: `packages/cli/src/workflow/checks/review-passed-check.ts:33-83`, `packages/cli/src/workflow/checks/self-review-passed-check.ts:9-50`.
- PASS: Task definitions can express whether a task implementation may perform limited in-session repair and what boundaries apply.
Evidence: `packages/cli/src/types/workflow-results.ts:71-77`, `packages/cli/src/workflow/domain/index.ts:834-845`.
- PASS: The built-in review task performs a comprehensive pass and does not intentionally stop after the first blocking item.
Evidence: prompt coverage asserted in `packages/cli/tests/workflow/prompt-structure.test.ts:1-140`.
- PASS: The built-in review task can directly fix safe, local, low-risk items and records repaired item IDs, changed evidence, and verification results.
Evidence: `packages/cli/src/workflow/checks/review-passed-check.ts:56-70`, prompt/test coverage in `packages/cli/tests/workflow/self-repair.test.ts:1-220`.
- PASS: If the built-in review task changes the candidate, its final verdict is based on the post-repair snapshot and unresolved items remain visible.
Evidence: `packages/cli/src/workflow/checks/review-passed-check.ts:52-83`, `packages/cli/src/workflow/convergence.ts:205-247`.
- PASS: The built-in review task reports ambiguous, broad, product-affecting, security/data-risk, or out-of-scope issues instead of repairing them silently.
Evidence: prompt/test coverage in `packages/cli/tests/workflow/self-repair.test.ts:118-220`.
- PASS: A check failure can pass structured failed context into a configured reaction task.
Evidence: `packages/cli/src/workflow/domain/index.ts:80-87`, `packages/cli/src/workflow/check-stage-runner.ts:607-680`.
- PASS: Reaction task output records attempted, resolved, and unresolved item IDs.
Evidence: `packages/cli/src/types/workflow-results.ts:49-54`, `packages/cli/src/workflow/convergence.ts:67-133`.
- FAIL: Stage state and issue UI expose generic convergence status: failed check, blocking item count, directly repaired count, reaction attempts, resolved/unresolved counts, and blocked reason.
Deviation: the projected `blockingItemCount` can be wrong for resolved/pre-existing/out-of-scope blocking items.
Evidence: `packages/cli/src/services/stage-state-service.ts:597-606`.
- FAIL: The built-in review workflow uses this generic mechanism to batch blocking review items before repair.
Deviation: batching exists, but the runtime mechanism is still review-specific rather than generic.
Evidence: `packages/cli/src/workflow/check-stage-runner.ts:103-133`, `packages/cli/src/services/workflow-application-service.ts:229-299`.
- PASS: `fix-review-findings` receives the full relevant item batch and attempts related fixes together.
Evidence: `packages/cli/src/workflow/task-runtime/repair-fix-adapter.ts:255-294`, `packages/cli/tests/workflow/reaction-structured-context.test.ts:173-239`.
- PASS: Recheck after repair verifies known item resolution before treating policy-allowed new blockers as blocking.
Evidence: `packages/cli/src/workflow/convergence.ts:205-247`, `packages/cli/tests/workflow/convergence-recheck.test.ts:288-391`.
- FAIL: Follow-up or out-of-scope items can be visible without blocking the current workflow by default.
Deviation: they are counted, but not rendered visibly in the UI.
Evidence: `packages/cli/web/src/components/WorkflowConvergencePanel.tsx:67-75`.
- PASS: Existing task/check boundaries remain intact: tasks execute and may modify artifacts/code; checks only verify.
Evidence: `packages/cli/src/workflow/checks/review-passed-check.ts:33-83`, `packages/cli/tests/workflow/reaction-structured-context.test.ts:412-510`.
- PASS: Verification mode belongs to task execution; checks only parse and validate declared outputs.
Evidence: `packages/cli/src/workflow/check-stage-runner.ts:301-303`, `packages/cli/src/workflow/checks/review-passed-check.ts:33-83`.
- FAIL: Custom workflow support is expressed through generic task/check/reaction/result contracts and built-in implementations; this issue does not require YAML workflow definition support.
Deviation: the implementation still hard-codes review-specific convergence flow in shared runtime services, so custom non-review structured workflows cannot reuse the full mechanism.
Evidence: `packages/cli/src/services/stage-state-service.ts:456-461`, `packages/cli/src/workflow/config-driven-stage-runner.ts:200-258`.
- PASS: Existing #186 review history behavior and #204 reviewed-SHA binding remain compatible and are not replaced by this issue.
Evidence: reviewed snapshot preservation remains in `packages/cli/src/workflow/checks/review-passed-check.ts:52-83` and convergence writes are additive in `packages/cli/src/workflow/check-stage-runner.ts:573-605`.

## Tests

- PASS: `npm test -- convergence-recheck reaction-structured-context convergence-state-api`
- WARNING: I did not run the frontend `workflow-convergence-panel` test file in this pass.
- WARNING: No automated test currently covers the resolved/pre-existing/out-of-scope blocker-count projection bug in `StageStateService`.

<promise>FAIL</promise>
