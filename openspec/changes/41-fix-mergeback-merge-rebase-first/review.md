# Review Report

## Verdict: FAIL

## Dimensions

### Correctness: FAIL

**ERROR: 13 of 21 existing merge-queue tests are broken** (`tests/merge-queue.test.ts`).

Root cause: The test mock `createMockWorktreeManager()` (line 24-31) does not include `rebaseOntoMaster`. When `processItem()` calls `this.deps.worktreeManager.rebaseOntoMaster(...)`, it throws `this.deps.worktreeManager.rebaseOntoMaster is not a function`. This causes every test to fail because `processItem` enters the catch block and sets `mergeState='merging'` (the initial state set before rebase), then the catch handler in `processNext` swallows it without further state changes.

Affected tests: enqueue (1), processNext (2), build verification (2), merge conflict (2), retry (2), recoverFromDB (3), project not found (1).

**ERROR: Redundant ternary in recoverFromDB** — `merge-queue.ts:113`:
```ts
const mergeState: MergeState = issue.mergeState === 'merging' ? 'pending' : 'pending';
```
Both branches return `'pending'`. The spec says `rebasing` entries should also be reset to `pending`, but the condition only checks for `'merging'`. Since both branches are identical, `'rebasing'` and `'blocked'` entries are also reset to `'pending'` — which is correct behavior, but the dead ternary is misleading. Should be simplified to `const mergeState: MergeState = 'pending';`.

**WARNING: rebaseOntoMaster commits uncommitted files with a broad `:!openspec/changes/, :!.opencode/` exclusion** (`worktree-manager.ts:240`). If the worktree has staged changes in openspec that should not be committed, this could commit unexpected files. The intent is reasonable (commit agent leftovers before rebase), but the exclusion is not documented and could surprise users.

**WARNING: abortRebase after failed rebase** — `merge-queue.ts:211`: When rebase fails, the code calls `abortRebase()` which restores the pre-rebase state. The spec says "the worktree is left in rebase-conflict state" and "the worktree is preserved for manual intervention." But aborting the rebase means the worktree is back to its original branch state, not in rebase-conflict state. If a user inspects the worktree, they won't see the conflict markers — they'll see the original branch. This contradicts the spec scenario "Rebase conflict results in blocked state" which says the worktree should be preserved for manual intervention with conflict files visible.

### Complexity: PASS

- All functions are under 50 lines.
- `processItem()` is ~110 lines (lines 179-292) but has clear sequential phases (rebase → merge → build → cleanup → done) — borderline but acceptable.
- Cyclomatic complexity is low throughout.
- No copy-pasted code.

### Test Coverage: FAIL

**ERROR: No tests for the new rebase-first flow.** Zero test files reference `rebaseOntoMaster`, `abortRebase`, `continueRebase`, `merge_blocked`, or `merge-blocked`.

Specific gaps:
- No test for `rebaseOntoMaster()` succeeding → fast-forward merge → merged.
- No test for `rebaseOntoMaster()` failing → blocked state with conflict files.
- No test for `abortRebase()`.
- No test for `continueRebase()`.
- No test for `merge_blocked` event emission.
- No test for `GET /api/issues/merge-blocked` endpoint.
- No test for `POST /api/issues/:number/retry-merge` with `blocked` state.
- No test for `recoverFromDB` recovering `rebasing` entries.
- No test for `mergeBack` with `--ff-only` (fast-forward-only behavior).

**ERROR: 13 existing tests are broken** (see Correctness above).

### Security: PASS

- `execFile` is used (not `exec`), which prevents shell injection.
- SQL queries use parameterized placeholders (`findByMergeStates`).
- No secrets or credentials exposed.
- The `GIT_EDITOR: 'true'` env in `continueRebase` is a standard git pattern to skip editor prompts.

### Spec Compliance: FAIL

#### T-001: Add rebaseOntoMaster, abortRebase, continueRebase to WorktreeManager

| Criterion | Result | Notes |
|-----------|--------|-------|
| rebaseOntoMaster() returns {success:true, conflicts:[]} on clean rebase | PASS | |
| rebaseOntoMaster() returns {success:false, conflicts:['file1','file2']} on conflict | PASS | |
| abortRebase() restores branch to pre-rebase state | PASS | |
| continueRebase() returns success when conflicts are staged and resolved | PASS | Uses GIT_EDITOR=true to skip editor |
| continueRebase() returns new conflicts if more arise | PASS | |
| Typecheck passes | PASS | `npm run build` succeeds |
| Build passes | PASS | |

#### T-002: Simplify mergeBack to fast-forward only

| Criterion | Result | Notes |
|-----------|--------|-------|
| mergeBack() uses git merge --ff-only instead of git merge --no-edit | PASS | Line 194: `git merge --ff-only` |
| Returns {success:false} with descriptive message when fast-forward is not possible | PASS | Line 196 |
| Does NOT modify base branch on failure | PASS | Checkout happens first but merge failure leaves base branch at HEAD |
| Existing remove/cleanup after successful merge still works | PASS | |
| Typecheck passes | PASS | |
| Build passes | PASS | |

#### T-003: Update MergeState type and MergeEntry to support rebase-first flow

| Criterion | Result | Notes |
|-----------|--------|-------|
| MergeState type includes 'rebasing' and 'blocked' | PASS | `types/index.ts:47` |
| MergeEntry has optional conflictingFiles: string[] field | PASS | `merge-queue.ts:21` |
| retry() accepts entries with mergeState 'blocked' | PASS | `merge-queue.ts:84` |
| recoverFromDB() resets 'rebasing' entries to 'pending' | **WARN** | Works correctly due to dead ternary, but the code doesn't explicitly handle 'rebasing' — both branches of the ternary return 'pending' |
| Typecheck passes | PASS | |
| Build passes | PASS | |

#### T-004: Restructure MergeQueue.processItem to rebase-first flow

| Criterion | Result | Notes |
|-----------|--------|-------|
| processItem calls rebaseOntoMaster() before mergeBack() | PASS | |
| Clean rebase → fast-forward merge → build verification → merged | PASS | |
| Conflict rebase → mergeState='blocked' with conflictingFiles populated | **FAIL** | After detecting conflicts, the code calls `abortRebase()` (line 211), which discards the rebase state. The spec says "the worktree is preserved for manual intervention" — but after abort, the worktree is in its original state with no conflict markers visible. |
| merge_blocked event emitted with conflict details on blocked | PASS | |
| Build verification and rollback still work after fast-forward merge | PASS | |
| Worktree preserved on blocked state | **PARTIAL** | Worktree is preserved (not removed), but the rebase is aborted so no conflict markers remain |
| Typecheck passes | PASS | |
| Build passes | PASS | |

#### T-005: Add GET /api/issues/merge-blocked endpoint

| Criterion | Result | Notes |
|-----------|--------|-------|
| GET /api/issues/merge-blocked returns 200 with array of blocked issues | PASS | |
| Each entry includes issueNumber, title, conflictingFiles, blockedAt | PASS | |
| Returns empty array when no blocked issues exist | PASS | |
| Returns 400 when no active project context | PASS | Line 152 |
| Typecheck passes | PASS | |
| Build passes | PASS | |

#### T-006: Update POST /api/issues/:number/retry-merge to accept blocked state

| Criterion | Result | Notes |
|-----------|--------|-------|
| POST /api/issues/:number/retry-merge returns 200 for mergeState='blocked' | PASS | |
| Returns 409 for non-retryable mergeState with current state in error message | PASS | Line 1713 |
| Returns 404 for non-existent issue | PASS | Line 1697 |
| Retried issue re-enters merge queue with mergeState='pending' | PASS | |
| Typecheck passes | PASS | |
| Build passes | PASS | |

## Fix Suggestions

1. **[tests/merge-queue.test.ts:24-31]** Add `rebaseOntoMaster` mock to `createMockWorktreeManager()`:
   ```ts
   rebaseOntoMaster: vi.fn().mockResolvedValue({ success: true, conflicts: [] }),
   abortRebase: vi.fn().mockResolvedValue(undefined),
   continueRebase: vi.fn().mockResolvedValue({ success: true, conflicts: [] }),
   ```
   This will fix all 13 broken tests.

2. **[packages/cli/src/git/merge-queue.ts:211-217]** Decide whether to abort the rebase on conflict or leave the worktree in rebase-conflict state. The spec says the worktree should be preserved for manual intervention with conflict files visible. If keeping the abort, update the spec to match. If removing the abort, the user can manually resolve conflicts in the worktree and trigger retry with `continueRebase`.

3. **[packages/cli/src/git/merge-queue.ts:113]** Simplify the dead ternary:
   ```ts
   const mergeState: MergeState = 'pending';
   ```

4. **[tests/]** Add tests for:
   - `rebaseOntoMaster()` success and failure paths
   - `abortRebase()` and `continueRebase()`
   - `merge_blocked` event emission
   - `GET /api/issues/merge-blocked` endpoint
   - `POST /api/issues/:number/retry-merge` with `blocked` state
   - `recoverFromDB` recovering `rebasing` entries
   - Rebase conflict → blocked → retry → success lifecycle
