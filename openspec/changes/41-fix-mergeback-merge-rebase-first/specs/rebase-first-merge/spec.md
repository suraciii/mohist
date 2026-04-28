## ADDED Requirements

### Requirement: Rebase-first merge flow

WorktreeManager SHALL support rebasing an issue branch onto the latest base branch inside the worktree, so that conflicts are discovered and resolved in the worktree where full context is available, and the final merge on master is a guaranteed conflict-free fast-forward.

#### Scenario: Rebase succeeds with no conflicts

- **WHEN** `rebaseOntoMaster()` is called for an issue
- **THEN** the system fetches the latest `origin/<baseBranch>`
- **AND** runs `git rebase origin/<baseBranch>` inside the worktree
- **AND** returns `{ success: true, conflicts: [] }`

#### Scenario: Rebase encounters conflicts

- **WHEN** `rebaseOntoMaster()` is called for an issue
- **AND** the rebase produces merge conflicts
- **THEN** the system returns `{ success: false, conflicts: string[] }` where `conflicts` is the list of conflicted file paths
- **AND** the worktree is left in rebase-conflict state (unmerged files present)
- **AND** the issue branch is NOT advanced

#### Scenario: Rebase aborted after conflict

- **WHEN** `rebaseOntoMaster()` encounters conflicts
- **AND** the caller decides not to resolve them
- **THEN** the system SHALL provide `abortRebase()` which runs `git rebase --abort` in the worktree
- **AND** the branch is restored to its pre-rebase state

#### Scenario: Continue rebase after manual resolution

- **WHEN** conflicts were resolved in the worktree (files staged with `git add`)
- **AND** `continueRebase()` is called
- **THEN** the system runs `git rebase --continue` in the worktree
- **AND** if all conflicts are resolved, returns `{ success: true, conflicts: [] }`
- **AND** if new conflicts arise, returns `{ success: false, conflicts: string[] }`

### Requirement: MergeQueue uses rebase-first flow

MergeQueue SHALL use the rebase-first strategy: rebase the issue branch onto the latest base branch in the worktree, then perform a fast-forward merge on master. The MergeState enum SHALL include `rebasing` to track when an issue is in the rebase phase.

#### Scenario: Successful rebase followed by fast-forward merge

- **WHEN** MergeQueue processes an issue
- **THEN** it calls `rebaseOntoMaster()` first
- **AND** if rebase succeeds, it calls `mergeBack()` which SHALL use `git merge --ff-only`
- **AND** the fast-forward merge is guaranteed to succeed without conflicts
- **AND** MergeState is set to `merged`

#### Scenario: Rebase conflict results in blocked state

- **WHEN** `rebaseOntoMaster()` returns conflicts
- **THEN** MergeQueue sets MergeState to `blocked`
- **AND** records the list of conflicting files in the merge entry
- **AND** emits a `merge_blocked` event with conflict details
- **AND** the worktree is preserved for manual intervention

#### Scenario: Retry of blocked merge when master has advanced

- **WHEN** an issue is in `blocked` state
- **AND** a retry is triggered (manual)
- **THEN** MergeQueue re-runs the rebase-first flow from the beginning
- **AND** if the new master HEAD no longer conflicts with the issue's changes, the merge succeeds

### Requirement: mergeBack uses fast-forward only

The `mergeBack()` method on WorktreeManager SHALL only perform fast-forward merges. It SHALL use `git merge --ff-only` and fail if a fast-forward is not possible.

#### Scenario: Fast-forward merge succeeds

- **WHEN** `mergeBack()` is called after a successful rebase
- **AND** the issue branch is a descendant of base branch HEAD
- **THEN** the system checks out the base branch
- **AND** runs `git merge --ff-only <branch>`
- **AND** returns `{ success: true, message: "..." }`

#### Scenario: Fast-forward merge fails (should not happen after rebase)

- **WHEN** `mergeBack()` is called
- **AND** a fast-forward is not possible
- **THEN** the system returns `{ success: false, message: "..." }`
- **AND** the base branch is NOT modified
