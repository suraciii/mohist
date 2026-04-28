## Context

The current `MergeQueue.processItem()` calls `WorktreeManager.mergeBack()` which does a direct `git merge <branch> --no-edit` on master (worktree-manager.ts:224). When multiple issues run in parallel on overlapping files, later merges produce conflicts on master where the agent has no worktree context to resolve them. The agent retry loop (up to 3 times via `reverse-merge → agent → mergeBack`) uses fresh sessions each time with no memory, leading to silent `blocked` states.

Key files today:
- `worktree-manager.ts` — `mergeBack()` at line 167 (three-way merge on master)
- `merge-queue.ts` — `processItem()` at line 178 (calls mergeBack directly)
- `types/index.ts:47` — `MergeState = 'pending' | 'merging' | 'merged' | 'build-failed' | 'conflict'`
- `server/index.ts:156` — `agent_completed` handler enqueues into MergeQueue

## Goals / Non-Goals

**Goals:**
- Move conflict detection from merge-on-master to rebase-in-worktree so agent has full file context
- Guarantee final merge on master is always a clean fast-forward (zero conflict)
- Preserve worktree on blocked state so users can manually intervene
- Add `blocked` mergeState to retry acceptance in the existing `retry()` method

**Non-Goals:**
- No changes to pipeline stages (Explore → Plan → Build → Review → Done)
- No changes to agent conflict resolution max retry count (3)
- No auto-retry timer for blocked issues (manual retry via API only, for now)
- No cross-issue dependency tracking
- No Kanban UI changes (covered by #40)

## Decisions

### D1: Rebase in worktree, not on master

Run `git rebase origin/<baseBranch>` inside the issue worktree, not on the main repo. Conflicts surface in the worktree where the agent has full context (source files, tests, project structure).

**Alternatives considered:**
- *Reverse-merge (current approach)*: Merge master into worktree branch. Still produces three-way merges, agent context is partial.
- *Cherry-pick onto new branch*: Loses original commit history, more complex cleanup.

### D2: Rebase conflicts result in blocked state (agent loop deferred)

When rebase conflicts, MergeQueue aborts the rebase and sets `mergeState = 'blocked'` with conflicting files recorded. The agent conflict resolution loop (continueRebase with agent) is deferred to a follow-up change. For now, blocked issues can be retried manually via API — master may have advanced and the retry succeeds cleanly.

The `continueRebase()` and `abortRebase()` methods on WorktreeManager are still implemented now (T-001) so the agent loop can be added later without WorktreeManager changes.

**Alternatives considered:**
- *Full pipeline re-run*: Set issue back to `build` stage and run full pipeline. Wasteful — only the conflict files need attention, not a full Build/Review cycle.
- *Agent loop in this change*: Increases scope significantly. The core value (rebase-first + blocked state) is deliverable without it.

### D3: mergeBack simplified to fast-forward only

After successful rebase, `mergeBack()` only needs `git checkout <baseBranch> && git merge --ff-only <branch>`. This is guaranteed to succeed because the rebase placed the branch tip ahead of base HEAD. The stash/uncommitted-changes logic in current `mergeBack()` (lines 180-237) is removed — those concerns move to `rebaseOntoMaster()`.

**Alternatives considered:**
- *Keep three-way merge as fallback*: Defeats the purpose — if rebase succeeded, fast-forward always works. A fallback would mask bugs.

### D4: MergeState gains `rebasing` and `blocked` values

Current: `'pending' | 'merging' | 'merged' | 'build-failed' | 'conflict'`
New: `'pending' | 'rebasing' | 'merging' | 'merged' | 'build-failed' | 'conflict' | 'blocked'`

`rebasing` = reserved for future agent conflict resolution loop (not actively set yet)
`blocked` = rebase conflicts detected, worktree preserved, needs manual intervention or retry

The existing `conflict` state is kept for backward compatibility with DB entries but no longer set by new code (replaced by `blocked`). The `retry()` method now accepts `blocked` in addition to `build-failed` and `conflict`.

**Alternatives considered:**
- *Reuse `conflict` for blocked*: Ambiguous — `conflict` meant "conflict detected during merge", `blocked` means "agent gave up after retries". Different user actions needed.
- *Remove `conflict` entirely*: Breaking change for existing DB entries. Keep for migration.

### D5: MergeEntry gains `conflictingFiles` field

To surface conflict info to users, `MergeEntry` gains `conflictingFiles?: string[]` populated when rebase reports conflicts. This data is returned by the `GET /api/issues/merge-blocked` endpoint and stored in the in-memory queue entry.

Conflict files are NOT persisted to DB (no schema change needed). If the server restarts, blocked issues are recovered via `recoverFromDB()` but conflict file lists are lost — the user can re-trigger retry to get fresh conflict info.

## Risks / Trade-offs

- **[Rebase rewrites history]** → Acceptable because `mo/issue-N` branches are local-only, never pushed to remote. No one else depends on these branch refs.
- **[Agent resolves conflict incorrectly]** → Build verification after fast-forward merge catches this. If build fails, `git reset --hard HEAD~1` rolls back (existing rollback logic).
- **[Server crash mid-rebase]** → MergeQueue aborts rebase before setting blocked. If crash happens during abort, worktree may be left in rebase state. On restart, `recoverFromDB()` treats the entry as `pending` and re-runs the full rebase-first flow (which will detect and handle any leftover rebase state).
- **[Conflict files not persisted across restart]** → Acceptable trade-off to avoid DB schema change. Users can retry to get fresh conflict info.
- **[No auto-retry timer]** → Manual-only for now. Auto-retry when master advances is a future enhancement (mentioned in issue as "次要" scope).

## Migration Plan

1. **DB migration**: `MergeState` type is a TypeScript union, stored as TEXT in SQLite. New values (`rebasing`, `blocked`) are additive — no schema migration needed. Existing rows with `mergeState = 'conflict'` continue to work; `retry()` accepts them.
2. **Deploy**: Single deploy. After deploy, any issue completing will use the new rebase-first flow. Issues already in `pending`/`merging` states are recovered by `recoverFromDB()` and processed with the new flow.
3. **Rollback**: Revert the code. `mergeBack()` returns to three-way merge. `blocked` mergeState entries in DB are not recognized → treated as terminal failure (no retry available without manual DB update). This is acceptable — rollback is only needed if the new flow has bugs.

## Open Questions

- Should the agent conflict-resolution prompt be specialized (only resolve conflicts) or reuse the existing Build-stage prompt? — Leaning toward a specialized conflict-resolution prompt for efficiency, but this can be decided during implementation.
