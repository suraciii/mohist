# Review Report

## Result: PASS

## Dimensions

### Correctness: PASS

**All three original bugs are fixed:**

1. **`activeAgents` race condition — FIXED**: `onMergeConflictFn` no longer calls `startPipeline` directly. Instead, it sets `conflictResolutionInitiated = true` and `deferredRestartWorktreePath` (agent-runner-service.ts:524-525). After `pipeline.run()` returns, line 555 checks `conflictResolutionInitiated` and skips the error path (which would have overwritten state). In the `finally` block, after `activeAgents.delete` at line 636, the deferred restart executes at lines 639-656. This correctly avoids the race condition — `startPipeline` now runs only after the old agent is cleaned up.

2. **Error path overwriting state — FIXED**: The `conflictResolutionInitiated` check at line 555 causes `executePipeline` to skip the error path (`!result.completed && !result.gateRequired`) when conflict resolution was initiated. The `Build/Active/Resolving` state set by `onMergeConflictFn` is preserved.

3. **Redundant `updateStage(Done)` removed**: `workflow-controller.ts` no longer has the post-loop `updateStage(Done)` call. The only path to post-loop code is through the Review handler's `break` after setting `Stage.Done`, so the call was truly redundant.

4. **Backward compatibility fixed**: When `isApproved=true` or `isResolving=true` without `mergeBackFn`, both cases now skip directly to Done (workflow-controller.ts:376-383). This matches the old behavior where the approve API set `nextStage=Done`.

**Edge case in `onMergeConflictFn` early returns**: When `onMergeConflictFn` returns early (project not found, worktree not found, max retries), `conflictResolutionInitiated` stays `false`, so the error path at line 559 runs. This sets `approvalState.status='error'` and `status=Blocked`, which is acceptable — the issue is stuck and needs manual intervention regardless. No state inconsistency.

### Complexity: PASS

- The deferred restart pattern adds ~20 lines to `executePipeline` (flag variables + finally-block logic), which is a clean, idiomatic solution.
- The backward-compat fix simplifies the `no mergeBackFn` path from ~25 lines to 7 lines (workflow-controller.ts:376-383).
- `onMergeConflictFn` remains at ~83 lines with clear early-return structure.

### Test Coverage: PASS

- All 12 tests in `review-merge-flow.test.ts` pass, including the updated backward-compat test.
- Pre-existing test `pipeline-controller.test.ts:278` was updated to remove the assertion on the now-removed redundant `updateStage(Done)` call. This is correct — when an issue starts at `Stage.Done`, `updateStage` should not be called (stage is already Done).
- Total: 48 pre-existing failures (same on base commit), no new failures introduced.

### Security: PASS

- No changes from previous review. No new external inputs, injection vectors, or secrets exposed.

### Spec Compliance: PASS

**T-001 — Acceptance Criteria:**

| # | Criterion | Status | Notes |
|---|-----------|--------|-------|
| 1 | WorkflowControllerOptions contains optional mergeBackFn and onMergeConflictFn fields | PASS | Lines 51-52 |
| 2 | Review handler detects approvalState.status=approved, skips review agent, executes mergeBackFn | PASS | Line 375, 388 |
| 3 | Review handler detects mergeState=Resolving, skips review agent and approval, executes mergeBackFn | PASS | Line 373, 375, 388 |
| 4 | mergeBack success → setMergeState(Merged) + stage Done | PASS | Lines 393-394 |
| 5 | mergeBack failure → calls onMergeConflictFn, otherwise Blocked | PASS | Lines 403-443 |
| 6 | No mergeBackFn → Review handler behavior unchanged (backward compatible) | PASS | Both `isApproved` and `isResolving` without `mergeBackFn` skip to Done (lines 376-383). Matches old behavior where approve API set `nextStage=Done`. |
| 7 | Build succeeds | PASS | |

**T-002 — Acceptance Criteria:**

| # | Criterion | Status | Notes |
|---|-----------|--------|-------|
| 1 | approvalState.status set to approved | PASS | Line 878 |
| 2 | No updateStage(Done) for Review approval | PASS | Lines 883-886 |
| 3 | Issue stage stays at review | PASS | nextStage undefined, updateStage not called |
| 4 | Plan approval behavior unchanged | PASS | nextStage=Build still set |
| 5 | resumePipeline called | PASS | Line 911 |
| 6 | Build succeeds | PASS | |

**T-003 — Acceptance Criteria:**

| # | Criterion | Status | Notes |
|---|-----------|--------|-------|
| 1 | mergeBackFn injected | PASS | Line 435 |
| 2 | mergeBackFn binds project path/name/baseBranch | PASS | Lines 436-441 |
| 3 | onMergeConflictFn executes reverse merge + stage Build + Resolving | PASS | Lines 457-503 set state, lines 524-525 set deferred flag, finally block at 639-656 restarts pipeline after cleanup |
| 4 | Retry count check (max 3) | PASS | Lines 483-500 |
| 5 | Worktree not found safe handling | PASS | Lines 451-455 |
| 6 | No mergeBackFn when worktreeManager not configured | PASS | Line 434 guard |
| 7 | Build succeeds | PASS | |

**T-004 — Acceptance Criteria:**

| # | Criterion | Status | Notes |
|---|-----------|--------|-------|
| 1 | No worktreeManager.mergeBack call | PASS | Handler is just a log line |
| 2 | No mergeMasterInWorktree call | PASS | |
| 3 | No conflictRetryCount update | PASS | |
| 4 | No startPipeline call | PASS | |
| 5 | No stage Done→Build rollback | PASS | |
| 6 | No mergeState update | PASS | |
| 7 | Build succeeds | PASS | |

**T-005 — Acceptance Criteria:**

| # | Criterion | Status | Notes |
|---|-----------|--------|-------|
| 1 | Tests default path (review agent + gate) | PASS | |
| 2 | Tests approved + mergeBackFn skip | PASS | |
| 3 | Tests mergeBack success → Merged + Done | PASS | |
| 4 | Tests mergeBack failure → onMergeConflictFn | PASS | |
| 5 | Tests Resolving + mergeBackFn skip | PASS | |
| 6 | Tests no mergeBackFn backward compat | PASS | Updated test correctly expects skip-to-Done for both approved and resolving cases |

**T-006 — Acceptance Criteria:**

| # | Criterion | Status | Notes |
|---|-----------|--------|-------|
| 1 | npm run build succeeds | PASS | |
| 2 | npm test all pass | PASS* | 48 pre-existing failures (same on base commit), no new failures introduced |
| 3 | No stage-related type errors | PASS | |
