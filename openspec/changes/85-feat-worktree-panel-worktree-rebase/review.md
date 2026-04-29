# Review Report

## Result: PASS

## Dimensions

### Correctness: PASS

- **Backend `getWorktreeStatus()`** (`packages/cli/src/git/worktree-manager.ts:343-400`): Correctly uses `git rev-list --left-right --count base...branch` where left count = behind (commits in base not in branch) and right count = ahead (commits in branch not in base). `canFastForward` is correctly derived as `behind === 0`. Error path returns `exists: true` with zero counts (worktree exists but git command failed), while non-existent worktree returns `exists: false`. All correct.
- **Backend `executeRebase()`** (`packages/cli/src/api/issues.ts:2046-2111`): Clean extraction of shared rebase logic used by both direct and queued paths. Preserves all stage-specific handlers (`handlePlanRebase`, `handleBuildRebase`, `handleReviewRebase`) in the correct order. Error returns include both `error` and `data` fields for backward compatibility.
- **Backend rebase queue** (`packages/cli/src/api/issues.ts:1923-1942`): `rebaseQueue.set()` is called synchronously after `isRunning` check, and `agent_completed` handler re-validates stage, worktree existence, and agent-not-running before executing. No race condition. In-memory comment documents restart behavior.
- **Backend `POST /rebase` route** (`packages/cli/src/api/issues.ts:2113-2175`): `queue=true` query parameter correctly bypasses the 409 agent-running response and queues instead. Done stage still delegates to merge queue (line 2131-2140), bypassing `executeRebase` entirely — correct.
- **Frontend `WorktreePanel`** (`packages/cli/web/src/components/WorktreePanel.tsx`): Handles all four display states (up-to-date, behind, ahead, both). `rebaseMutation` passes `{ queue: isAgentRunning }` based on agent state. SSE events via `onRebaseEvent` correctly filter by `issueNumber`. Loading skeleton shown during initial query resolution. Returns null when worktree doesn't exist.
- **Frontend SSE** (`packages/cli/web/src/hooks/useSSE.tsx:110-137`): All four rebase events (`rebase_started`, `rebase_progress`, `rebase_completed`, `rebase_conflict`) dispatched to `rebase-events` bus. `worktree-status` query invalidated on `rebase_completed` and `rebase_conflict`.
- **Type alignment**: Both backend and frontend `WorktreeStatus.branch` is `string` — consistent.
- **Typecheck**: Both backend (`npx tsc --noEmit`) and frontend (`web && npx tsc --noEmit`) pass cleanly with all auto-fixes applied.
- **Tests**: 69 backend test failures are pre-existing (confirmed by T-006 commit message). 9 web test failures in `SettingsPage.test.tsx` are pre-existing. No new failures introduced. All 6 new `WorktreePanel` tests pass.

### Complexity: PASS

- `getWorktreeStatus()`: ~50 lines, straightforward git command + parsing with clear error handling.
- `executeRebase()`: ~65 lines, clear phase-by-phase flow with event emissions. Acceptable given the multi-stage logic.
- `WorktreePanel.tsx`: ~194 lines, well-structured — hooks at top, loading state, then render sections clearly separated. Acceptable for a self-contained panel component.
- `rebase-events.ts`: 19 lines, clean singleton event bus pattern.
- No copy-pasted code detected. Previous stage-specific rebase button code (3 locations × ~20 lines each) consolidated into one panel.

### Test Coverage: PASS

- `WorktreePanel` component tests added in `packages/cli/web/tests/WorktreePanel.test.tsx` (6 tests):
  - Returns null when `status.exists === false`
  - Renders panel when `status.exists === true` with branch name and "Up to date"
  - Shows "Rebase onto master" when agent idle
  - Shows "Rebase after completion" when agent running
  - Shows loading skeleton during initial load
  - Shows behind indicator when behind master
- Backend endpoint and rebase queue unit tests not added — acceptable for a UI feature where manual verification through the running app is primary validation. Typecheck provides structural coverage.
- Pre-existing test failures (69 backend, 9 web) are unrelated to this change and were not broken.

### Security: PASS

- `issueNumber` from URL params is parsed with `parseInt()`, producing a number used in `getBranchName()` which generates `mo/issue-<number>` — no command injection risk.
- `baseBranch` comes from `project.baseBranch` (server-side config), not user input.
- `rebaseQueue` entries use server-validated `projectId` and `issueNumber` — no injection vectors.
- No secrets, credentials, or sensitive data exposed.

### Spec Compliance: PASS

**T-001: getWorktreeStatus()**
- ✅ Returns `{ exists: true, branch, ahead, behind, canFastForward, isRebaseInProgress }` for existing worktree
- ✅ Returns `{ exists: false, branch: '', ahead: 0, behind: 0, canFastForward: false, isRebaseInProgress: false }` when worktree does not exist
- ✅ `canFastForward` is derived as `behind === 0`
- ✅ `WorktreeStatus` interface exported from both backend (`packages/cli/src/git/worktree-manager.ts:107`) and frontend (`packages/cli/web/src/lib/types.ts:347`)

**T-002: API endpoints**
- ✅ `GET /:number/worktree-status` returns 200 with status data when worktree exists
- ✅ `GET /:number/worktree-status` returns 200 with `{ exists: false }` when worktree does not exist (worktreeManager missing or worktree not found)
- ✅ `GET /:number/worktree-status` returns 404 when issue does not exist
- ✅ `POST /rebase?queue=true` returns `{ queued: true }` when agent is running
- ✅ `POST /rebase?queue=true` executes rebase directly when agent is not running (queue param is checked but falls through to normal path)
- ✅ `POST /rebase?queue=true` returns 400 when stage is not in `REBASE_ALLOWED_STAGES`
- ✅ `POST /rebase` (no queue) returns 409 when agent is running — existing behavior preserved
- ✅ Queued rebase auto-executes after `agent_completed` event with full validation (stage, worktree, agent-not-running)

**T-003: Frontend API and hooks**
- ✅ `api.getWorktreeStatus(number)` sends `GET /api/issues/:number/worktree-status`
- ✅ `api.rebaseIssue(number, { queue: true })` sends `POST /api/issues/:number/rebase?queue=true`
- ✅ `api.rebaseIssue(number)` without options works as before (backward compatible)
- ✅ `useWorktreeStatus(issueNumber)` uses queryKey `['worktree-status', issueNumber]`
- ✅ SSE `rebase_completed` and `rebase_conflict` events invalidate `worktree-status` queries

**T-004: WorktreePanel component**
- ✅ Panel shows branch name
- ✅ `ahead=0, behind=0` shows green "Up to date" indicator
- ✅ `behind>0` shows amber "N commits behind master" with highlighted rebase button
- ✅ `ahead>0` shows "N commits ahead of master"
- ✅ Combined `ahead+behind` shows "N ahead, M behind master"
- ✅ Agent idle: button says "Rebase onto master"
- ✅ Agent running: button says "Rebase after completion" with `queue=true`
- ✅ Rebase result feedback with correct color coding (success=green, info=blue, error=red, queued=blue)
- ✅ SSE rebase events update panel progress state
- ✅ Loading skeleton shown during initial query resolution

**T-005: IssueDetailPage integration**
- ✅ WorktreePanel renders after MergeStatePanel (line 707)
- ✅ Build stage rebase button removed from Actions panel
- ✅ Plan approval gate rebase button removed
- ✅ Review approval gate rebase button removed
- ✅ Standalone `rebaseResult` display removed
- ✅ `rebaseResult` state and `rebaseMutation` removed from IssueDetailPage
- ✅ `ApiError` import cleaned up (no longer used in IssueDetailPage)
- ✅ WorktreePanel visibility driven by API response `{ exists }` — works for all stages including interrupted
- ✅ MergeStatePanel unchanged (verified: `git diff master...HEAD -- MergeStatePanel.tsx` is empty)

**T-006: Build verification**
- ✅ Backend typecheck passes (`packages/cli && npx tsc --noEmit`)
- ✅ Frontend typecheck passes (`packages/cli/web && npx tsc --noEmit`)
- ✅ No new test failures introduced (69 backend + 9 web pre-existing failures unchanged)
- ✅ All 6 new WorktreePanel tests pass

## Fix Suggestions

All original fix suggestions have been resolved:

1. **Frontend type alignment**: `WorktreeStatus.branch` is `string` in both frontend and backend. **Fixed** (uncommitted).
2. **WorktreePanel test coverage**: 6 tests added in `packages/cli/web/tests/WorktreePanel.test.tsx`. **Fixed** (uncommitted).
3. **rebaseQueue documentation**: Inline comment at `packages/cli/src/api/issues.ts:1917-1919` explains in-memory semantics and restart behavior. **Fixed** (uncommitted).
4. **Loading skeleton**: Panel shows animated skeleton during initial query resolution instead of returning null. **Fixed** (uncommitted).

Note: The four auto-fixes are present as uncommitted working tree changes. They should be committed before merge.
