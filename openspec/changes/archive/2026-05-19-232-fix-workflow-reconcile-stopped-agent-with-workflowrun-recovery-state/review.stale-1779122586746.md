# Review

## Findings

1. Error: Interrupted recovery advertises `Resume`, but the API still rejects the exact blocked-issue shape this change is supposed to fix. The UI now renders a `Resume` button whenever `issue.status === blocked` and `issue.recovery.allowedActions` contains `resume` (`packages/cli/web/src/components/IssueDetailPage.tsx:783-868`), but `POST /api/issues/:number/resume` still hard-rejects anything except `paused` or `interrupted` issue status (`packages/cli/src/api/issues.ts:1904-1908`). Existing route coverage explicitly locks that behavior in (`packages/cli/tests/recovery-routing-regression.test.ts:276-285`). This violates the spec requirement that interrupted recovery semantics be usable via the recovery APIs and gives the user another contradictory action surface.
Fix suggestion: `packages/cli/src/api/issues.ts:1904-1918` and `packages/cli/src/services/issue-service.ts:133-138` should accept blocked issues when the reconciled recovery projection reports latest attempt state `interrupted`, instead of keying resume eligibility only off `issue.status`.

2. Error: Reconciliation proves liveness at issue scope, not attempt scope, so a stale current attempt can remain `running` if any unrelated coder session for the same issue is alive. `AttemptReconciliationService.reconcileRunningAttempts` only checks `hasActiveQueueTask(issueId)` and `hasLiveCoderSession(issueId)` (`packages/cli/src/services/attempt-reconciliation-service.ts:31-45,90-127`). Although an attempt-scoped lookup helper exists (`findRunningCoderSessionsByAttemptEvidence` at `:58-75`), it is never used. This violates the design/spec requirement that live evidence be related to the current attempt and can preserve the exact stale-running contradiction whenever an old or parallel session on the same issue is still running.
Fix suggestion: `packages/cli/src/services/attempt-reconciliation-service.ts:90-127` should evaluate each running attempt against its own evidence identifiers (`coderSessionId`, `acpSessionId`, `executionId`, `processPid`, `queueTaskId`) and only keep that attempt `running` when matching evidence is live.

3. Error: The new attempt model does not actually retain runtime evidence identifiers for normal task/check dispatch, so reconciliation usually cannot connect a running attempt to the executing agent. Both task and check attempts are started without evidence (`packages/cli/src/workflow/config-driven-stage-runner.ts:118-124,261-267`), even though the domain model and repository support `queueTaskId`, `acpSessionId`, `coderSessionId`, `executionId`, and `processPid` (`packages/cli/src/workflow/domain/index.ts:360-376,499-515`). Because those fields stay null for ordinary executions, the implementation falls back to coarse issue-level session checks instead of attempt-specific reconciliation, leaving the coder-session tracking spec only partially implemented.
Fix suggestion: thread the available runtime identifiers from the task/check handlers into `startTaskAttempt` / `startCheckAttempt`, or add an immediate follow-up update when the agent session is created so the latest attempt stores concrete evidence.

## Correctness

- FAIL: See findings 1-3.

## Complexity

- PASS with warning: most touched functions are small, but `WorkflowApplicationService` is accumulating multiple responsibilities (`packages/cli/src/services/workflow-application-service.ts:98-316`). No single new function is obviously over the requested threshold, but the service is becoming a coordination hotspot.

## Test Coverage

- FAIL: targeted tests pass for the implemented behavior (`npm test -- --run tests/recovery-routing-regression.test.ts tests/workflow-application-service.test.ts`), but they currently reinforce the broken resume semantics for blocked/interrupted recovery (`packages/cli/tests/recovery-routing-regression.test.ts:276-285`) and do not cover attempt-scoped liveness disambiguation.

## Security

- PASS: no obvious injection or secret-handling issues found in the touched code paths.

## Spec Compliance

- Acceptance criterion: Stopping or losing an agent moves the current work item's latest attempt out of `Running`.
FAIL. There is interruption logic (`packages/cli/src/services/workflow-application-service.ts:98-140`), but it relies on issue-wide evidence checks (`packages/cli/src/services/attempt-reconciliation-service.ts:31-45,90-127`) and can leave a stale current attempt running when any unrelated live session exists for the same issue.

- Acceptance criterion: Interrupted attempts are not treated as failed attempts.
PASS. Interrupted attempts are separate in the domain (`packages/cli/src/workflow/domain/index.ts:412-421,546-555`), retry rejects interrupted work explicitly (`packages/cli/src/services/workflow-application-service.ts:278-295`), and the UI labels interruption distinctly (`packages/cli/web/src/components/IssueDetailPage.tsx:800-803`).

- Acceptance criterion: `Retry` is enabled only for a Failed latest attempt.
PASS. UI retry rendering now depends on `allowedActions.includes('retry')` (`packages/cli/web/src/components/IssueDetailPage.tsx:787,835-842`), and retry availability is keyed to failed latest attempts before fallback legacy status checks (`packages/cli/src/services/workflow-application-service.ts:297-315`).

- Acceptance criterion: A `Running` latest attempt requires live execution evidence; if that evidence is missing, Mohist reconciles the attempt before showing recovery actions.
FAIL. Reconciliation runs before projection/retry decisions (`packages/cli/src/services/workflow-application-service.ts:143-175,228-229`), but the evidence test is too broad because any live coder session for the issue keeps the attempt running (`packages/cli/src/services/attempt-reconciliation-service.ts:38-45,108-111`).

- Acceptance criterion: WorkflowRun state does not remain `running` when the current work item's latest attempt is no longer running.
PASS with warning. The derived recovery summary becomes `waiting-for-recovery` when interrupted/failure evidence is present (`packages/cli/src/workflow/domain/index.ts:1604-1639`), but this still depends on successful reconciliation, which is weakened by finding 2.

- Acceptance criterion: The issue detail page no longer renders `Retry` solely because `issue.status === blocked`.
PASS. Blocked UI actions now derive from `issue.recovery.allowedActions` rather than blocked status alone (`packages/cli/web/src/components/IssueDetailPage.tsx:783-868`).

- Acceptance criterion: UI, CLI, and API agree on the recovery action for the latest attempt state.
FAIL. The blocked/interrupted UI can offer `Resume` (`packages/cli/web/src/components/IssueDetailPage.tsx:844-851`) while the API rejects blocked resume requests (`packages/cli/src/api/issues.ts:1904-1908`), and current tests confirm that disagreement (`packages/cli/tests/recovery-routing-regression.test.ts:276-285`).

- Acceptance criterion: Regression coverage reproduces the #229 shape: blocked issue, running WorkflowRun, no running queue task, stale running coder session, and no valid Retry action.
PASS with gap. Retry conflict coverage exists and the targeted tests pass, but I did not find coverage proving attempt-specific liveness discrimination when another unrelated session on the same issue is live.

- Acceptance criterion: Regression coverage proves that a genuine Failed task/check attempt still exposes `Retry` and can retry failed work.
PASS. `WorkflowApplicationService.checkRetryAvailability` tests cover failed task/check availability (`packages/cli/tests/workflow-application-service.test.ts:472-546`), and the retry route path is exercised in recovery regression tests.

Overall: FAIL

<promise>FAIL</promise>
