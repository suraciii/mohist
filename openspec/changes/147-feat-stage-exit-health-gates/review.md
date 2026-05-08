# Review Report

## Result: FAIL

## Dimensions

### Correctness: PASS
- FIXED: `packages/cli/src/api/issues.ts:2384` to `packages/cli/src/api/issues.ts:2396` now fails closed with `500` when `postMergeFinalizer` is absent, and only returns success after `postMergeFinalizer.finalize(issue)` succeeds. The direct merge API no longer has an inline `stage=done` / `status=completed` / `mergeState=merged` fallback.
- FIXED: `packages/cli/src/git/merge-queue.ts:327` to `packages/cli/src/git/merge-queue.ts:336` now routes missing `deps.postMergeFinalizer` and failed finalization through `handleFailure()` before completion. The queue no longer emits `merge_completed` or marks the entry merged on the missing-finalizer path.
- FIXED: `packages/cli/tests/api-routes.test.ts:926` to `packages/cli/tests/api-routes.test.ts:945` now verifies that direct merge without `postMergeFinalizer` returns `500` and leaves the issue out of `done/completed/merged`.
- FIXED: `packages/cli/tests/merge-queue.test.ts:222` to `packages/cli/tests/merge-queue.test.ts:251` now verifies that merge queue without `postMergeFinalizer` emits no completion event, emits one failure event, keeps the issue out of `done/completed`, and sets `mergeState=build-failed`.
- UNCHANGED PASS: `packages/cli/src/services/agent-runner-service.ts:1009` to `packages/cli/src/services/agent-runner-service.ts:1025` remains fail-closed for `Stage.Check + MergeState.Merged` recovery when `postMergeFinalizer` is absent.
- UNCHANGED PASS: No `new BuildTestCheck` or `new CodeCompilesCheck` call sites remain under `packages/cli/src`; legacy references are limited to compatibility/export/wrapper code.

### Complexity: PASS
- FIXED: Completion is again centralized through `PostMergeFinalizer.completeIssue()` for normal successful direct merge and merge queue paths. The previous duplicated inline state transition to `done/completed/merged` has been removed from both `packages/cli/src/api/issues.ts` and `packages/cli/src/git/merge-queue.ts`.
- UNCHANGED PASS: Check-stage ordering remains clear: `health:check` is a pre-task check, while AI review and user approval remain post-task checks.

### Test Coverage: FAIL
- Targeted tests passed: `npm test -- --run tests/workflow-loader-health-gates.test.ts tests/workflow/health-gate-check.test.ts tests/workflow/stage-exit-health-gate-regression.test.ts tests/merge-queue.test.ts tests/api-routes.test.ts tests/check-stage-ordering.test.ts` with 6 files and 161 tests passing.
- Build passed: `npm run build`.
- FULL SUITE FAILURE: `npm test` fails in `packages/cli/tests/archive-lifecycle-regression.test.ts:127` because the test still expects merge queue success without `postMergeFinalizer`, but the corrected implementation now fails closed with `mergeState=build-failed`.
- FULL SUITE FAILURE: `npm test` fails in `packages/cli/tests/archive-lifecycle-regression.test.ts:191` because the test still expects direct merge success without `postMergeFinalizer`, but the corrected implementation now returns `500`.
- REGRESSION: The auto-fix updated the focused merge/API tests but missed stale archive lifecycle regression tests that still encode the old inline-completion behavior. Because build/test failures count as unresolved, this review remains failing despite the production code fixes.

### Security: PASS
- No new secret exposure or unsafe persistence path was found.
- Health gates still execute trusted workflow-configured commands with `shell: true`, consistent with the existing local workflow capability.
- `npm run build` still reports existing web dependency audit findings: 1 moderate and 1 high vulnerability. These are not introduced by the reviewed auto-fixes but remain a dependency hygiene item.

### Spec Compliance: FAIL
- Workflow health gate policy: PASS. `WorkflowConfig` and `loadHealthGatePolicies` support defaults, per-stage overrides, disabled gates, auto-fix fields, max attempts, fallback reactions, and `checks.buildTest` fallback.
- `checks.buildTest` compatibility: PASS. Existing `checks.buildTest` configuration maps to the check-stage health gate when `healthGates.check` is absent, and explicit `healthGates.check` takes precedence.
- Stage health gates before approval: PASS. Plan/build gate placement remains consistent with the specs, and default check-stage `health:check` runs before check-stage AI review generation and before `UserApprovalCheck`.
- Build completion includes build health gate: PASS. Build stage requires `AllTasksCompleteCheck` plus `health:build` before advancing to check.
- Check approval waits for full verification gate: PASS. Default check approval is only reachable after the pre-task `health:check` passes, AI review artifacts are generated, and `AiReviewCheck` passes.
- Health gate failure visibility in stage execution: PASS. `HealthGateCheck` and `PostMergeFinalizer` record structured health-gate check results with command, timeout, duration, enabled status, summary, and log excerpt.
- Post-merge health gate before done: PASS. Direct merge and merge queue paths no longer complete without `PostMergeFinalizer`, and `PostMergeFinalizer.completeIssue()` only runs after a disabled policy is recorded or an enabled post-merge gate passes.
- Direct merge respects final health gates: PASS. `POST /api/issues/:number/merge` now fails closed when finalization is unavailable and returns `422` with `healthGateResult` when post-merge verification fails.
- API health gate visibility: PASS. Direct merge responses include `healthGateResult` on finalizer success and finalizer failure; the missing-finalizer path no longer marks the issue complete with an undefined result.
- Verification requirement: FAIL. The full package test suite does not pass because stale archive lifecycle regression tests still assert behavior that violates the new final health gate contract.

## Fix Suggestions
1. `packages/cli/tests/archive-lifecycle-regression.test.ts:111` Provide a stub `postMergeFinalizer` that marks the issue merged/done for the worktree-retention success-path test, or change the test to expect fail-closed behavior when no finalizer is configured.
2. `packages/cli/tests/archive-lifecycle-regression.test.ts:165` Pass a stub successful `postMergeFinalizer` into `createIssueRoutes()` for the manual merge success-path worktree-retention test, or update the test to assert `500` and unchanged issue completion state when finalization is absent.
3. `packages/cli/tests/archive-lifecycle-regression.test.ts:181` Rename or split the manual merge test so archive lifecycle coverage does not depend on the now-invalid legacy assumption that direct merge can complete without post-merge finalization.
