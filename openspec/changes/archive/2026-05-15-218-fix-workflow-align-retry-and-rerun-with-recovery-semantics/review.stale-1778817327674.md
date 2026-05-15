## Review

### Summary

The implementation aligns retry and rerun with distinct recovery semantics: retry targets failed work while preserving earlier valid work; rerun discards the current stage attempt and restarts from the first work item. The change covers the domain model, application service, HTTP API, Web UI error visibility, recovery vocabulary, and regression coverage.

### Correctness

**WorkflowRun.retryStage()** (`packages/cli/src/workflow/domain/index.ts:785-896`)

- Failed task retry: finds the failed task, calls `resetTaskAndDownstream()` which resets tasks at or after the failed task's order, then resets all checks. Earlier completed tasks are preserved. **PASS**
- Failed check retry: calls `resetCheckAndDownstream()` which resets the failed check and later checks, then resets repair tasks caused by that check. Completed tasks and earlier passed checks are preserved. **PASS**
- Approval rejection retry: resets all tasks and checks in the stage. This is appropriate since approval rejection means the whole stage needs rework. **PASS**
- Sets `status = 'running'`, `failure = null`, clears approval. Does not change `currentStage`. **PASS**

**WorkflowRun.rerunStage()** (`packages/cli/src/workflow/domain/index.ts:898-937`)

- Resets ALL current-stage tasks to pending with `attempts = 0`, clears all checks including `runCount = 0`. **PASS**
- Clears failure and approval state. Sets `status = 'running'`. Does not change `currentStage`. **PASS**
- Earlier passed stages are preserved (lines 904-913 only touch stages with order < current). **PASS**

**WorkflowApplicationService.checkRetryAvailability()** (`packages/cli/src/services/workflow-application-service.ts:73-114`)

- Uses `loadLatestAggregate` to find the latest WorkflowRun, checks it is `failed`, checks stage matches, and looks for failed task or check. Does NOT require `tasks.json` or checkpoint. **PASS**
- Returns distinct error reasons: `no-failed-workflow-run`, `stage-mismatch`, `no-retryable-failed-work`. **PASS**

**POST /api/issues/:number/retry** (`packages/cli/src/api/issues.ts:3196-3300`)

- When `workflowApplicationService` is available, calls `checkRetryAvailability()` and returns the error message on 409. Falls back to legacy checkpoint-based rejection when unavailable. **PASS**
- External resource checks (project, worktree, change dir) are secondary preconditions with distinct error messages. **PASS**
- Retry message uses "retrying from failed work" vocabulary. **PASS**

**POST /api/issues/:number/rerun** (`packages/cli/src/api/issues.ts:3324-3418`)

- Clears checkpoint, approval, blocked reason, retry count. Calls `workflowApplicationService.rerunStage()`. **PASS**
- Does not change the stage. Earlier stages are not rerun. **PASS**

**POST /api/issues/:number/restart** (`packages/cli/src/api/issues.ts:3302-3322`)

- Returns 410 with message: "restart has been removed; use retry, rerun, or rewind instead". **PASS**

### Complexity

- `retryStage()` at ~110 lines is over the 50-line guideline. The method handles three cases (approval rejection, failed task, failed check) with distinct reset logic. The cyclomatic complexity is moderate (under 10). **WARNING** — could benefit from extracting the three reset branches into helper methods, but not blocking.
- `rerunStage()` at ~40 lines is clean and focused. **PASS**
- `PlanStageRunner.executeTasks()` at ~220 lines is long but pre-existing and handles complex agent session lifecycle. **WARNING** — pre-existing, not introduced by this change.

### Test Coverage

- **Domain tests** (`tests/workflow-run-domain.test.ts`): 29 tests covering retry failed task, retry failed check, retry after approval rejection, rerun current stage from first work, rerun preserves earlier stages, Plan rerun resets all tasks. **PASS**
- **Application service tests** (`tests/workflow-application-service.test.ts`): 14 tests. **PASS**
- **Regression #215** (`tests/recovery-215-regression.test.ts`): 8 tests covering Plan failure before `tasks.json`, retry without checkpoint, rerun from first Plan work, stage preservation. **PASS**
- **Recovery verb** (`tests/recovery-verb-regression.test.ts`): 23 tests covering restart deprecation, retry/rerun vocabulary, stage preservation, resume guidance. **PASS**
- **All recovery-related tests pass** (74 tests across 4 files). **PASS**
- One pre-existing test failure in `tests/shared-agent-skills.test.ts` — unrelated to this change (tests UI template content, not modified in this branch).

### Security

- No injection risks. Recovery endpoints validate issue existence, project, and worktree before proceeding.
- No secrets exposed in error messages. **PASS**

### Spec Compliance

| Criterion | Status | Evidence |
|-----------|--------|----------|
| Plan fails before `tasks.json` → Retry retries failed Plan work | **PASS** | `recovery-215-regression.test.ts:114-185`, API uses `checkRetryAvailability` which does not check `tasks.json` existence |
| Retry failed task preserves earlier completed tasks | **PASS** | `workflow-run-domain.test.ts:403-428`, `domain/index.ts:837-843` resets only failed task and downstream |
| Retry failed check preserves completed tasks | **PASS** | `workflow-run-domain.test.ts:430-457`, `domain/index.ts:844-876` preserves completed tasks, resets failed check and downstream |
| `POST /retry` availability based on WorkflowRun failed work | **PASS** | `workflow-application-service.ts:73-114` checks `loadLatestAggregate`, status `failed`, stage match, failed task/check |
| `POST /retry` distinguishable errors | **PASS** | `RetryRejectionReason` type at `workflow-application-service.ts:36-41` with `no-failed-workflow-run`, `stage-mismatch`, `no-retryable-failed-work` |
| Rerun from first work item, not first incomplete | **PASS** | `domain/index.ts:920-928` resets ALL tasks to pending; `workflow-run-domain.test.ts:268-291` explicitly tests this |
| Rerun clears checkpoint, failure, approval, retry state | **PASS** | `issues.ts:3358-3388` clears checkpoint, approval, blocked reason, retry count; `domain/index.ts:916-934` clears failure, approval, all tasks/checks |
| Earlier passed stages not rerun | **PASS** | `domain/index.ts:904-913` only touches prior stages; `workflow-run-domain.test.ts:175-215, 244-266` |
| Plan rerun does not skip artifacts because files exist | **PASS** | `domain/index.ts:920-928` resets all Plan tasks to pending including `proposal`; `workflow-run-domain.test.ts:217-242` |
| Web UI shows retry errors in action error area | **PASS** | `IssueDetailPage.tsx:755-762` includes `retryMutation.error` alongside close/reopen/start/rerun errors |
| Recovery vocabulary uses retry/rerun/rewind, no restart | **PASS** | `issues.ts:3302-3322` restart returns 410 with deprecation; `recovery-verb-regression.test.ts:334-391` verifies restart removal |
| Regression #215 shape covered | **PASS** | `recovery-215-regression.test.ts` 8 tests across 4 describe blocks |

### Warnings

1. **W1**: `retryStage()` in `domain/index.ts:785-896` is ~110 lines. The three reset branches (approval rejection, failed task, failed check) could be extracted into named helpers for readability. Not blocking.

2. **W2**: The reject endpoint message at `issues.ts:1844` says "pipeline rerunning from build/plan" — this is accurate (it uses "rerunning" not "restart") but the phrasing could be clearer as "pipeline will rerun from build/plan stage". Non-blocking.

3. **W3**: The Web UI test (`IssueDetailPage.test.tsx`) does not have a dedicated test for retry error visibility. The error rendering is structurally identical to rerun/start/close errors (same JSX block at lines 755-762), and the Check repair actions test at line 358 verifies the UI renders recovery buttons. While the error display is simple enough to verify by code inspection, a focused test would strengthen coverage. Non-blocking.

4. **W4**: `PlanStageRunner.executeSingleArtifactTask()` at `plan-stage-runner.ts:150-221` still uses `checkpointManager.getResumeSteps()` and `task.verifyArtifact()` to decide skip behavior. During normal resume this is correct (design D4 says "file-exists skip may still be valid for normal interruption resume"). The key question is: when rerun creates pending WorkflowRun tasks, does the Plan runner receive the correct `ctx.requestedWork` that starts at `proposal`? The `BaseStageRunner` and workflow engine layer handle this — the runner's `executeReportedTask()` method is called per-task by the engine, so when all tasks are reset to pending by rerun, the engine will request `proposal` first. The checkpoint is also deleted at `issues.ts:3358-3363` during rerun, which clears `getResumeSteps()`. **PASS with note** — the interaction between checkpoint deletion, WorkflowRun task reset, and runner execution is correct but distributed across multiple layers.

### Fix Suggestions

No error-level issues found. All warnings are non-blocking.

<promise>PASS</promise>
