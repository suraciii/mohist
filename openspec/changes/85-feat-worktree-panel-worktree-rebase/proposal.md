## Why

Rebase buttons are scattered across stage-specific conditions (Plan inside approval gate, Build as standalone button, Review inside approval gate, Done in MergeStatePanel), and the `interrupted` status has no rebase access at all. Users cannot see the worktree's actual git state (ahead/behind master, whether up-to-date), preventing informed decisions about when to rebase. Rebase is a worktree operation, not a stage operation — it deserves a dedicated panel.

## What Changes

- Add `GET /api/issues/:number/worktree-status` endpoint returning `{ exists, branch, ahead, behind, canFastForward, isRebaseInProgress }`
- Add queue parameter to `POST /api/issues/:number/rebase`: when agent is running, queue the rebase to execute automatically after agent completes (returns `{ queued: true }`)
- Add WorktreePanel component in IssueDetailPage sidebar (between Details and Actions panels), visible whenever worktree exists (stage not Backlog/Explore/Done-merged)
- WorktreePanel shows: branch name, ahead/behind counts, up-to-date indicator, rebase button with queue-mode support, rebase result feedback
- Remove all stage-specific rebase buttons: Plan approval gate rebase (`IssueDetailPage.tsx:850-882`), Build standalone rebase (`IssueDetailPage.tsx:639-659`), Review approval gate rebase (`IssueDetailPage.tsx:798-831`)
- Keep MergeStatePanel "Rebase and Retry" unchanged (Done stage merge flow)
- Rebase post-behavior per stage unchanged (Plan: re-evaluate, Build: clear checkpoint, Review: build verify, Done: merge queue retry)

## Capabilities

### New Capabilities

- `worktree-panel` — UI panel showing worktree git status (ahead/behind/up-to-date) and unified rebase action with agent-completion queue support

### Modified Capabilities

- `worktree-manager` — add `getWorktreeStatus()` method returning ahead/behind/canFastForward via `git rev-list --left-right --count`
- `http-api` — add `GET /api/issues/:number/worktree-status` endpoint; extend `POST /api/issues/:number/rebase` with queue parameter
- `web-ui` — add WorktreePanel component; remove stage-specific rebase buttons from IssueDetailPage and approval gate sections

## Impact

- **packages/cli/src/git/worktree-manager.ts**: New `getWorktreeStatus()` method
- **packages/cli/src/api/issues.ts**: New worktree-status route; modified rebase route with queue support
- **packages/cli/src/services/**: Rebase queue logic (detect agent completion, trigger queued rebase)
- **packages/cli/web/src/components/IssueDetailPage.tsx**: Add WorktreePanel; remove 3 stage-specific rebase button blocks
- **packages/cli/web/src/components/WorktreePanel.tsx**: New component
- **packages/cli/web/src/hooks/useQueries.ts**: New `useWorktreeStatus` hook
- **packages/cli/web/src/api.ts**: New `getWorktreeStatus` and modified `rebaseIssue` methods
- **packages/cli/web/src/components/MergeStatePanel.tsx**: No changes (kept as-is)
