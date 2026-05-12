## MODIFIED Requirements

### Requirement: Files changed is the primary review view

The review surface SHALL make Files changed the default and primary view. It SHALL show the complete changed file list for the current base-vs-head worktree comparison and allow users to expand file diffs with line-numbered patch rendering.

#### Scenario: Open issue with file changes

- **WHEN** a user opens an issue whose retained worktree has file changes relative to the current base branch
- **THEN** Files changed is selected by default
- **AND** every changed file is listed with additions and deletions
- **AND** expanding a file shows its unified diff with line numbers

#### Scenario: Merged base changes do not pollute file review

- **WHEN** the issue branch has merged the base branch to stay current
- **AND** some files changed on the base branch are now identical on both base and issue branches
- **THEN** the Files changed view does not list those identical files as issue changes
- **AND** the visible file set matches only the remaining worktree differences unique to the issue branch state

#### Scenario: No file changes in retained worktree

- **WHEN** an issue worktree exists but there are no file changes relative to the current base branch
- **THEN** the Files changed view shows `No file changes yet`
