## Why

When multiple issues run in parallel and touch overlapping files, the current `mergeBack()` does a direct `git merge` on master which produces conflicts that agents cannot reliably resolve — they lack worktree context and each retry is a fresh session. This results in issues stuck at `mergeState=Blocked` with unmerged commits, while the Kanban shows them as completed. Recent incidents (#20, #34, #38 all touching IssueCard.tsx/types.ts) caused 6 simultaneous blocked issues requiring manual resolution.

## What Changes

- Replace `mergeBack()` flow with rebase-first strategy: rebase issue branch onto latest master **inside the worktree** (where full file context is available), then fast-forward merge into master (guaranteed zero conflicts)
- Add `rebaseOntoMaster()`, `abortRebase()`, `continueRebase()` methods to WorktreeManager
- Add `Rebasing` state to `MergeState` type: `'pending' | 'rebasing' | 'merging' | 'merged' | 'build-failed' | 'conflict' | 'blocked'`
- Update `MergeQueue.processItem()` to call rebase before merge, with conflict detection and structured error reporting
- Add auto-retry mechanism: when `mergeState=blocked`, periodically check for new master commits and re-attempt rebase
- Add conflict-aware merge ordering: before processing, check file overlap between queued issues and serialize overlapping ones

## Capabilities

### New Capabilities

- `rebase-first-merge`: Rebase issue branch onto master inside worktree before merging, ensuring conflict resolution happens where full file context is available, and final merge is a zero-conflict fast-forward
- `blocked-auto-retry`: Periodic background check that auto-retries blocked merges when new commits land on master, reducing manual intervention
- `conflict-aware-merge-ordering`: Pre-merge file overlap detection that serializes conflicting issues, preventing concurrent merge collisions

### Modified Capabilities

- `worktree-manager`: Add `rebaseOntoMaster()`, `abortRebase()`, `continueRebase()` methods; `mergeBack()` simplified to fast-forward only after successful rebase
- `event-bus`: Add rebase lifecycle events (`rebase_started`, `rebase_completed`, `rebase_conflict`, `rebase_retry`)

## Impact

- `packages/cli/src/git/worktree-manager.ts` — rebase methods, simplified mergeBack
- `packages/cli/src/git/merge-queue.ts` — rebase-first flow, auto-retry timer, conflict-aware ordering
- `packages/cli/src/types/index.ts` — MergeState enum extended with `rebasing`, `blocked`
- `packages/cli/src/services/event-bus.ts` — rebase events
- `packages/cli/src/db/issue-repo.ts` — queries for blocked issues with retry eligibility
- `packages/cli/src/server/index.ts` — auto-retry scheduler startup
