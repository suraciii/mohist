# Review

## Verdict

Overall: FAIL

## Findings

1. High: interrupted workflow recovery is still routed through failed-stage retry semantics
File: `packages/cli/src/workflow/domain/index.ts:1517-1524`, `packages/cli/src/workflow/domain/index.ts:1830-1839`, `packages/cli/src/services/workflow-run-service.ts:34-37`, `packages/cli/src/workflow/workflow-engine.ts:256-262`

`markWaitingForRecovery()` persists interrupted work by setting both `stageRun.status` and `WorkflowRun.status` to `failed` (`index.ts:1830-1839`). `canRetryStage()` then returns `true` purely from those failed run/stage flags (`index.ts:1517-1524`), and `WorkflowRunService.canRetryStage()` forwards that unchanged (`workflow-run-service.ts:34-37`). In `WorkflowEngine.runAggregateWorkflow()`, any non-backlog issue with `canRetryStage(...) === true` is immediately resumed via `service.retryStage(...)` instead of `resumeDecision(...)` (`workflow-engine.ts:256-262`).

That means an interrupted latest attempt is still treated as failed work once `resume-pipeline` runs, which violates the spec's requirement that interrupted work remain distinct from failed retryable work and that resume use interrupted recovery semantics rather than failed-run retry semantics.

Suggested fix:
- In `packages/cli/src/workflow/domain/index.ts`, stop making interruption satisfy `canRetryStage()`. Either keep interrupted recovery out of `status === 'failed'`, or tighten `canRetryStage()` so it requires a reconciled latest current work attempt with `state === 'failed'`.
- In `packages/cli/src/workflow/workflow-engine.ts`, do not choose the retry path from raw `canRetryStage()` alone. Base that decision on the reconciled recovery projection / latest attempt state, so interrupted work goes through `resumeDecision()`.

## Correctness

- FAIL due to the finding above.

## Complexity

- PASS with warning: the reviewed recovery logic is spread across domain, service, API, projection, and engine layers, which makes semantic drift easier. The main problem found is semantic coupling, not raw function length.

## Test Coverage

- PASS with gap: focused tests pass, but they do not catch the `resume-pipeline` engine path reclassifying interrupted work as retryable failed work.
- Verified by running:
  - `npm test -- --run tests/recovery-routing-regression.test.ts tests/cli-commands.test.ts tests/workflow-run-domain.test.ts`

## Security

- PASS. No obvious injection, auth, or secret-handling regressions were found in the reviewed paths.

## Spec Compliance

1. PASS: Stopping or losing an agent moves the current work item's latest attempt out of `Running`.
Evidence: `packages/cli/src/services/workflow-application-service.ts:131-138`, `packages/cli/src/workflow/domain/index.ts:1378-1400`, `packages/cli/src/workflow/domain/index.ts:1403-1421`, `packages/cli/tests/recovery-routing-regression.test.ts:428-464`.

2. FAIL: Interrupted attempts are not treated as failed attempts.
Deviation: interruption is persisted by `markWaitingForRecovery()` as failed stage/run state, and the engine later routes that state through retry handling.
Evidence: `packages/cli/src/workflow/domain/index.ts:1830-1839`, `packages/cli/src/workflow/domain/index.ts:1517-1524`, `packages/cli/src/workflow/workflow-engine.ts:256-262`.

3. PASS: `Retry` is enabled only for a failed latest attempt.
Evidence: `packages/cli/src/services/workflow-application-service.ts:300-323`, `packages/cli/web/src/components/IssueDetailPage.tsx:797-881`, `packages/cli/tests/recovery-routing-regression.test.ts:457-463`, `packages/cli/tests/cli-commands.test.ts:119-188`.

4. PASS: A `Running` latest attempt requires live execution evidence; if that evidence is missing, Mohist reconciles before showing recovery actions.
Evidence: `packages/cli/src/services/attempt-reconciliation-service.ts:131-155`, `packages/cli/src/services/workflow-application-service.ts:98-140`, `packages/cli/tests/recovery-routing-regression.test.ts:428-464`, `466-496`.

5. PASS with caveat: WorkflowRun state does not remain `running` when the current work item's latest attempt is no longer running.
Evidence: `packages/cli/src/workflow/domain/index.ts:1830-1839`, `packages/cli/src/workflow/domain/index.ts:1607-1629`.
Note: this passes narrowly because the run is moved out of `running`, but it is moved to `failed`, which causes the finding above.

6. PASS: The issue detail page no longer renders `Retry` solely because `issue.status === blocked`.
Evidence: `packages/cli/web/src/components/IssueDetailPage.tsx:797-881`, `packages/cli/web/src/components/IssueDetailPage.test.tsx:449-510`.

7. PASS: UI, CLI, and API agree on the recovery action for the latest attempt state.
Evidence: shared projection usage in `packages/cli/src/api/issues.ts:743-753`, CLI rendering in `packages/cli/src/cli/commands/issue.ts:181-232`, UI action gating in `packages/cli/web/src/components/IssueDetailPage.tsx:797-881`, tests in `packages/cli/tests/cli-commands.test.ts:119-230` and `packages/cli/tests/recovery-routing-regression.test.ts:398-464`.

8. PASS: Regression coverage reproduces the #229 shape.
Evidence: `packages/cli/tests/recovery-routing-regression.test.ts:428-464` covers blocked issue, stale running evidence, interrupted reconciliation, and no valid retry.

9. PASS: Regression coverage proves that a genuine failed task/check attempt still exposes `Retry` and can retry failed work.
Evidence: `packages/cli/tests/recovery-routing-regression.test.ts:603-715` and related retry-path assertions in `packages/cli/tests/workflow-run-domain.test.ts:1291-1343`.

## Notes

- Focused tests currently pass despite the finding, so this is a behavioral hole rather than a red test.

<promise>FAIL</promise>
