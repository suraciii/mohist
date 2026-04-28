## Why

When multiple issues run in parallel and touch overlapping files, the current mergeBack does a direct `git merge` on master which produces conflicts that agents cannot reliably resolve — they lack worktree context and each retry is a fresh session. This results in issues silently stuck at `mergeState=Blocked` with unmerged commits, while the Kanban shows them as completed.

## What Changes

- Replace the mergeBack flow from "merge directly on master" to "rebase worktree onto master first, then fast-forward merge"
- `WorktreeManager` gains a `rebaseOntoMaster()` method that rebases the issue branch onto the latest base branch inside the worktree, returning conflict info
- `MergeQueue` processItem restructured: rebase in worktree → (if conflicts, agent resolves with full context) → fast-forward merge on master (guaranteed conflict-free)
- Agent conflict resolution retries now happen during rebase in the worktree, not during merge on master
- MergeState enum gains `rebasing` and `blocked` states; `blocked` is the terminal failure state when rebase conflicts cannot be resolved
- On `blocked`, system records conflicting files and allows manual intervention or auto-retry when master advances

## Capabilities

### New Capabilities

- `rebase-first-merge`: WorktreeManager rebase + fast-forward merge flow — rebase issue branch onto latest base branch in worktree (agent resolves conflicts with full context), then fast-forward merge on master (zero-conflict guarantee)

### Modified Capabilities

- `worktree-manager`: Add `rebaseOntoMaster()` method; `mergeBack()` simplified to fast-forward only (`git merge --ff-only`)
- `http-api`: Add endpoint to list blocked issues with conflict details; add endpoint to trigger manual re-retry of blocked merge

## Impact

- `packages/cli/src/types/index.ts` — MergeState enum gains `rebasing` and `blocked`
- `packages/cli/src/git/worktree-manager.ts` — new `rebaseOntoMaster()`, simplified `mergeBack()`
- `packages/cli/src/git/merge-queue.ts` — processItem restructured to rebase-first flow
- `packages/cli/src/api/issues.ts` — new `merge-blocked` endpoint, updated `retry-merge` to accept `blocked`
