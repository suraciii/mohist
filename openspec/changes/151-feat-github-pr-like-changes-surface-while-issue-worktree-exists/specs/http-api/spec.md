## MODIFIED Requirements

### Requirement: Review APIs expose availability and complete review data

`GET /api/issues/:number/diff`, `GET /api/issues/:number/commits`, and `GET /api/issues/:number/commits/:hash/diff` SHALL return availability-aware review payloads. Expected lifecycle states such as not-started issues and removed worktrees SHALL be represented with explicit `available`, `reason`, and `message` fields rather than ambiguous empty arrays.

#### Scenario: Diff API available

- **WHEN** `GET /api/issues/:number/diff` is called for an issue with an existing worktree and accessible base/head branches
- **THEN** the response data includes `available: true` and `reason: null`
- **AND** includes `base`, `head`, `summary.filesChanged`, `summary.additions`, and `summary.deletions`
- **AND** includes complete file entries with file path, additions, deletions, binary status, and per-file unified diff content

#### Scenario: Commits API available

- **WHEN** `GET /api/issues/:number/commits` is called for an issue with an existing worktree and accessible base/head branches
- **THEN** the response data includes `available: true` and `reason: null`
- **AND** includes `base`, `head`, and a summary with commit count
- **AND** includes the complete base-to-head commit range with hash, message, author, date, files changed count, additions, deletions, and touched files

#### Scenario: Commit diff API available

- **WHEN** `GET /api/issues/:number/commits/:hash/diff` is called for a commit that belongs to the issue branch
- **THEN** the response data includes `available: true`, `reason: null`, `hash`, and unified patch diff text

#### Scenario: Review data unavailable by lifecycle

- **WHEN** review data is unavailable because the worktree is removed, the issue has not started, a branch is missing, or git fails
- **THEN** the response data includes `available: false`
- **AND** `reason` is one of `worktree_removed`, `not_started`, `branch_missing`, or `git_error`
- **AND** `message` explains the cause for display in the UI

### Requirement: Commits API preserves commit boundaries

`GET /api/issues/:number/commits` SHALL parse git output in a way that preserves commit boundaries and does not drop commits when stat output follows commit headers. The parser SHALL return the complete `base..head` range for retained worktrees.

#### Scenario: Multi-commit branch

- **WHEN** the issue branch has multiple commits relative to base
- **THEN** the commits API returns every commit in the range
- **AND** each commit's additions, deletions, and touched files are associated with the correct commit

#### Scenario: Commit with no file changes

- **WHEN** a commit has no file changes
- **THEN** the commits API still returns that commit
- **AND** reports zero file changes, additions, deletions, and an empty files list
