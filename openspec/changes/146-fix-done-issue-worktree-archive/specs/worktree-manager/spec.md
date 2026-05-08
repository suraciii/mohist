## MODIFIED Requirements

### Requirement: REQ-WT-001 Done issues retain worktrees until archive

WorktreeManager cleanup SHALL NOT be part of successful merge completion. Successful merge SHALL leave the issue worktree available for inspection until the user archives the issue.

#### Scenario: Merge queue success retains worktree
- **GIVEN** an issue has a worktree
- **WHEN** the merge queue successfully merges the issue branch into the base branch
- **THEN** the issue reaches Done with `mergeState=merged`
- **AND** the issue worktree still exists
- **AND** `WorktreeManager.remove()` is not called by the merge queue success path

#### Scenario: Manual merge success retains worktree
- **GIVEN** an issue has a worktree
- **WHEN** `POST /api/issues/:number/merge` successfully merges the branch
- **THEN** the API returns success
- **AND** the issue worktree still exists
- **AND** `WorktreeManager.remove()` is not called by the manual merge path

### Requirement: REQ-WT-002 Archive cleanup removes retained worktrees

Issue archive cleanup SHALL remove retained issue worktrees by default. If archive cleanup is explicitly disabled, the issue SHALL be marked archived while leaving local transient state intact.

#### Scenario: Archive removes retained worktree by default
- **GIVEN** a Done issue has a retained worktree
- **WHEN** the user archives the issue without disabling cleanup
- **THEN** the issue is marked archived
- **AND** the retained worktree is removed

#### Scenario: Archive with cleanup disabled retains worktree
- **GIVEN** an issue has a retained worktree
- **WHEN** the user archives the issue with cleanup disabled
- **THEN** the issue is marked archived
- **AND** the retained worktree still exists
