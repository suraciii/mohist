## MODIFIED Requirements

### Requirement: Files changed is the primary review view

The primary Files changed reading experience SHALL live on the dedicated changed-files page instead of depending on the inline Issue Detail changes section as the main reading surface. It SHALL show the complete changed file list for the current base-vs-head worktree comparison and allow users to inspect line-numbered diffs without unrelated issue-management noise.

#### Scenario: Open issue with file changes

- **WHEN** a user opens the changed-files page for an issue whose retained worktree has file changes relative to the current base branch
- **THEN** Files changed is the primary view
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

### Requirement: File review remains distinct from review decisions

The changed-files experience SHALL stay focused on reading final file changes and SHALL NOT expand into a review decision surface. Browsing files, viewing diffs, and switching reading modes are in scope; approval, rejection, review reports, merge actions, and line comments are not.

#### Scenario: Reading without decisions

- **WHEN** a user reads code on the changed-files page
- **THEN** the surface provides file browsing and diff reading controls
- **AND** it does not provide review comments, approval/reject actions, merge actions, or AI review report panes
