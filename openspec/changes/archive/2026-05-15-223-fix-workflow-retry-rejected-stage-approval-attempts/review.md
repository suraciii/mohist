## Review: Issue #223 — Fix workflow retry rejected stage approval attempts

### Correctness

**PASS** — The core bug fix is minimal and correct.

- **T-001 (`canRetryStage`)**: The domain predicate at `packages/cli/src/workflow/domain/index.ts:813-820` correctly mirrors the guards in `retryStage()` (status=failed, currentStage match, stageRun status=failed) without mutating state. Pure read-only boolean method.

- **T-002 (retryable blocked resumes)**: The fix at `packages/cli/src/services/agent-runner-service.ts:1092-1099` adds an `else if (this.workflowRunService)` branch between the approved-approval path and the catch-all blocked skip. This correctly inserts the retryability check without affecting the existing approved-continuation path (line 1088-1091) or the no-workflowRunService fallback (line 1100-1104). The service delegates actual retry to `runPipelineToCompletion` → `WorkflowEngine`, keeping `AgentRunnerService` shallow.

- **T-003 (rejection feedback priority)**: The one-line change at `packages/cli/src/api/issues.ts:1745` flips priority from `approvalState.output ?? message` to `message ?? approvalState.output`, correctly ensuring user rejection feedback is not shadowed by prior approval request output. `WorkflowRunService.canRetryStage` at `packages/cli/src/services/workflow-run-service.ts:34-38` delegates to the domain aggregate without mutation.

- **T-004 (feedback to Plan attempts)**: `extractRejectionFeedback` at `packages/cli/src/workflow/plan-stage-runner.ts:39-47` correctly reads from both `retryFeedback` (via `pendingRejectionFeedback` map) and fallback `stageRun.approvalOutput`. The `normalizeRejectionFeedback` helper (line 30-37) handles both string and `{ feedback: string }` structured output. The `isRejectedPlanRetry` guard (line 49-54) forces fresh attempts by skipping checkpoint shortcuts (lines 175, 179), ensuring artifacts are regenerated. Feedback is threaded through `buildArtifactPrompt` and `buildSelfReviewPrompt` to all Plan artifact prompts.

- **T-005 (regression tests)**: 13 tests covering all acceptance criteria. Tests use in-memory SQLite with real aggregates and repos, validating end-to-end queue behavior.

**Minor note**: The first AC-1 test (line 93-143) creates two workflow runs. The first run gets fully completed but the second is the one that matters (failed with rejected approval). `loadLatestAggregate` correctly picks the second via `ORDER BY created_at DESC`. The test doesn't explicitly set `workflow_stage_runs.status = 'failed'` on the second run, but `hydrateWorkflowRun` (`persistence.ts:123-127`) infers stage failure from rejected approval and sets the status. This is consistent with how the real system works.

### Complexity

**PASS** — All functions are well under 50 lines. `canRetryStage` is 5 lines. The `executeResumePipelineTask` change adds 8 lines to a guarded branch. `extractRejectionFeedback` is 9 lines. `normalizeRejectionFeedback` is 6 lines. No cyclomatic complexity concerns.

### Test Coverage

**PASS** — All new code paths are covered:

- `canRetryStage`: 5 domain unit tests in `workflow-run-domain.test.ts` (retryable, not-failed, different-stage, no-mutation, non-current-stage)
- Rejection feedback: 5 application-service tests in `workflow-application-service.test.ts` (string, structured, not-shadowed, enqueue, string-over-prior)
- Queue behavior: 13 regression tests in `rejected-approval-retry-regression.test.ts` covering AC-1 through AC-6
- All 51 relevant tests pass. The one failing test (`shared-agent-skills.test.ts`) is pre-existing and unrelated.

### Security

**PASS** — No new input vectors. The `rejectionFeedback` flows through existing prompt construction paths. No injection risks. No secrets exposed.

### Spec Compliance

#### workflow-run/spec.md

- **retryable-current-stage-rejection**: PASS
  - Failed current stage retryable: `domain/index.ts:813-820` checks all three conditions. Test at `workflow-run-domain.test.ts:519-525`.
  - Non-current stage not retryable: `domain/index.ts:815` returns false. Test at `workflow-run-domain.test.ts:527-534`.
  - Non-failed run not retryable: `domain/index.ts:814` returns false. Test at `workflow-run-domain.test.ts:539-542`.
  - No mutation: Test at `workflow-run-domain.test.ts:544-555` compares snapshots before/after.

- **stage-approval-rejection-feedback**: PASS
  - Rejection message recorded: `issues.ts:1745` passes `message` first. `domain/index.ts:735` stores as `output`. Test at `workflow-application-service.test.ts:209-226`.
  - Prior approval context does not shadow: Priority flip at `issues.ts:1745`. Test at `workflow-application-service.test.ts:296-326`.

#### pipeline-model/spec.md

- **blocked-retryable-current-stage-resume**: PASS
  - Retryable blocked failure runs: `agent-runner-service.ts:1092-1094` allows through. Test at `rejected-approval-retry-regression.test.ts:146-176` verifies `runPipelineToCompletion` is called.
  - Genuinely blocked remains skipped: `agent-runner-service.ts:1095-1098`. Test at `rejected-approval-retry-regression.test.ts:180-194` (no run), `197-222` (different stage), `224-244` (non-failed status).
  - Approved continuation still works: `agent-runner-service.ts:1088-1091` unchanged. Test at `rejected-approval-retry-regression.test.ts:355-383`.

- **rejected-approval-resume-regression**: PASS
  - Rejected Plan starts same-stage retry: Test at `rejected-approval-retry-regression.test.ts:82-143` verifies result is not `skipped`, and test at `146-176` verifies `runPipelineToCompletion` is called with `Stage.Plan`.
  - Non-retryable remains skipped: Tests at `rejected-approval-retry-regression.test.ts:180-244`.

#### coder-session-tracking/spec.md

- **agent-backed-rejected-approval-retry-session**: PASS
  - New session: `plan-stage-runner.ts:173` (`isRejectedPlanRetry`) forces fresh attempt, skipping checkpoint reuse at lines 175 and 179. The Plan stage always starts a new `AgentSession` at line 378+ when not shortcut.
  - Rejection feedback in retry input: `plan-stage-runner.ts:338` extracts feedback, passes through `createTaskConfigs` to `buildArtifactPrompt`/`buildSelfReviewPrompt` which include feedback in the prompt text at `artifact-prompt.ts:123-131,199-207`.
  - Requests approval again: `plan-stage-runner.ts:175-179` force fresh attempt, and `retryStage` at `domain/index.ts:775` clears `stageRun.approval = null`, so the normal approval cycle runs again.

### Warnings

1. **`loadLatestAggregate` is not truly read-only**: `workflow-run-repo.ts:381-391` calls `repairWorkflowRunSnapshot` + `saveAggregateSnapshot` during load, meaning `canRetryStage` via `WorkflowRunService` triggers a DB write. This is pre-existing behavior and the repair is idempotent, but it's worth noting for future optimization. Not a regression.

2. **Test AC-1 first test creates two runs**: The first workflow run is left in a completed/awaiting state while the second is used for the rejection test. The test passes because `loadLatestAggregate` picks the latest. If the test setup were simplified to use a single run, it would be clearer. Not a functional issue.

3. **`extractRejectionFeedback` duplicated logic**: The diff added `extractRejectionFeedback` as a new function (line 39-47) that differs from the one shown in the original diff. The final file has the correct version with `retryFeedback` parameter, which is the right design. The git diff showed an earlier iteration. No issue with the final code.

### Summary

The implementation is a focused, minimal bug fix. The core change is 8 lines in `executeResumePipelineTask` that insert a retryability check between the approved-approval path and the catch-all skip. The supporting `canRetryStage` domain predicate correctly mirrors `retryStage` guards. The feedback priority fix is a single-line flip. The Plan runner correctly forces fresh attempts and threads feedback into prompts. Test coverage is comprehensive with 13 regression tests plus 10 domain/service-level tests. All relevant tests pass. Typecheck passes.

<promise>PASS</promise>
