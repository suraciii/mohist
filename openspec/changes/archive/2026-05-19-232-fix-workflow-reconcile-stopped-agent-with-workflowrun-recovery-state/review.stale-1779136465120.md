# Review

## Findings

- Error: `workflowRecoverySummary()` still reports `running` when the current work item has no live running latest attempt, which violates the new recovery-summary contract. In `packages/cli/src/workflow/domain/index.ts:1625-1637`, the summary falls through to `return 'running'` whenever there is no failed/interrupted attempt, even if the current work item is a fresh pending repair task with `latestAttempt === null`. The new regression at `packages/cli/tests/workflow-application-service.test.ts:805-844` explicitly creates that shape (`self-review-passed` failed, next work is `fix-plan-review`, recovery latestAttemptState is `null`), so the aggregate would still summarize that run as `running`, contradicting `specs/workflow-run/spec.md:60-64` and the acceptance criterion “WorkflowRun state does not remain running when the current work item's latest attempt is no longer running.” Suggested fix: change `workflowRecoverySummary()` to derive from `nextWork()`/current work item state instead of defaulting to `running`, and add a domain test for the repair-task-pending case.
- Error: `resume-pipeline` recovery routing still uses the old unreconciled `canRetryStage()` predicate instead of the reconciled recovery projection. In `packages/cli/src/services/agent-runner-service.ts:1197-1205`, blocked issues are allowed through if `workflowRunService.canRetryStage(issue.id, issue.stage)` returns true; `packages/cli/src/services/workflow-run-service.ts:34-38` simply loads the latest aggregate and calls `run.canRetryStage(stage)` with no reconciliation. That violates `specs/workflow-engine/spec.md:61-64`, which requires workflow resume code to use the reconciled latest attempt state before exposing or accepting recovery actions. It also reintroduces a stale-state split-brain path outside the API/UI projection. Suggested fix: replace this branch with `WorkflowApplicationService.checkRetryAvailability()` or `getRecoveryProjection()` using the issue tasks path, and add a regression around `AgentRunnerService.executeResumePipelineTask()` for a stale-running/latest-interrupted blocked issue.

## Correctness

- PASS: Work-item attempts are modeled on tasks/checks with explicit `running|completed|failed|interrupted` state and synced transitions. Evidence: `packages/cli/src/workflow/domain/index.ts:329-435`, `489-579`, `packages/cli/src/db/workflow-run-repo.ts:525-714`.
- PASS: Reconciliation interrupts stale running attempts instead of treating them as failed results. Evidence: `packages/cli/src/services/attempt-reconciliation-service.ts:96-155`, `packages/cli/src/services/workflow-application-service.ts:98-139`.
- PASS: Retry is gated by reconciled latest attempt state, with interrupted/running producing explicit rejection reasons. Evidence: `packages/cli/src/services/workflow-application-service.ts:305-328`, `390-419`, `packages/cli/src/api/issues.ts:4028-4052`.

## Complexity

- PASS: Most new logic is factored into focused helpers (`AttemptReconciliationService`, `getRecoveryProjection`, `computeAllowedActions`).
- Warning: `packages/cli/src/api/issues.ts` remains very large, which makes it easier for old and new recovery paths to diverge.

## Security

- PASS: No new injection or secret-handling issues found in the reviewed changes.

## Test Coverage

- Warning: There is good targeted coverage for API/UI/CLI projection behavior, but the failing summary invariant above is currently untested at the domain level, and the `resume-pipeline` stale-state routing path is not covered by a reconciliation-specific regression.

## Spec Compliance

### Acceptance Criteria

- PASS: Stopping or losing an agent moves the current work item's latest attempt out of `Running`.
  Evidence: `packages/cli/src/workflow/domain/index.ts:1407-1425`, `1848-1857`; tests `packages/cli/tests/recovery-routing-regression.test.ts:428-463`, `packages/cli/tests/workflow-application-service.test.ts:744-803`.
- PASS: Interrupted attempts are not treated as failed attempts.
  Evidence: `packages/cli/src/services/workflow-application-service.ts:313-318`; `packages/cli/web/src/components/IssueDetailPage.tsx:823-826`.
- PASS: `Retry` is enabled only for a `Failed` latest attempt.
  Evidence: `packages/cli/src/services/workflow-application-service.ts:320-328`, `263-271`; `packages/cli/web/src/components/IssueDetailPage.tsx:809-887`.
- PASS: A `Running` latest attempt requires live execution evidence; if that evidence is missing, Mohist reconciles the attempt before showing recovery actions.
  Evidence: `packages/cli/src/services/attempt-reconciliation-service.ts:131-155`, `packages/cli/src/services/workflow-application-service.ts:148-181`; tests `packages/cli/tests/workflow-application-service.test.ts:616-742`.
- FAIL: WorkflowRun state does not remain `running` when the current work item's latest attempt is no longer running.
  Deviation: `packages/cli/src/workflow/domain/index.ts:1637` still returns `running` for the pending-repair/current-work-item-without-attempt case shown in `packages/cli/tests/workflow-application-service.test.ts:805-844`, which conflicts with `specs/workflow-run/spec.md:60-64`.
- PASS: The issue detail page no longer renders `Retry` solely because `issue.status === blocked`.
  Evidence: `packages/cli/web/src/components/IssueDetailPage.tsx:809-887`; tests `packages/cli/web/src/components/IssueDetailPage.test.tsx:419-542`.
- PASS: UI, CLI, and API agree on the recovery action for the latest attempt state.
  Evidence: shared projection in `packages/cli/src/services/workflow-application-service.ts:156-180`; CLI rendering in `packages/cli/src/cli/commands/issue.ts:181-233`; tests `packages/cli/tests/cli-commands.test.ts:119-230`, `packages/cli/tests/recovery-routing-regression.test.ts:417-425`.
- PASS: Regression coverage reproduces the #229 shape: blocked issue, running WorkflowRun, no running queue task, stale running coder session, and no valid `Retry` action.
  Evidence: `packages/cli/tests/recovery-routing-regression.test.ts:428-463`.
- PASS: Regression coverage proves that a genuine `Failed` task/check attempt still exposes `Retry` and can retry failed work.
  Evidence: `packages/cli/tests/workflow-application-service.test.ts:521-551`, `805-844`; `packages/cli/src/workflow/domain/index.ts:1428-1519`.

## Suggested Fixes

- `packages/cli/src/workflow/domain/index.ts:1615-1637`: derive `workflowRecoverySummary()` from the current work item / `nextWork()` result, and treat `latestAttempt === null` on the current pending work item as non-running.
- `packages/cli/src/services/agent-runner-service.ts:1197-1205`: replace direct `workflowRunService.canRetryStage()` gating with `WorkflowApplicationService.checkRetryAvailability()` or a recovery projection lookup so `resume-pipeline` uses reconciled latest-attempt state.
- `packages/cli/tests/workflow-run-domain.test.ts`: add a regression asserting that a repair-task-pending current work item produces `waiting-for-recovery`, not `running`.
- `packages/cli/tests/agent-runner-service.test.ts` or `packages/cli/tests/recover-issues.test.ts`: add a stale-running blocked issue case proving `resume-pipeline` consults reconciled recovery instead of raw `canRetryStage()`.

<promise>FAIL</promise>
