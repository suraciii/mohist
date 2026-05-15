## Review: #218 fix(workflow): align retry and rerun with recovery semantics

### Scope

23 files changed, +1914 / -92 lines across domain, service, API, plan runner, CLI, web UI, and tests.

### Correctness

**PASS** — No logic errors found.

- **Retry failed task**: `retryStage()` finds `failedTask`, calls `resetTaskAndDownstream(failedTask.id)` which resets only tasks at or after the failed task's order. Earlier completed tasks are preserved by the order-based boundary check in `resetTaskAndDownstream` (`domain/index.ts:370-384`).
- **Retry failed check**: For a failed check, `retryStage()` preserves completed tasks (they're `terminal`, skipped by `!task.terminal` guard at `domain/index.ts:893`), resets the failed check and downstream checks via `resetCheckAndDownstream()` (`domain/index.ts:386-407`), and resets `causedBy` repair tasks.
- **Rerun from first work**: `rerunStage()` resets ALL current-stage tasks to pending with `attempts: 0` and resets all checks including `runCount: 0` (`domain/index.ts:959-973`). The domain method no longer requires `status === 'running'`, which is correct — the service layer handles loading the right aggregate.
- **Plan rerun skips artifact-exists**: The bridge observer guard at `plan-stage-runner.ts:419` now requires `resumeSteps.length > 0` (checkpoint has entries), so when rerun deletes the checkpoint, existing artifact files alone do not mark work complete. The unconditional `verifyArtifact()` skip in `executeSingleArtifactTask` was also removed (`plan-stage-runner.ts:189-192`).
- **Rerun endpoint**: `POST /api/issues/:number/rerun` now calls `workflowApplicationService.rerunStage()` directly instead of the old `resumeDecision → retry fallback` pattern (`issues.ts:4070`).
- **Review-rerun**: The review-rerun endpoint correctly tries `retryStage` first (lighter), then falls back to `rerunStage` if still failed (`issues.ts:4260-4272`). This is an improvement over the old inverse order.

**Edge cases verified:**
- `worktreeManager?.exists` guard at `issues.ts:3903` correctly handles both missing worktreeManager and missing `exists` method.
- Plan stage `hasRetryArtifacts()` returns true when change dir exists (`issues.ts:752`), providing a correct legacy fallback for Plan retry without `workflowApplicationService`.
- Rerun deletes both `issue.stage` and `'plan'` checkpoints for Plan stage (`issues.ts:4030-4032`).

### Complexity

**WARNING** — `retryStage()` at `domain/index.ts:814-926` is ~112 lines with 5 branching paths (approval-rejected, failed task, failed check with Check-stage special case, failed check general, fallback). The branching is inherent to the domain but could benefit from extracting the Check-stage special case (lines 874-889) into a helper. Other functions are well-scoped:

- `resetTaskAndDownstream`: 15 lines
- `resetCheckAndDownstream`: 22 lines
- `checkRetryAvailability`: 42 lines
- `rerunStage`: 40 lines
- `retryStageOrReject`: 33 lines

The first loop in the failed-check retry path (`domain/index.ts:892-904`) is effectively a no-op in normal scenarios because all tasks are `terminal` (completed) when checks run. It's defensive but adds reading complexity without behavioral impact.

### Test Coverage

**PASS** — All new code has tests. Full suite: 168 test files, 2943 passed, 0 failed.

| Layer | Tests | Coverage |
|-------|-------|----------|
| Domain (`workflow-run-domain.test.ts`) | +249 lines | Retry failed task, retry failed check, retry failed check with repair tasks, rerun from first work, rerun preserves prior stages, Plan rerun from first work |
| Service (`workflow-application-service.test.ts`) | +151 lines | checkRetryAvailability: no run, not failed, stage mismatch, no failed work, available with failed task, available with failed check, latest aggregate fallback |
| API regression (`recovery-215-regression.test.ts`) | 309 lines | Plan fails before tasks.json, retry without tasks.json, no checkpoint-required error, rerun from first Plan work, stage preservation, post-tasks.json retry |
| Plan runner (`pipeline-checkpoint.test.ts`) | +65 lines | Re-execute all tasks on rerun with existing artifacts |
| Web UI (`IssueDetailPage.test.tsx`) | +44 lines | Retry error display, other actions visible after retry error |
| API routes (`api-routes.test.ts`) | 3 lines | Updated non-blocked issue test to set Plan stage |

### Security

**PASS** — No injection risks, no exposed secrets, no unsafe input handling. `catch {}` on artifact deletion (`issues.ts:4046`) is acceptable for cleanup operations. Issue number parsing uses `parseInt()` which is safe for route parameters.

### Spec Compliance

| Criterion | Status | Evidence |
|-----------|--------|----------|
| Plan retry before tasks.json exists | **PASS** | `checkRetryAvailability` checks WorkflowRun failed state, not filesystem (`workflow-application-service.ts:73-114`). Legacy fallback `hasRetryArtifacts` returns true for Plan stage (`issues.ts:752`). Regression test at `recovery-215-regression.test.ts:136-161`. |
| Retry failed task preserves earlier completed tasks | **PASS** | `resetTaskAndDownstream` uses order boundary (`domain/index.ts:370-384`). Domain test verifies T-001 stays completed (`workflow-run-domain.test.ts:425`). |
| Retry failed check preserves completed tasks | **PASS** | `!task.terminal` guard skips completed tasks (`domain/index.ts:893`). Domain test verifies T-001 stays completed (`workflow-run-domain.test.ts:464`). |
| Retry availability based on WorkflowRun failed work | **PASS** | `checkRetryAvailability` loads latest aggregate, checks for failed task/check (`workflow-application-service.ts:73-114`). API uses this when service available (`issues.ts:3925-3936`). |
| Distinguishable retry rejection errors | **PASS** | `RetryRejectionReason` type with values: `no-failed-workflow-run`, `stage-mismatch`, `no-retryable-failed-work` (`workflow-application-service.ts:35-39`). API provides distinct messages for missing project (404), missing worktree (409), missing change dir (409). |
| Rerun from first current-stage work | **PASS** | `rerunStage` resets all tasks from index 0 with `attempts: 0` (`domain/index.ts:959-966`). Plan runner requires `resumeSteps.length > 0` for artifact skip (`plan-stage-runner.ts:419`). |
| Rerun clears checkpoint, failure, approval, retry state | **PASS** | Checkpoint delete at `issues.ts:4028-4033`. Domain clears failure/approval (`domain/index.ts:955-957`). API clears blocked reason and retry count (`issues.ts:4056-4057`). |
| Rerun preserves earlier passed stages | **PASS** | Domain test verifies Plan/Build/Check stay passed after Integrate rerun (`workflow-run-domain.test.ts`). Domain code only resets current stage tasks (`domain/index.ts:959-973`). |
| Plan rerun doesn't skip existing artifacts | **PASS** | Removed unconditional artifact skip in `executeSingleArtifactTask`. Bridge observer requires `resumeSteps.length > 0` (`plan-stage-runner.ts:419`). Pipeline checkpoint test verifies all 5 artifacts are regenerated (`pipeline-checkpoint.test.ts:608+`). |
| Web UI shows retry errors | **PASS** | `retryMutation.error` added to action error area condition and message chain (`IssueDetailPage.tsx:857-863`). Two web tests verify visibility and continued access to other actions (`IssueDetailPage.test.tsx:454-491`). |
| Recovery vocabulary (retry/rerun/rewind, no restart) | **PASS** | Approval rejection changed from "restarted" to "rerunning" (`issues.ts:2514`). Retry message uses "retrying from failed work" (`issues.ts:3945`). Restart endpoint returns 410 with redirect message (`issues.ts:3987`). "server restart" in web UI is unrelated (`IssueDetailPage.tsx:676`). |
| Regression for #215 shape | **PASS** | Full scenario: Plan fails before tasks.json → retry succeeds → rerun restarts from first Plan work → stage preserved (`recovery-215-regression.test.ts`). |

### Warnings

1. **Dead code in retryStage**: The first loop in the failed-check branch (`domain/index.ts:892-904`) is effectively a no-op because tasks are terminal when checks run. The `isRepairTaskForFailedCheck` guard is overridden by `resetCheckAndDownstream` which also resets caused-by tasks. Not a bug but adds reading complexity.

2. **Unused method**: `retryStageOrReject` at `workflow-application-service.ts:154-186` is defined but not called by any API endpoint. The API uses `checkRetryAvailability` + `retryStage` separately. Consider removing or using it to simplify the retry endpoint.

3. **Defensive prior-stage mutation**: Both `retryStage` and `rerunStage` mutate prior stage tasks to `completed` (`domain/index.ts:832-842`, `943-953`). This could mask aggregate loading bugs and fabricates `attempts = 1` for tasks with 0 attempts.

<promise>PASS</promise>
