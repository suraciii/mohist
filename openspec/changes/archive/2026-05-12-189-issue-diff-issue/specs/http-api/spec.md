## MODIFIED Requirements

### Requirement: Review APIs expose availability and complete review data

`GET /api/issues/:number/diff`, `GET /api/issues/:number/commits`, and `GET /api/issues/:number/commits/:hash/diff` SHALL return availability-aware review payloads. For issue-level file review, `GET /api/issues/:number/diff` SHALL compare the current base branch worktree to the current issue branch worktree, rather than diffing from the historical merge-base.

#### Scenario: Diff API available

- **WHEN** `GET /api/issues/:number/diff` is called for an issue with an existing worktree and accessible base/head branches
- **THEN** the response data includes `available: true` and `reason: null`
- **AND** includes `base`, `head`, `summary.filesChanged`, `summary.additions`, and `summary.deletions`
- **AND** includes complete file entries with file path, additions, deletions, binary status, and per-file unified diff content

#### Scenario: Diff excludes merged base-branch changes

- **WHEN** an issue branch has previously merged the base branch and therefore contains commits already present on the current base branch
- **THEN** `GET /api/issues/:number/diff` compares `base` vs `head` directly
- **AND** files whose content is already the same on both branches are not reported as issue changes
- **AND** the returned `summary` and per-file patch content reflect only the remaining worktree differences between base and head

#### Scenario: Review data unavailable by lifecycle

- **WHEN** review data is unavailable because the worktree is removed, the issue has not started, a branch is missing, or git fails
- **THEN** the response data includes `available: false`
- **AND** `reason` is one of `worktree_removed`, `not_started`, `branch_missing`, or `git_error`
- **AND** `message` explains the cause for display in the UI
