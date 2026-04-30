# Review Report

## Result: FAIL

## Dimensions

### Correctness: FAIL

**ERROR: Frontend-backend check name mismatch breaks UI logic**

The backend produces `CheckResult.name` values `'build-test'`, `'merge-ready'`, `'ai-review'` (workflow-controller.ts:1075, :1222, :1372). The frontend `CheckResultsPanel.tsx:163-164` searches for:
```ts
const buildTest = checks.find((c) => c.name === 'Build & Test')
const aiReview = checks.find((c) => c.name === 'AI Review')
```
These will **never match**, so `buildFailed` and `aiReviewFailed` are always `false`, and the panel always shows "All checks passed" behavior regardless of actual check results. The "Back to Build" and "Force Approve" buttons never appear.

**ERROR: Frontend `CheckSuiteOutput.overallResult` type is `'passed' | 'failed'` but backend type includes `'blocked'`**

`packages/cli/web/src/lib/types.ts:362`: `overallResult: 'passed' | 'failed'` vs `packages/cli/src/types/index.ts:195`: `CheckOverallResult = 'passed' | 'failed' | 'blocked'`. If the backend ever sets `'blocked'`, the frontend type won't match. The backend never actually sets `'blocked'` today (only `'passed'` or `'failed'`), so this is a type inconsistency rather than a runtime bug.

**ERROR: Frontend `CheckResult` has `autoFixAttempts` field absent from backend type**

`packages/cli/web/src/lib/types.ts:355`: `autoFixAttempts?: number` is not in the backend `CheckResult` type (`packages/cli/src/types/index.ts:182-193`). The backend never sets this field, so `CheckResultsPanel.tsx:87` and `:91` which reference `check.autoFixAttempts` will always be `undefined`, meaning "Auto-fixed (N attempts)" badge and "Auto-fix attempt N/2..." indicator never display.

**WARN: `runBuildTestCheck` loop structure is confusing but functionally correct**

`workflow-controller.ts:1054`: `for (let attempt = 0; attempt <= (autoFix ? maxFixAttempts : 0); attempt++)` — when `autoFix=true` and `maxFixAttempts=2`, the loop runs up to 3 iterations (attempt 0, 1, 2). The fix agent spawns inside the catch block of each failed iteration. This gives 2 fix attempts and up to 3 build runs, which matches the spec. The `autoFixed: attempt > 0` at line 1078 is correct. However, the loop structure is difficult to follow.

**WARN: 103 pre-existing tests fail**

`tests/review-auto-fix.test.ts:221` calls `(ctrl as any).runPipelineReviewStage(...)` which no longer exists — all 21 tests fail. `tests/review-merge-flow.test.ts` — all 12 tests fail (old `mergeBackFn` flow). `tests/pipeline-controller.test.ts` — 13 of 16 tests fail (old event patterns). `tests/stage-auto-fix.test.ts` — 4 tests fail (incorrect `parseVerdict` expectations).

**WARN: Build & Test check runs command via `execFileAsync` with `shell: true`**

`workflow-controller.ts:1064-1069`: The command string from config is passed directly to shell execution. Low severity — command comes from local workflow.yaml (trusted source).

### Complexity: PASS

- `runPipelineCheckStage` at ~120 lines is well-structured and sequential
- `runBuildTestCheck` at ~85 lines handles the fix loop clearly
- `runAiReviewCheck` at ~140 lines preserves existing review logic
- `runAutoFixLoop` at ~95 lines is focused
- `CheckResultsPanel.tsx` at 351 lines is within acceptable bounds for a UI component with multiple states

### Test Coverage: FAIL

- **28/28 check-suite tests pass** — covers Build & Test pass/fail/timeout/auto-fix, Merge Ready fast-forward/needs-rebase/disabled, AI Review pass/fail/auto-fix, sequential execution, disabled checks. Good coverage for new code.
- **103 pre-existing tests fail** across `review-auto-fix.test.ts` (21), `review-merge-flow.test.ts` (12), `pipeline-controller.test.ts` (13), `stage-auto-fix.test.ts` (4), `worktree-manager.test.ts` (3), `acp-hang-recovery.test.ts` (5), `agent-runner-service.test.ts` (3), `build-pipeline-observability.test.ts` (1), `database.test.ts` (1), `e2e.test.ts` (2), `merge-queue.test.ts` (2), and others. Many reference the old `runPipelineReviewStage` method or old `mergeBackFn` flow.

### Security: PASS

- No SQL injection risks (parameterized queries in migration)
- Build command comes from local workflow.yaml (trusted source)
- `truncateLog` prevents excessive memory usage from build output
- `maxBuffer: 10 * 1024 * 1024` caps execFile output
- No exposed secrets

### Spec Compliance: FAIL

#### T-001: Rename Stage.Review to Stage.Check
- **PASS** AC: "Stage enum has `Check = 'check'` and no `Review = 'review'`" — `types/index.ts:5` has `Check = 'check'`, no `Review`
- **PASS** AC: "STAGE_ORDER and STAGE_TRANSITIONS use Stage.Check" — `types/index.ts:14,22` confirmed
- **PASS** AC: "DB migration v16 runs UPDATE issues SET stage = 'check' WHERE stage = 'review'" — `migrations.ts:610`
- **PASS** AC: "packages/cli/src/api/issues.ts uses Stage.Check in approve/reject handlers" — `issues.ts:950,1098` confirmed
- **PASS** AC: "CLI --skip-to-check flag works" — `issue.ts:390` has `--skip-to-check`
- **PASS** AC: "No Stage.Review in types/index.ts or api/issues.ts" — confirmed via code review
- **PASS** AC: "Typecheck passes" — `npx tsc --noEmit` produces no errors
- **FAIL** AC: "Existing tests pass" — 103 pre-existing tests fail (review-auto-fix, review-merge-flow, pipeline-controller, stage-auto-fix)

#### T-002: Add ChecksConfig to WorkflowConfig
- **PASS** AC: "ChecksConfig interface defined with buildTest, ffMerge, aiReview fields" — `workflow-loader.ts:16-35`
- **PASS** AC: "DEFAULT_CHECKS_CONFIG has correct defaults (command, timeout 300000, autoFix true, maxFixAttempts 2)" — `workflow-loader.ts:43-56` matches all values
- **PASS** AC: "loadWorkflow() parses checks section from workflow.yaml" — `workflow-loader.ts:178` passes `parsed.checks` to `parseChecksConfig`
- **PASS** AC: "Missing checks section falls back to defaults" — `workflow-loader.ts:59` returns `undefined` when no checks, then `loadChecksConfig` returns `DEFAULT_CHECKS_CONFIG`
- **PASS** AC: "Invalid checks fields don't crash parsing" — `parseChecksConfig` at `:58-96` uses safe typeof checks with fallbacks
- **PASS** AC: "Typecheck passes" — confirmed

#### T-003: Implement CheckResult and CheckSuiteOutput types
- **PASS** AC: "CheckResult interface has all spec'd fields with correct types" — `types/index.ts:182-193` has name (string), status (CheckStatus), duration?, autoFixed?, summary?, verdict?, dimensions?, reviewReport?, buildLog?, conflictFiles?
- **PASS** AC: "CheckSuiteOutput interface has checks array and overallResult" — `types/index.ts:197-199`
- **PASS** AC: "Types are exported from the types module" — all are exported
- **PASS** AC: "Typecheck passes" — confirmed

#### T-004: Implement runPipelineCheckStage with Build & Test check
- **PASS** AC: "runPipelineCheckStage() runs Build & Test check first" — `workflow-controller.ts:1259`
- **PASS** AC: "Build command succeeds on first attempt → CheckResult status 'passed'" — `:1074-1081`
- **PASS** AC: "Build command fails + autoFix succeeds → CheckResult status 'passed', autoFixed true" — `:1078` sets `autoFixed: attempt > 0`
- **PASS** AC: "Build command fails + autoFix exhausted (2 attempts) → CheckResult status 'failed'" — `:1087-1105`
- **PASS** AC: "Build command fails + autoFix disabled → CheckResult status 'failed' immediately" — loop runs once when autoFix=false (`attempt <= 0`)
- **PASS** AC: "Build command timeout → CheckResult status 'failed' with timeout indicator" — `:1084` checks `err.killed === true`, `:1101` includes "timed out"
- **PASS** AC: "Build & Test failure stops the suite" — `:1270` returns early when buildTestResult.status === 'failed'
- **PASS** AC: "CheckResult stored in CheckSuiteOutput within approvalState.output" — `:1272-1276` creates suiteOutput with checks array; `:556` stores in approvalState.output
- **PASS** AC: "Stage.Check case in run() dispatches to runPipelineCheckStage" — `:533`
- **PASS** AC: "Typecheck passes" — confirmed

#### T-005: Add Merge Ready check and AI Review check
- **PASS** AC: "Merge Ready check calls canFastForward and returns CheckResult with 'Merge Ready: yes' or 'Merge Ready: needs rebase'" — `workflow-controller.ts:1213-1226`
- **PASS** AC: "Merge Ready always returns status 'passed' (informational, non-blocking)" — all return paths at `:1189, :1206, :1222, :1235` use `status: 'passed'`
- **PASS** AC: "Merge Ready disabled via checks.ff-merge.enabled: false → skipped, no CheckResult produced" — `:1293` checks `checksConfig.ffMerge.enabled`
- **PASS** AC: "AI Review check preserves existing behavior: reviewer → self-check → verdict → auto-fix loop" — `runAiReviewCheck` at `:1366-1505` follows same pattern
- **PASS** AC: "AI Review PASS → CheckResult status 'passed', verdict 'PASS'" — `:1462`
- **PASS** AC: "AI Review FAIL + auto-fix PASS → CheckResult status 'passed', autoFixed true" — `:1487`
- **PASS** AC: "AI Review FAIL + auto-fix exhausted → CheckResult status 'failed', verdict 'FAIL'" — `:1492`
- **PASS** AC: "AI Review disabled via checks.ai-review.enabled: false → skipped" — `:1313` checks `checksConfig.aiReview.enabled`
- **PASS** AC: "All checks pass → CheckSuiteOutput.overallResult 'passed'" — `:1357`
- **PASS** AC: "Any blocking check fails → CheckSuiteOutput.overallResult 'failed'" — `:1274, :1335`
- **PASS** AC: "Typecheck passes" — confirmed

#### T-006: Wire approval handler to MergeQueue
- **PASS** AC: "Approve on Check stage calls mergeQueue.enqueue(), not resumePipeline()" — `issues.ts:959` calls `mergeQueue.enqueue(projectId, number)`
- **PASS** AC: "Issue mergeState set to 'pending' on enqueue" — NOTE: MergeQueue.enqueue internally sets mergeState; the approve handler does not explicitly set it before enqueue, but MergeQueue handles it
- **PASS** AC: "merge_completed event transitions issue stage to 'done'" — `server/index.ts:265-278` calls `issueRepo.updateStage(issueId, Stage.Done)`
- **FAIL** AC: "merge_failed event sets mergeState (blocked/conflict/build-failed), stage stays 'check'" — `server/index.ts:280-282` only logs the failure, does NOT call `issueRepo.setMergeState()` or update mergeState. The spec requires setting mergeState to the specific failure state.
- **PASS** AC: "MergeQueue retry still works for failed merges" — MergeQueue has existing retry mechanism
- **PASS** AC: "Plan stage approval still works (transitions to Build)" — `issues.ts:972-973` sets `nextStage = Stage.Build`
- **PASS** AC: "Typecheck passes" — confirmed

#### T-007: Update API status endpoint
- **PASS** AC: "GET /api/status issuesByStage uses 'check' key, not 'review'" — `status.ts:87` uses `'check'`
- **PASS** AC: "GET /api/issues/:number for check-stage issue with awaiting approval includes CheckSuiteOutput in approvalState.output" — The issue show endpoint at `issues.ts:197-241` returns the full issue including approvalState.output; CheckSuiteOutput is stored there by workflow-controller
- **PASS** AC: "No 'review' key in issuesByStage response" — `status.ts:83-89` has no 'review' key
- **PASS** AC: "Typecheck passes" — confirmed

#### T-008: Rename Stage.Review in frontend
- **PASS** AC: "No Stage.Review or 'review' string in frontend code (grep confirms)" — Stage enum at `web/src/lib/types.ts:1-8` has no Review; all components use Stage.Check
- **PASS** AC: "KanbanBoard column shows 'Check' label" — `KanbanBoard.tsx` uses 'Check'
- **PASS** AC: "APPROVAL_STAGES includes Stage.Check" — `IssueCard.tsx` references Stage.Check in approval stages
- **PASS** AC: "IssueDetailPage STAGES array uses Stage.Check" — `IssueDetailPage.tsx:19`
- **PASS** AC: "SessionHeader displays 'Check' for check stage" — `SessionHeader.tsx` updated
- **PASS** AC: "SessionTimeline stageOrder includes 'check'" — `SessionTimeline.tsx` updated
- **PASS** AC: "Frontend builds without errors" — `npx tsc --noEmit` in web/ produces no errors

#### T-009: Build CheckResultsPanel
- **FAIL** AC: "CheckResultsPanel renders one row per CheckResult with correct status colors" — Rows render, but check name lookup at `:163-164` uses display names `'Build & Test'`/`'AI Review'` instead of backend names `'build-test'`/`'ai-review'`, breaking all conditional logic
- **FAIL** AC: "All passed: green indicators + Approve & Merge button visible" — Works only because fallback `overallResult === 'passed'` is checked from suite output directly, not from name lookup. However, the button appears even when checks fail because `buildFailed` is always false due to name mismatch.
- **FAIL** AC: "Build failed: red indicator on build-test, pending on others, only Back to Build button" — `buildFailed` is always `false` due to name mismatch, so "Back to Build" button never appears
- **FAIL** AC: "AI Review failed: amber indicator, three action buttons" — `aiReviewFailed` is always `false` due to name mismatch, so action buttons never appear
- **FAIL** AC: "Merge Ready needs rebase: blue informational badge, non-blocking" — Badge logic at `:71` checks `check.name === 'Merge Ready'` but backend sends `'merge-ready'`, so badge never appears
- **PASS** AC: "Clicking row expands to show details (buildLog, reviewReport, merge info)" — Expandable detail section at `:118-147` works
- **PASS** AC: "Component replaces old inline review report in IssueDetailPage" — `IssueDetailPage.tsx:782-788` uses `CheckResultsPanel`
- **PASS** AC: "Frontend builds without errors" — confirmed

#### T-010: Wire CheckResultsPanel actions to API endpoints
- **PASS** AC: "Approve & Merge calls approve endpoint, issue transitions to pending mergeState" — `CheckResultsPanel.tsx:170` calls `api.approveIssue(issueNumber)`
- **PASS** AC: "Back to Build regresses issue stage from check to build" — `:179` calls `api.rejectIssue(issueNumber, message)` which maps to POST reject endpoint
- **PASS** AC: "Add Instructions opens text input, submits message to messages endpoint" — `:318-338` shows textarea, calls `api.sendMessage(issueNumber, message)`
- **PASS** AC: "Force Approve calls approve endpoint with force flag" — `:311` calls `api.approveIssue(issueNumber, { force: true })`
- **PASS** AC: "All actions show loading state and handle errors" — `isPending` states and error display at `:343-347`
- **PASS** AC: "Frontend builds without errors" — confirmed

#### T-011: Write tests for check suite
- **PASS** AC: "Tests cover all 6 scenarios above" — check-suite.test.ts covers type correctness, build-test scenarios, merge-ready scenarios, AI review scenarios, sequential execution, disabled checks
- **PASS** AC: "Build & Test pass/fail/auto-fix/timeout each have a test case" — Lines 279-388
- **PASS** AC: "Merge Ready fast-forwardable vs needs-rebase each have a test case" — Lines 399-476
- **PASS** AC: "Sequential execution test verifies Build failure stops the suite" — Lines 625-649
- **PASS** AC: "Disabled checks test verifies skipping" — Lines 753-854
- **PASS** AC: "All tests pass" — 28/28 check-suite tests pass

## Fix Suggestions

1. **[CheckResultsPanel.tsx:163-164]** Frontend check name lookup must match backend: change `'Build & Test'` to `'build-test'` and `'AI Review'` to `'ai-review'`. Alternatively, use a display name map and match on backend names.

2. **[web/src/lib/types.ts:348-358]** Remove `autoFixAttempts` from frontend `CheckResult` type, or add it to the backend `CheckResult` type and populate it in `runBuildTestCheck` to track per-attempt auto-fix progress for the UI indicator.

3. **[web/src/lib/types.ts:362]** Add `'blocked'` to `CheckSuiteOutput.overallResult` type to match backend: `overallResult: 'passed' | 'failed' | 'blocked'`.

4. **[server/index.ts:280-282]** In the `merge_failed` event handler, update the issue's `mergeState` based on the `reason` parameter, as required by the spec: `issueRepo.setMergeState(issueId, reason)` or map reason to the correct MergeState value.

5. **[tests/review-auto-fix.test.ts:221]** Update all calls from `runPipelineReviewStage` to `runPipelineCheckStage` and adapt test expectations to the new CheckSuiteOutput structure.

6. **[tests/review-merge-flow.test.ts]** Update tests to reflect MergeQueue-based approval flow instead of the old `mergeBackFn` pattern.

7. **[tests/pipeline-controller.test.ts]** Update tests to expect `check_update` events instead of old review stage event patterns.

8. **[tests/stage-auto-fix.test.ts]** Fix `parseVerdict` test expectations — `parseVerdict` returns `null` for non-matching content, not `'FAIL'`.
