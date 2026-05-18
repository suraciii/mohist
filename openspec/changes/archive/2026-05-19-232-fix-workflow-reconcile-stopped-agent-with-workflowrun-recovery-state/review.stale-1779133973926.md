# Review

## Findings

1. Error: recovery reconciliation is skipped unless the persisted `WorkflowRun` is already `running`, so stale `latestAttempt.state === 'running'` can survive on non-running runs and still drive recovery reads. `WorkflowApplicationService.reconcileIssueWorkflow()` only loads `loadRunningAggregate()`/`loadActiveAggregate()` and returns early when `snapshot().status !== 'running'` (`packages/cli/src/services/workflow-application-service.ts:98-104`). Every recovery-sensitive read and action path here (`getRecoveryProjection`, `getWorkflowRecoverySummary`, `checkRetryAvailability`, `retryStageOrReject`, `rerunStage`, `resumeDecision`) depends on that reconciliation entrypoint (`packages/cli/src/services/workflow-application-service.ts:143-152`, `269-270`, `385-387`, `420-421`, `533-535`). This leaves an inconsistent but realistic persisted shape unhealed: run status `failed` plus current work latest attempt `running` with no live evidence. The spec requires reconciling the latest running attempt before exposing or accepting recovery actions, not only when the run row still says `running`. Suggested fix: in `packages/cli/src/services/workflow-application-service.ts:98-140`, load the latest aggregate when present, inspect the current stage for running latest attempts regardless of run status, and interrupt stale attempts before computing projections or recovery decisions.

## Correctness

- FAIL: stale running attempts are only reconciled for runs whose persisted status is `running` (`packages/cli/src/services/workflow-application-service.ts:98-104`).

## Complexity

- PASS: reviewed touched logic stays reasonably localized; no new obviously oversized functions were introduced in the changed files.

## Test Coverage

- PASS with gap: targeted tests pass for the exercised paths, including `tests/workflow-run-domain.test.ts`, `tests/recovery-routing-regression.test.ts`, `tests/workflow-application-service.test.ts`, `tests/workflow-engine-aggregate.test.ts`, `tests/recovery-verb-regression.test.ts`, and `tests/cli-commands.test.ts` via `npx vitest run tests/workflow-application-service.test.ts tests/recovery-routing-regression.test.ts tests/recovery-verb-regression.test.ts tests/cli-commands.test.ts tests/workflow-run-domain.test.ts tests/workflow-engine-aggregate.test.ts`.
- Gap: no regression test covers a latest run with `status='failed'` and a stale `latestAttempt.state='running'`, which is the path currently missed by reconciliation.

## Security

- PASS: no new input-handling or injection issue was evident in the reviewed implementation.

## Acceptance Criteria

- PASS: Stopping or losing an agent moves the current work item's latest attempt out of `Running` in covered running-run paths via `interruptRunningWorkAttempts()` and reconciliation (`packages/cli/src/workflow/domain/index.ts:1403-1421`, `packages/cli/src/services/workflow-application-service.ts:131-138`), with regression coverage in `packages/cli/tests/workflowrun-e2e.test.ts:184-207` and `packages/cli/tests/recovery-routing-regression.test.ts:302-308`.
- PASS: Interrupted attempts are not treated as failed attempts; task/check interruption sets `latestAttempt.state = 'interrupted'` and resets work progress to pending (`packages/cli/src/workflow/domain/index.ts:423-434`, `566-577`).
- PASS: `Retry` is enabled only for a failed latest attempt in the shared recovery projection path (`packages/cli/src/services/workflow-application-service.ts:258-266`, `300-317`) and Issue Detail consumes `allowedActions` instead of blocked-status heuristics (`packages/cli/web/src/components/IssueDetailPage.tsx:797-887`).
- FAIL: A `Running` latest attempt does not always reconcile before showing recovery actions. Reconciliation is skipped when the latest aggregate is not persisted as `running` (`packages/cli/src/services/workflow-application-service.ts:98-104`), so stale `running` attempts on non-running runs can still leak through recovery-sensitive reads.
- PASS: WorkflowRun state does not remain user-projected as active running when the current work item's latest attempt is interrupted in the implemented path; `workflowRecoverySummary()` returns `waiting-for-recovery` for interrupted attempts (`packages/cli/src/workflow/domain/index.ts:1609-1631`).
- PASS: The issue detail page no longer renders `Retry` solely because `issue.status === blocked`; it keys off `allowedActions.includes('retry')` (`packages/cli/web/src/components/IssueDetailPage.tsx:797-887`).
- PASS: UI, CLI, and API agree on recovery action availability for the covered fixtures, with tests in `packages/cli/tests/cli-commands.test.ts:120-230`, `packages/cli/tests/recovery-routing-regression.test.ts:348-425`, and `packages/cli/web/src/components/IssueDetailPage.test.tsx:358-510`.
- PASS: Regression coverage reproduces the #229-style stale-running shape for the running-run case and rejects retry appropriately (`packages/cli/tests/recovery-routing-regression.test.ts:302-308`, `348-425`).
- PASS: Regression coverage proves genuine failed work still exposes retry paths (`packages/cli/tests/workflowrun-e2e.test.ts:344-381` and related retry tests).

<promise>FAIL</promise>
