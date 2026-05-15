## Findings

1. High: Rejected Plan retries can reuse existing artifacts and skip creating a new attempt/session
File: `packages/cli/src/workflow/plan-stage-runner.ts:162-170`
The Plan retry path only clears the checkpoint in `POST /issues/:number/reject` (`packages/cli/src/api/issues.ts:1792-1795`), but `PlanStageRunner.executeSingleArtifactTask()` still treats an already-existing artifact as completed and immediately re-marks the step complete without invoking `AgentSession.create()` or `session.execute()`. On a rejected retry where `proposal.md`, `design.md`, `tasks.json`, `specs/`, and `self-review.md` already exist, the runner can complete all Plan tasks by file existence alone. That violates the required behavior that a rejected Plan retry starts a new observable session/attempt, regenerates artifacts with the rejection feedback, and requests approval again instead of reusing the old attempt.
Suggested fix: in `packages/cli/src/workflow/plan-stage-runner.ts`, gate the artifact-exists shortcut behind a non-retry condition, or explicitly invalidate the existing Plan artifacts/state when the latest workflow run shows `approvalStatus === 'rejected'`. The retry path must force at least one fresh agent-backed Plan execution and rebuild the reviewable artifacts from the rejection feedback.

## Spec Compliance

1. PASS - Rejecting stage approval records the rejection feedback in WorkflowRun history.
Evidence: `packages/cli/src/workflow/domain/index.ts:732-743` stores rejected approval output and sets failure reason `approval-rejected`. `packages/cli/src/api/issues.ts:1744-1746` passes the user message into `rejectStage()`.

2. PASS - Rejecting stage approval enqueues work that actually runs instead of completing as `skipped` when the current stage failure is retryable.
Evidence: `packages/cli/src/services/agent-runner-service.ts:1087-1099` allows blocked issues through when `workflowRunService.canRetryStage(...)` is true. Covered by `packages/cli/tests/rejected-approval-retry-regression.test.ts:82-144`.

3. PASS - A blocked issue whose latest WorkflowRun failed at its current stage for a retryable rejection is treated as runnable by `resume-pipeline`.
Evidence: `packages/cli/src/services/workflow-run-service.ts:34-38`, `packages/cli/src/workflow/domain/index.ts:813-820`, and `packages/cli/src/services/agent-runner-service.ts:1092-1099`.

4. PASS - `resume-pipeline` uses the WorkflowRun retry semantics for the same current stage rather than broadly unblocking arbitrary blocked issues.
Evidence: blocked issues are still skipped when `canRetryStage` is false in `packages/cli/src/services/agent-runner-service.ts:1093-1099`. Negative cases are covered in `packages/cli/tests/rejected-approval-retry-regression.test.ts:147-212`.

5. FAIL - A new stage attempt/session is created after rejecting `Stage(name = "plan")` approval.
Evidence: `packages/cli/src/workflow/plan-stage-runner.ts:162-170` completes tasks from existing artifacts without creating a new session. The session is only created at `packages/cli/src/workflow/plan-stage-runner.ts:172-179, 226-253`. If artifacts already exist, that code is never reached.

6. FAIL - The new stage attempt receives the rejection feedback in its prompt/input context.
Evidence: feedback extraction and prompt wiring exist in `packages/cli/src/workflow/plan-stage-runner.ts:30-42, 103-118, 125-146` and `packages/cli/src/agents/artifact-prompt.ts:123-133, 199-209`, but because the artifact-exists fast path can skip all agent execution (`packages/cli/src/workflow/plan-stage-runner.ts:162-170`), there may be no new attempt that actually receives that input.

7. PASS - Existing behavior remains unchanged for genuinely blocked issues that do not have a retryable current-stage failed WorkflowRun.
Evidence: `packages/cli/src/services/agent-runner-service.ts:1093-1103`; tests at `packages/cli/tests/rejected-approval-retry-regression.test.ts:147-212`.

8. PASS - Queue task result is not `skipped` for retryable stage rejection reruns.
Evidence: `packages/cli/tests/rejected-approval-retry-regression.test.ts:137-144`.

9. FAIL - Regression tests cover: stage approval rejection -> queue resume -> same-stage retry starts.
Evidence: the added regression test only asserts `result !== 'skipped'` in `packages/cli/tests/rejected-approval-retry-regression.test.ts:82-144`. It does not verify that a same-stage retry actually started, that a new session/attempt was created, or that Plan work reran instead of being short-circuited by existing artifacts.

10. PASS - Regression tests cover that a genuinely blocked issue without a retryable current-stage failed WorkflowRun is still skipped/non-runnable.
Evidence: `packages/cli/tests/rejected-approval-retry-regression.test.ts:147-212`.

## Quality Notes

- Correctness: FAIL due to the Plan retry reuse bug above.
- Complexity: PASS. The changed helpers are small and straightforward.
- Test Coverage: FAIL. Tests cover queue gating and aggregate persistence, but they miss the end-to-end requirement that the Plan retry starts a fresh agent-backed attempt/session and reuses rejection feedback during real execution.
- Security: PASS. No new injection or secret-handling issue found in the touched code.

## Verification

- `npm test -- tests/rejected-approval-retry-regression.test.ts tests/workflow-application-service.test.ts tests/workflow-run-domain.test.ts` passed.

<promise>FAIL</promise>
