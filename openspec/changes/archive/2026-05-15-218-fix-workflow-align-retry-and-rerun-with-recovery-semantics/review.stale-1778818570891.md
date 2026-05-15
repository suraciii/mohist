## Review: #218 fix(workflow): align retry and rerun with recovery semantics

### Correctness

No errors found. The prior review identified a Plan runner file-exists skip bug which was fixed in commit `b86f9ec259` by removing the standalone `task.verifyArtifact()` skip from the aggregate path and adding a `resumeSteps.length > 0` guard to the bridge observer path.

Key correctness observations:

1. **Retry targets failed work** — `retryStage` at `domain/index.ts:837-838` correctly identifies failed task/check and resets only from that boundary. Earlier completed tasks remain completed due to the `order >= boundaryOrder` check in `resetTaskAndDownstream` and the `!task.terminal` guard in the check-failure path.

2. **Rerun resets from first work** — `rerunStage` at `domain/index.ts:920-928` resets all current-stage tasks to pending with `attempts: 0` and clears all checks. Earlier passed stages are untouched (only gaps in prior-stage task status are filled, not resets).

3. **Plan rerun honors WorkflowRun state, not file existence** — The fix removed the standalone `task.verifyArtifact()` skip from `plan-stage-runner.ts:163-166`, so only checkpoint-based resume can skip. When checkpoint is cleared during rerun, all Plan tasks re-execute. Test at `pipeline-checkpoint.test.ts:505-568` verifies this.

4. **Retry availability based on WorkflowRun** — `checkRetryAvailability` at `workflow-application-service.ts:73-113` loads the latest failed WorkflowRun and checks for failed task/check, with no dependency on `tasks.json` or checkpoint existence.

5. **Rerun API clears all current-stage state** — `issues.ts:3358-3388` clears checkpoint (including plan variant), cancels agents, clears approval, resets retry count, and calls `workflowApplicationService.rerunStage()` which resets the domain model.

6. **Prior review fix verified** — The `getPlanRejectionFeedback` method and `shouldReplan` flag at `plan-stage-runner.ts:160-161` correctly integrates Plan rejection feedback into rerun without affecting the file-exists skip logic.

---

### Complexity

**WARNING: `retryStage` exceeds 50-line guideline**

`domain/index.ts:785-896` is ~110 lines with nested branching for:
- Approval rejection (full reset)
- Failed task (downstream reset)
- Failed check with special Check-stage fix-review case
- Fallback no-specific-failure

The branching is logically sound but dense. Recommend extracting the approval-rejected branch and the Check-stage fix-review special case into named helpers for readability.

All other methods are within limits:
- `rerunStage`: 40 lines
- `checkRetryAvailability`: 42 lines  
- API retry endpoint: well-structured with early returns
- `resetTaskAndDownstream`: 14 lines
- `resetCheckAndDownstream`: 22 lines

---

### Test Coverage

- **Domain retry** — `workflow-run-domain.test.ts:403-492` covers failed task retry (preserves earlier completed tasks), failed check retry (preserves completed tasks, resets downstream checks), failed check with repair tasks. PASS.
- **Domain rerun** — `workflow-run-domain.test.ts:131-400` covers rerun from first work, not resetting earlier stages, Plan rerun with existing artifacts, resetting all current-stage state. PASS.
- **Application service retry availability** — `workflow-application-service.test.ts:209-359` covers no-failed-run, stage-mismatch, no-retryable-work, available for failed task/check, latest aggregate fallback. PASS.
- **API retry/rerun regression** — `recovery-215-regression.test.ts:1-309` covers retry before tasks.json, retry after tasks.json, rerun from Plan, stage preservation, error visibility. PASS.
- **Plan runner rerun** — `pipeline-checkpoint.test.ts:505-568` covers re-executing all Plan tasks when artifacts already exist and checkpoint is cleared. PASS.
- **Web UI retry errors** — `web/tests/IssueDetailPage.test.tsx:453-491` covers retry error visibility and recovery action availability after error. PASS.
- **Artifact prompt feedback** — `artifact-prompt.test.ts:186-192` covers plan rejection feedback inclusion. PASS.

Typecheck passes. All 219 related tests pass (7 pre-existing failures in `shared-agent-skills.test.ts` are unrelated to this change).

---

### Security

No concerns. Recovery endpoints validate project, issue, and stage before executing. `tasksPath` is derived from `findChangeDir` and `path.join`, not user input. No secrets exposed.

---

### Spec Compliance

| # | Acceptance Criterion | Status | Evidence |
|---|---|---|---|
| 1 | Plan fails generating tasks.json → Retry retries failed Plan work | **PASS** | `checkRetryAvailability` at `workflow-application-service.ts:73-113` checks WorkflowRun failed work, not tasks.json. Regression test at `recovery-215-regression.test.ts:113-186`. |
| 2 | Retry failed task → no re-run of earlier successful tasks | **PASS** | `domain/index.ts:837-838` calls `resetTaskAndDownstream(failedTask.id)` which resets from failed task boundary. Test at `workflow-run-domain.test.ts:403-428`. |
| 3 | Retry failed check → preserves completed tasks | **PASS** | `domain/index.ts:844-875` uses `!task.terminal` guard to preserve completed tasks, then `resetCheckAndDownstream` resets check and caused-by tasks. Test at `workflow-run-domain.test.ts:430-492`. |
| 4 | POST /retry availability based on WorkflowRun | **PASS** | `issues.ts:3258-3262` calls `checkRetryAvailability` which loads latest WorkflowRun. No dependency on checkpoint or tasks.json. |
| 5 | POST /retry distinguishable errors | **PASS** | `workflow-application-service.ts:35-41` defines 6 rejection reasons. API returns distinct messages at `issues.ts:3209-3265`. Tests at `workflow-application-service.test.ts:209-359`. |
| 6 | Rerun from first work, not first incomplete | **PASS** | `domain/index.ts:920-928` resets ALL tasks to pending from index 0 with `attempts: 0`. Test at `workflow-run-domain.test.ts:268-291`. |
| 7 | Rerun clears checkpoint, failure, approval, retry state | **PASS** | API at `issues.ts:3358-3388` clears checkpoint (including plan variant), cancels agents, clears approval, resets retry count. Domain at `domain/index.ts:916-933` clears failure, approval, resets all state. |
| 8 | Earlier passed stages not rerun | **PASS** | `domain/index.ts:904-913` only fills status gaps in prior stages. Test at `workflow-run-domain.test.ts:244-266`. |
| 9 | Plan rerun doesn't skip artifacts due to file existence | **PASS** | Fix at `b86f9ec259` removed standalone `task.verifyArtifact()` skip from aggregate path. Bridge observer adds `resumeSteps.length > 0` guard at `plan-stage-runner.ts:386`. Test at `pipeline-checkpoint.test.ts:505-568`. |
| 10 | Web UI displays retry errors | **PASS** | `IssueDetailPage.tsx:755-761` includes `retryMutation.error` in shared action error area. Test at `web/tests/IssueDetailPage.test.tsx:453-491`. |
| 11 | Recovery vocabulary uses retry/rerun/rewind, not restart | **PASS** | CLI at `issue.ts:773-778` uses server response message. API at `issues.ts:1844` uses "rerunning" instead of "restarted". Restart endpoint returns 410 with "restart has been removed; use retry, rerun, or rewind instead" at `issues.ts:3317`. |
| 12 | Regression coverage for #215 shape | **PASS** | `recovery-215-regression.test.ts` covers retry before tasks.json (lines 113-186), rerun from Plan (lines 188-231), stage preservation (lines 234-278), and post-tasks.json retry (lines 280-308). |

---

### Warnings

1. **`retryStage` complexity** — 110 lines with 4 branches. Recommend extracting `retryAfterApprovalRejection`, `retryAfterFailedTask`, `retryAfterFailedCheck` helpers.
2. **Double load in retry path** — `checkRetryAvailability` loads the aggregate, then `retryIssueCheckpoint` loads and retries again. Minor inefficiency but not a correctness issue.

---

### Summary

The implementation correctly implements retry-from-failed-work and rerun-from-first-work recovery semantics. The Plan runner file-exists skip bug identified in the prior review was fixed. All 12 acceptance criteria are met. Typecheck passes. 219 related tests pass. Two warnings about `retryStage` complexity and double-load inefficiency are non-blocking.

<promise>PASS</promise>
