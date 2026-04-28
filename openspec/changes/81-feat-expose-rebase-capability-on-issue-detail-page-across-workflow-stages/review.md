# Review Report

## Verdict: PASS

## Dimensions

### Correctness: PASS

- Event types in `event-bus.ts:35-38` are correctly typed with proper payload shapes matching the spec.
- `ALL_EVENT_TYPES` in `events.ts:34-37` includes all 4 rebase events.
- Precondition checks in the rebase route (`issues.ts:1811-1924`) are logically correct: stage validation, worktree existence, agent running check, done-stage delegation.
- `handleReviewRebase` (`issues.ts:1743-1757`) correctly runs build verify with 5-minute timeout and returns `boolean | undefined`.
- `handlePlanRebase` (`issues.ts:1759-1786`) correctly checks `hasPendingGate` before injecting message and resuming pipeline.
- `handleBuildRebase` (`issues.ts:1788-1809`) correctly resets task statuses and deletes checkpoint.
- Frontend `api.rebaseIssue` (`api.ts:187-196`) correctly catches `ApiError` and returns data for 409 responses so the UI can display conflicts.
- `useSSE.ts` correctly subscribes to all 4 rebase event types and invalidates queries on `rebase_completed`/`rebase_conflict`.
- `MergeStatePanel.tsx` correctly renamed all three states (build-failed, conflict, blocked) from "Retry Merge" to "Rebase and Retry" and loading text to "Rebasing and retrying...".
- IssueDetailPage rebase button logic correctly shows/hides by stage and agent state.

### Complexity: PASS

- `handleReviewRebase` (15 lines), `handlePlanRebase` (28 lines), `handleBuildRebase` (22 lines) are well-extracted helpers keeping the main rebase route handler focused.
- The rebase route handler (`issues.ts:1811-1924`, ~113 lines) is the largest function but has linear flow with clear sections.
- No copy-pasted code beyond the necessary duplicate JSX for rebase button in different approval gate positions (which is acceptable given different surrounding context).

### Test Coverage: PASS

- `tests/api-rebase.test.ts` covers 11 test cases: precondition checks (404, backlog, explore, worktree missing, agent running), done-stage delegation, done-stage retry failure, done-stage missing mergeQueue, fast-forward, conflict, and successful rebase.
- All 11 rebase API tests pass.
- TypeScript typecheck passes cleanly.

Note: 65 pre-existing test failures exist in the test suite (merge-queue-rebase, e2e, pipeline-checkpoint, etc.) — these are NOT caused by the issue-81 changes. They reference methods like `startAutoRetry` and `branchHasCommits` that appear to be from a prior incomplete refactor.

### Security: PASS

- No SQL injection risks: all database operations use parameterized queries.
- No command injection: `execFileAsync` in `handleReviewRebase` uses array arguments (not shell string interpolation).
- Input validation: issue number is parsed with `parseInt`, stage is validated against `REBASE_ALLOWED_STAGES`.
- No exposed secrets or credentials.

### Spec Compliance: PASS

#### T-001: Add rebase event types to EventBus and SSE — ALL PASS
- [PASS] EventMap includes `rebase_started` with `{ issueId, projectId, issueNumber }` — `event-bus.ts:35`
- [PASS] EventMap includes `rebase_progress` with `{ issueId, projectId, issueNumber, step }` — `event-bus.ts:36`
- [PASS] EventMap includes `rebase_completed` with `{ issueId, projectId, issueNumber, rebased }` — `event-bus.ts:37`
- [PASS] EventMap includes `rebase_conflict` with `{ issueId, projectId, issueNumber, conflicts }` — `event-bus.ts:38`
- [PASS] ALL_EVENT_TYPES includes all 4 — `events.ts:34-37`
- [PASS] Typecheck passes

#### T-002: Add POST /api/issues/:number/rebase route handler — ALL PASS
- [PASS] Returns 400 with 'Worktree not found' — `issues.ts:1850`
- [PASS] Returns 409 with 'Agent is running' — `issues.ts:1854`
- [PASS] Returns 400 with 'Rebase not available' for backlog/explore — `issues.ts:1826`
- [PASS] Returns 200 `{ rebased: false, message: 'Already up to date' }` — `issues.ts:1867`
- [PASS] Returns 200 `{ rebased: true, message: 'Rebase successful' }` — `issues.ts:1908-1920`
- [PASS] Returns 409 with conflicts on conflict — `issues.ts:1886-1890`
- [PASS] Done stage delegates to mergeQueue.retry() — `issues.ts:1833`
- [PASS] Emits rebase_started before operation — `issues.ts:1857`
- [PASS] Emits rebase_progress events for fetching and rebasing — `issues.ts:1859, 1862, 1870`
- [PASS] Emits rebase_completed on success — `issues.ts:1898`
- [PASS] Emits rebase_conflict with conflict file list — `issues.ts:1880-1885`
- [PASS] Typecheck passes

#### T-003: Add review-stage build verify after rebase — ALL PASS
- [PASS] Review stage runs build verify after rebase — `issues.ts:1894-1896`
- [PASS] Returns buildPassed: true when build succeeds — `handleReviewRebase` returns `true`
- [PASS] Returns buildPassed: false with descriptive message when build fails — `handleReviewRebase` catches error and returns `false`, message includes "build verification failed"
- [PASS] Build verify has 5-minute timeout — `issues.ts:1750`
- [PASS] Emits rebase_progress with step 'verifying' — `issues.ts:1744`
- [PASS] Typecheck passes

#### T-004: Add build-stage checkpoint clear after rebase — ALL PASS
- [PASS] Build stage clears checkpoint after rebase — `issues.ts:1904-1906` → `handleBuildRebase`
- [PASS] All tasks reset to pending status — `issues.ts:1799-1803` (sets `passes=false`, `error=null`, `attempts=0`)
- [PASS] Response message includes 'Checkpoint cleared, resume pipeline to rebuild' — `issues.ts:1915`
- [PASS] Typecheck passes

#### T-005: Add plan-stage message injection after rebase — ALL PASS
- [PASS] Plan stage injects message after successful rebase — `handlePlanRebase` at `issues.ts:1759-1786`
- [PASS] Injected message includes instruction about master having new changes — `issues.ts:1765`
- [PASS] Injected message includes instruction to check if design/tasks can leverage new code and verify file paths — `issues.ts:1765`
- [PASS] Agent session resumes after message injection — `issues.ts:1778-1785` calls `agentRunner.resumePipeline`
- [PASS] Typecheck passes

#### T-006: Add rebase method to frontend API client and SSE handler — ALL PASS
- [PASS] api.rebaseIssue(number) sends POST and returns correct type — `api.ts:187-196`
- [PASS] useSSE subscribes to all 4 rebase events — `useSSE.ts:116-119`
- [PASS] rebase_completed and rebase_conflict invalidate issues query cache — `useSSE.ts:68-72`
- [PASS] Typecheck passes

#### T-007: Rename Retry Merge to Rebase and Retry in MergeStatePanel — ALL PASS
- [PASS] build-failed state button shows 'Rebase and Retry' — `MergeStatePanel.tsx:85`
- [PASS] conflict state button shows 'Rebase and Retry' — `MergeStatePanel.tsx:111`
- [PASS] blocked state button shows 'Rebase and Retry' — `MergeStatePanel.tsx:171`
- [PASS] Loading state text is 'Rebasing and retrying...' — `MergeStatePanel.tsx:85, 111, 171`
- [PASS] Typecheck passes

#### T-008: Add Rebase onto master button to IssueDetailPage — ALL PASS
- [PASS] Button visible in plan stage when approvalState is awaiting — `IssueDetailPage.tsx:715-747` (inside `isApprovalGate` + `issue.stage === Stage.Plan`)
- [PASS] Button visible in build stage when agent is idle — `IssueDetailPage.tsx:504-524`
- [PASS] Button disabled in build stage when agent is running, with tooltip — `IssueDetailPage.tsx:508-523` (disabled + title + text)
- [PASS] Button visible in review stage when approvalState is awaiting, with hint text — `IssueDetailPage.tsx:663-696`
- [PASS] Button not visible in backlog, explore, or done stages — only shown for Plan/Build/Review
- [PASS] Clicking button calls api.rebaseIssue() and shows loading spinner — `IssueDetailPage.tsx:507-518`
- [PASS] Success message displayed when API returns rebased: true — `IssueDetailPage.tsx:161`
- [PASS] 'Already up to date' message displayed when API returns rebased: false — `IssueDetailPage.tsx:163`
- [PASS] Conflict file list displayed when API returns 409 with conflicts — `IssueDetailPage.tsx:158, 615-621`
- [PASS] Rebase result displayed in Plan stage via inline panel — `IssueDetailPage.tsx:730-745`
- [PASS] Rebase result displayed in Review stage via inline panel — `IssueDetailPage.tsx:679-694`
- [PASS] Typecheck passes

#### Warnings
- [WARNING] `issues.ts:1862` emits `rebase_progress` with `step: 'checking'` which is not in the spec's defined values (`"fetching" | "rebasing" | "verifying"` in `event-bus/spec.md`). The frontend types (`types.ts`) include `'checking'` in the union. This provides useful granularity (distinguishing fetch from fast-forward check). Recommend updating the spec to include `"checking"`.

## Fix Suggestions

1. **[WARNING] `packages/cli/src/api/issues.ts:1862`** — `rebase_progress` emits `step: 'checking'` which is not in the spec's defined values (`"fetching" | "rebasing" | "verifying"`). Either update the spec to include `"checking"` as a valid step value, or remove this emission and merge the "checking" step into "fetching".
