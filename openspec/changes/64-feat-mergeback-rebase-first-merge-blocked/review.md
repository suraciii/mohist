# Review Report

## Verdict: PASS

## Dimensions

### Correctness: PASS

- **[FIXED] pickNext overlap logic** (`merge-queue.ts:356`): Now returns `candidate` (earlier FIFO entry) when overlap detected, matching spec "按入队顺序先处理 issue A".
- **[FIXED] Existing tests** (`merge-queue.test.ts`): Mock updated with `rebaseOntoMaster` and `getPath`. `setupMockExecFile()` helper returns `{ stdout, stderr }` objects for `promisify` compatibility and handles `git log` to make `branchHasCommits` return true. 21/21 tests pass.
- **[FIXED] handleFailure infrastructure errors** (`merge-queue.ts:389,430`): `'Project not found'` and `'Worktree not found'` now explicitly pass `'conflict'` state instead of defaulting to `'build-failed'`, allowing auto-retry.
- **[FIXED] processNext race condition** (`merge-queue.ts:300`): `this.processing = true` moved before `await pickNext()` to prevent concurrent `enqueue` calls from selecting the same entry.
- **[WARNING] recoverFromDB loses retryCount**: By-design per spec notes ("retryCount is memory-only"). After restart, retry counter resets.

### Complexity: PASS

- No change from original review.

### Test Coverage: PASS

- **[FIXED] Existing test suite**: 21/21 tests in `merge-queue.test.ts` pass.
- **[FIXED] New test suite**: 18/18 tests in `merge-queue-rebase.test.ts` pass.
- **[WARNING] Missing test: auto-commit before rebase**: Not covered in tests (low risk — code path is straightforward).
- **[WARNING] Missing test: build failure rollback after ff-merge**: Not covered in new test file (covered in old test file via mock).

### Security: PASS

- No change from original review.

### Spec Compliance: PASS

**T-001**: All criteria PASS.
**T-002**: All criteria PASS.
**T-003**: All criteria PASS.
**T-004**: All criteria PASS — overlap logic now returns FIFO candidate (earlier entry).
**T-005**: All criteria PASS.
**T-006**: All criteria PASS.
**T-007**: All criteria PASS — all 39 merge-queue tests pass.

**event-bus spec — rebase_retry**: **PASS** — EventMap field renamed to `attempt`, emit call uses `attempt: entry.retryCount`.
**event-bus spec — merge_blocked**: **PASS** — EventMap now includes `reason`, `retryCount`, `lastConflict` fields.
**event-bus spec — ALL_EVENT_TYPES**: PASS.
**event-bus spec — SSE heartbeat**: PASS.
