# Review

## Findings

1. High: PID-less `running` coder sessions are treated as live proof, so stale attempts can remain `running` forever without any actual liveness signal.
File: `packages/cli/src/services/attempt-reconciliation-service.ts:137-145`
Evidence: `isAttemptEvidenceLive()` returns `true` as soon as it finds a matching coder session with `status === 'running' || 'probing'` and `!session.processPid`.
Why this is a bug: the spec requires a `running` latest attempt to have live execution evidence, and missing evidence must reconcile to `interrupted`. A persisted session row with no PID and no queue task is historical state, not live proof. This directly violates the stale-evidence reconciliation rule and can reintroduce the core contradiction whenever agent execution records do not include a PID.
Corroborating test evidence: `packages/cli/tests/recovery-routing-regression.test.ts:466-495` codifies the same incorrect behavior by asserting that a PID-less stored session keeps the attempt `running` with `['wait', 'stop']`.
Suggested fix: in `packages/cli/src/services/attempt-reconciliation-service.ts:137-145`, stop treating PID-less `running`/`probing` rows as sufficient liveness by themselves. Require an actual bounded liveness signal, such as a live queue task, a live process check, or recent explicit probe/heartbeat evidence. Then update the regression at `packages/cli/tests/recovery-routing-regression.test.ts:466-495` to expect reconciliation to `interrupted` when no real liveness proof exists.

## Acceptance Criteria

1. FAIL: Stopping or losing an agent moves the current work item's latest attempt out of `Running`.
Evidence: explicit stop paths do interrupt attempts (`packages/cli/src/services/agent-runner-service.ts:273`, `packages/cli/src/services/workflow-application-service.ts:358-364`), but stale PID-less sessions are still treated as live and remain `running` (`packages/cli/src/services/attempt-reconciliation-service.ts:137-145`).

2. PASS: Interrupted attempts are not treated as failed attempts.
Evidence: retry is blocked for interrupted latest attempts with a dedicated reason/message (`packages/cli/src/services/workflow-application-service.ts:313-318`), and interrupted task/check state remains incomplete (`packages/cli/src/workflow/domain/index.ts:425-435`, `568-578`; `packages/cli/tests/workflow-run-domain.test.ts:310-324`).

3. PASS: `Retry` is enabled only for a failed latest attempt.
Evidence: `checkRetryAvailability()` returns available only for `attemptState === 'failed'` (`packages/cli/src/services/workflow-application-service.ts:305-328`), retry API enforces it before executing (`packages/cli/src/api/issues.ts:4030-4041`), and UI gates Retry off `recovery.allowedActions` (`packages/cli/web/src/components/IssueDetailPage.tsx:809-891`).

4. FAIL: A `Running` latest attempt requires live execution evidence; if that evidence is missing, Mohist reconciles the attempt before showing recovery actions.
Evidence: reconciliation does run before projection/retry decisions (`packages/cli/src/services/workflow-application-service.ts:148-180`, `274-329`), but `packages/cli/src/services/attempt-reconciliation-service.ts:139-141` treats a PID-less stored session as live evidence with no actual liveness check.

5. PASS: WorkflowRun state does not remain `running` when the current work item's latest attempt is no longer running.
Evidence: interrupted reconciliation marks the run/stage into waiting-for-recovery projection (`packages/cli/src/workflow/domain/index.ts:1625-1641`, `1865-1875`), and regression coverage verifies reconciled interrupted runs no longer project active running (`packages/cli/tests/workflow-application-service.test.ts:744-803`).

6. PASS: The issue detail page no longer renders `Retry` solely because `issue.status === blocked`.
Evidence: Issue Detail derives action buttons from `recovery.allowedActions` (`packages/cli/web/src/components/IssueDetailPage.tsx:809-891`), and tests verify blocked issues without recovery retry do not render Retry (`packages/cli/web/src/components/IssueDetailPage.test.tsx:482-543`).

7. PASS: UI, CLI, and API agree on the recovery action for the latest attempt state.
Evidence: API exposes a shared recovery projection (`packages/cli/src/api/issues.ts:745-755`, `1257-1324`, `1348-1415`, `3791-3838`), CLI renders that projection (`packages/cli/src/cli/commands/issue.ts:181-225`), and tests cover CLI/API/UI action agreement (`packages/cli/tests/cli-commands.test.ts:119-231`, `packages/cli/web/src/components/IssueDetailPage.test.tsx:392-543`).

8. PASS: Regression coverage reproduces the #229 shape: blocked issue, running WorkflowRun, no running queue task, stale running coder session, and no valid Retry action.
Evidence: `packages/cli/tests/recovery-routing-regression.test.ts:428-464` exercises the stale-running blocked Build case and asserts reconciliation to `interrupted` with no retry.

9. PASS: Regression coverage proves that a genuine Failed task/check attempt still exposes `Retry` and can retry failed work.
Evidence: failed task/check retry availability is covered in `packages/cli/tests/workflow-application-service.test.ts:478-519`, and retry API path remains covered by route regressions (`packages/cli/tests/recovery-routing-regression.test.ts:587+`).

## Quality Notes

- Correctness: FAIL because stale PID-less session rows can block required reconciliation.
- Complexity: generally acceptable in the touched paths; no single review finding here.
- Test coverage: focused regressions pass. Verified with `npm test -- workflow-application-service.test.ts recovery-routing-regression.test.ts cli-commands.test.ts IssueDetailPage.test.ts`.
- Security: no specific security issues found in the reviewed change set.

<promise>FAIL</promise>
