# Review

## Findings

- No error-level findings in the current implementation.

## Correctness

- PASS: `POST /api/issues/:number/retry` now gates on reconciled latest-attempt state via `checkRetryAvailability(...)`, so retry is rejected for `running` and `interrupted` work and accepted only for `failed` work. Evidence: `packages/cli/src/api/issues.ts:4028-4041`, `packages/cli/src/services/workflow-application-service.ts:274-329`.
- PASS: `POST /api/issues/:number/rerun` now uses the reconciled recovery projection and rejects rerun when the latest attempt is still running. Evidence: `packages/cli/src/api/issues.ts:4115-4134`, `packages/cli/tests/api-routes.test.ts:1960-2016`.
- PASS: stale running attempts without live queue/process evidence reconcile to `interrupted` rather than remaining `running`. Evidence: `packages/cli/src/services/attempt-reconciliation-service.ts:131-152`, `packages/cli/tests/recovery-routing-regression.test.ts:428-496`, `packages/cli/tests/workflow-application-service.test.ts:744-803`.

## Complexity

- PASS with warning: the touched handlers remain readable, but `packages/cli/src/api/issues.ts` is still a very large route module overall. The new rerun gate itself is small and localized at `packages/cli/src/api/issues.ts:4115-4134`.

## Test Coverage

- PASS: focused regression coverage exists for stale-running reconciliation, interrupted retry rejection, CLI recovery rendering, UI recovery rendering, and rerun rejection while work is still live-running. Evidence: `packages/cli/tests/recovery-routing-regression.test.ts:428-496`, `packages/cli/tests/api-routes.test.ts:1960-2016`, `packages/cli/tests/cli-commands.test.ts:119-231`, `packages/cli/web/src/components/IssueDetailPage.test.tsx:419-542`.
- PASS: verified with `npx vitest run tests/api-routes.test.ts tests/recovery-routing-regression.test.ts tests/workflow-application-service.test.ts tests/cli-commands.test.ts web/src/components/IssueDetailPage.test.tsx`.

## Security

- PASS: no new injection, auth, or secret-handling issues found in the reviewed paths.

## Spec Compliance

1. PASS: Stopping or losing an agent moves the current work item's latest attempt out of `Running`.
Evidence: reconciliation interrupts stale attempts in `packages/cli/src/services/attempt-reconciliation-service.ts:109-128`; exercised by `packages/cli/tests/recovery-routing-regression.test.ts:428-496` and `packages/cli/tests/workflow-application-service.test.ts:737-742`.

2. PASS: Interrupted attempts are not treated as failed attempts.
Evidence: interrupted work is rejected by retry with interrupted-specific guidance in `packages/cli/src/services/workflow-application-service.ts:313-318`; domain retryability stays false in `packages/cli/tests/workflow-run-domain.test.ts:1365-1388`.

3. PASS: `Retry` is enabled only for a failed latest attempt.
Evidence: `packages/cli/src/services/workflow-application-service.ts:320-328`; API gate in `packages/cli/src/api/issues.ts:4030-4041`; UI actions derive from `allowedActions` in `packages/cli/web/src/components/IssueDetailPage.tsx:808-893`.

4. PASS: A `Running` latest attempt requires live execution evidence; if that evidence is missing, Mohist reconciles the attempt before showing recovery actions.
Evidence: live evidence check logic in `packages/cli/src/services/attempt-reconciliation-service.ts:131-152`; recovery projection reconciles first in `packages/cli/src/services/workflow-application-service.ts:156-180`; stale PID-less/session-loss regressions in `packages/cli/tests/recovery-routing-regression.test.ts:428-496`.

5. PASS: WorkflowRun state does not remain `running` when the current work item's latest attempt is no longer running.
Evidence: reconciled projection moves to waiting-for-recovery in `packages/cli/tests/workflow-application-service.test.ts:790-801`; recovery summary is asserted as `waiting-for-recovery` at `packages/cli/tests/recovery-routing-regression.test.ts:421-425`.

6. PASS: The issue detail page no longer renders `Retry` solely because `issue.status === blocked`.
Evidence: UI uses `recovery.allowedActions` in `packages/cli/web/src/components/IssueDetailPage.tsx:808-893`; blocked check-stage case without allowed retry renders no Retry in `packages/cli/web/src/components/IssueDetailPage.test.tsx:482-542`.

7. PASS: UI, CLI, and API agree on the recovery action for the latest attempt state.
Evidence: CLI renders API projection in `packages/cli/src/cli/commands/issue.ts:181-225`; CLI tests cover `running`, `failed`, and `interrupted` actions in `packages/cli/tests/cli-commands.test.ts:119-231`; API and queue projection assertions in `packages/cli/tests/recovery-routing-regression.test.ts:417-425`.

8. PASS: Regression coverage reproduces the #229 shape: blocked issue, running WorkflowRun, no running queue task, stale running coder session, and no valid Retry action.
Evidence: `packages/cli/tests/recovery-routing-regression.test.ts:428-463`.

9. PASS: Regression coverage proves that a genuine Failed task/check attempt still exposes `Retry` and can retry failed work.
Evidence: failed latest-attempt retry availability in `packages/cli/src/services/workflow-application-service.ts:320-328`; route/service coverage remains green in `tests/api-routes.test.ts` and `tests/workflow-application-service.test.ts` from the verified test run above.

<promise>PASS</promise>
