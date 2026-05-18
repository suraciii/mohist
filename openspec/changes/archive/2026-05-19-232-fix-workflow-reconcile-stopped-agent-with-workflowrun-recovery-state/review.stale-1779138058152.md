# Review

## Findings

1. Error: `POST /api/issues/:number/rerun` ignores the recovery projection and accepts rerun even when the latest attempt is still `running`.
File: `packages/cli/src/api/issues.ts:4102-4181`
Why it matters: the spec requires recovery-sensitive paths, including rerun, to reconcile the latest running attempt before exposing or accepting actions, and running work should guide users to wait or stop rather than rerun. This handler only checks stage/backlog/done, then clears state and calls `workflowApplicationService.rerunStage(...)` without consulting `getRecoveryProjection()` or `allowedActions`. That means a client can bypass the attempt-derived action gate and rerun actively running work.
Suggested fix: before mutating state in the rerun handler, call `getRecoveryProjection(issue.id, { tasksPath })` or a dedicated rerun-availability method that reconciles first, then reject with 409 unless `allowedActions` includes `rerun`.

## Correctness

- FAIL: rerun API does not enforce attempt-derived recovery gating for running work. Evidence: `packages/cli/src/api/issues.ts:4102-4181`.

## Complexity

- PASS with warning: the new logic is spread across several services, but the reviewed functions are mostly bounded. Main concern is behavioral branching across API/service/projection layers rather than raw function length.

## Test Coverage

- PASS with gap: targeted tests passed, but there is no regression covering `POST /api/issues/:number/rerun` rejection while the reconciled latest attempt is still `running`.
- Verified with `npx vitest run tests/workflow-application-service.test.ts tests/recovery-routing-regression.test.ts tests/cli-commands.test.ts tests/workflowrun-e2e.test.ts tests/workflow-run-domain.test.ts`.

## Security

- PASS: no obvious injection or secret-handling issues found in the reviewed change paths.

## Spec Compliance

- PASS: Stopping or losing an agent moves the current work item's latest attempt out of `Running`.
Evidence: `packages/cli/src/workflow/domain/index.ts:425-435,568-578`, `packages/cli/src/services/workflow-application-service.ts:129-136`, tests in `packages/cli/tests/workflow-application-service.test.ts:744-802`.

- PASS: Interrupted attempts are not treated as failed attempts.
Evidence: `packages/cli/src/workflow/domain/index.ts:425-435,568-578`, `packages/cli/tests/workflow-run-domain.test.ts:1365-1388`.

- PASS: `Retry` is enabled only for a failed latest attempt.
Evidence: `packages/cli/src/services/workflow-application-service.ts:305-328`, UI gating in `packages/cli/web/src/components/IssueDetailPage.tsx:808-893`, CLI rendering in `packages/cli/src/cli/commands/issue.ts:181-225`.

- PASS: A `Running` latest attempt requires live execution evidence; missing evidence triggers reconciliation before recovery display.
Evidence: `packages/cli/src/services/attempt-reconciliation-service.ts:131-152`, `packages/cli/src/services/workflow-application-service.ts:98-139,156-181,274-329`, tests in `packages/cli/tests/recovery-routing-regression.test.ts:428-495`.

- PASS: WorkflowRun state does not remain `running` when the current work item's latest attempt is no longer running.
Evidence: `packages/cli/src/workflow/domain/index.ts:1615-1641`, `packages/cli/tests/workflowrun-e2e.test.ts:184-208`.

- PASS: The issue detail page no longer renders `Retry` solely because `issue.status === blocked`.
Evidence: `packages/cli/web/src/components/IssueDetailPage.tsx:808-893`, tests in `packages/cli/web/src/components/IssueDetailPage.test.tsx:419-542`.

- PASS: UI, CLI, and API agree on the recovery action for the latest attempt state.
Evidence: API projection from `packages/cli/src/services/workflow-application-service.ts:156-181`; CLI rendering in `packages/cli/src/cli/commands/issue.ts:181-225` with tests `packages/cli/tests/cli-commands.test.ts:119-230`; UI rendering in `packages/cli/web/src/components/IssueDetailPage.tsx:745-905`.

- PASS: Regression coverage reproduces the #229 shape and removes invalid Retry.
Evidence: `packages/cli/tests/recovery-routing-regression.test.ts:428-463`.

- PASS: Regression coverage proves genuine failed work still exposes Retry.
Evidence: failed attempt handling in `packages/cli/src/services/workflow-application-service.ts:320-328,390-419`; tests include failed-path coverage in `packages/cli/tests/cli-commands.test.ts:127-131` and workflow service/domain suites.

- FAIL: Reconciliation is not invoked on all recovery paths before accepting actions.
Deviation: the rerun API accepts rerun without checking the reconciled recovery projection or allowed actions.
Evidence: `packages/cli/src/api/issues.ts:4102-4181`.

<promise>FAIL</promise>
