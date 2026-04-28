## Why

The merge flow has two competing paths — `agent_completed` handler in server does direct `mergeBack` + reverse-merge (`mergeMasterInWorktree`) into worktree then re-enters the pipeline for conflict resolution, while `MergeQueue` does rebase-first but immediately blocks on conflict with no agent resolution. This dual-path design causes unpredictable behavior: issues get stuck at `Blocked` or `Resolving` depending on which path they hit, conflict resolution re-enters the full pipeline cycle (build → review → done) unnecessarily, and the reverse-merge approach leaves non-FF merge commits on master.

## What Changes

- **BREAKING**: Remove dual merge paths. `agent_completed` handler only enqueues into MergeQueue — no direct merge logic.
- **BREAKING**: Replace reverse-merge (`mergeMasterInWorktree`) with rebase-first strategy. No non-FF merges to master, ever.
- Add `canFastForward()` to WorktreeManager — check if branch is linear ahead of master before attempting merge.
- Add `rebaseContinue()` to WorktreeManager — allow agent to continue rebase after resolving conflicts.
- Add `abortOnConflict: false` option to `rebaseOntoMaster()` — leave conflict markers in worktree for agent resolution.
- Remove `mergeMasterInWorktree()` — dead code after rebase-first.
- Simplify `MergeQueue.processItem()` to single path: check FF → if FF, merge → if not FF, rebase with `abortOnConflict: false` → on conflict, delegate to `resolveConflicts` callback → agent fixes markers and `git rebase --continue` → retry FF → build verify.
- Remove `runConflictResolutionStage()` from WorkflowController — agent conflict resolution is a direct ACP session, not a pipeline stage.
- Add `resolveConflicts` delegate to `MergeQueueDeps` — server injects the implementation (direct ACP session with conflict resolution prompt).
- Simplify `recoverFromDB()` — add `Resolving` to active states.
- Add 3 events: `agent_conflict_resolution_started`, `agent_conflict_resolution_completed`, `agent_conflict_resolution_failed`.
- Update MergeStatePanel UI to show rebasing/resolving/blocked states.

## Capabilities

### New Capabilities

- `rebase-conflict-resolution` — agent resolves rebase conflict markers via direct ACP session, then `git rebase --continue`. Not a pipeline stage. Single attempt, fail → Blocked.

### Modified Capabilities

- `worktree-manager` — add `canFastForward()`, `rebaseContinue()`, `abortOnConflict` option on `rebaseOntoMaster()`. Remove `mergeMasterInWorktree()`.
- `event-bus` — add `agent_conflict_resolution_started`, `agent_conflict_resolution_completed`, `agent_conflict_resolution_failed` events.

## Impact

- **packages/cli/src/git/merge-queue.ts**: Rewritten `processItem()` — single rebase-first path with FF check, resolveConflicts delegate.
- **packages/cli/src/git/worktree-manager.ts**: Add `canFastForward()`, `rebaseContinue()`, remove `mergeMasterInWorktree()`.
- **packages/cli/src/server/index.ts**: `agent_completed` handler simplified to `mergeQueue.enqueue()` only. Add `resolveConflicts` callback implementation (direct ACP session).
- **packages/cli/src/workflow/workflow-controller.ts**: Remove `runConflictResolutionStage()`, remove `MergeState.Resolving` dispatch in `runPipelineBuildStage()`.
- **packages/cli/src/services/event-bus.ts**: Add 3 new event types.
- **packages/cli/src/types/index.ts**: MergeState enum unchanged (already has `Resolving`).
- **packages/cli/src/api/issues.ts**: Remove blocked-issue auto-retry if present.
- **Web UI**: MergeStatePanel updates for rebasing/resolving/blocked states.
- **Tests**: Remove `mergeMasterInWorktree` tests, add `canFastForward`/`rebaseContinue`/`resolveConflicts` tests.
