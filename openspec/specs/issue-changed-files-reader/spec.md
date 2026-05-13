# OpenSpec Capability: issue-changed-files-reader

### Requirement: Dedicated changed-files page provides the primary reading surface

Mohist SHALL provide a dedicated issue changed-files page at `/issue/:number/files` for reading the final base-vs-head file changes of an issue worktree. The page SHALL present issue number/title, base branch, head branch, files changed, additions, deletions, and current stage/status in a reading-focused layout.

#### Scenario: Open changed-files page

- **WHEN** a user opens `/issue/:number/files` for an issue with available diff data
- **THEN** the page shows the issue number and title
- **AND** shows base/head metadata, files changed, additions, deletions, and current stage/status
- **AND** the page is distinct from Issue Detail rather than an inline section below issue description and comments

#### Scenario: Reading-only scope

- **WHEN** a user is on the changed-files page
- **THEN** the page shows file and diff reading controls only
- **AND** it does not show approval/reject actions, review reports, line comments, or merge decision actions

### Requirement: Changed files are browsable through a directory-grouped tree

The changed-files page SHALL present changed files in a directory-grouped tree rather than a flat file button list. The tree SHALL support filtering by file path and SHALL keep the reading pane stable while the user browses files.

#### Scenario: Directory-grouped changed files

- **WHEN** an issue contains multiple changed files across nested paths
- **THEN** the left pane groups files by directory segments
- **AND** each file entry shows its path identity together with change magnitude or status metadata

#### Scenario: Filter files by path

- **WHEN** a user enters part of a file path in the file filter input
- **THEN** the tree narrows to matching files
- **AND** non-matching directories collapse or disappear from the filtered view

### Requirement: Unified diff reader supports sticky file context and expand controls

The changed-files page SHALL render unified diff by default in the reading pane. Each visible file SHALL show a sticky header with path, status, additions, and deletions, and the page SHALL support expand-all and collapse-all controls.

#### Scenario: Unified diff default

- **WHEN** a user opens the changed-files page with available file diffs
- **THEN** unified diff is the default reader mode
- **AND** line-numbered patch content is visible for expanded files

#### Scenario: Sticky file header

- **WHEN** a user scrolls through a long file diff
- **THEN** the current file keeps a sticky header showing path, status, additions, and deletions

#### Scenario: Expand and collapse all

- **WHEN** the user activates expand-all or collapse-all
- **THEN** the changed-files reader expands or collapses all eligible file diff sections consistently

### Requirement: Large diffs are protected by default

The changed-files page SHALL avoid eagerly rendering very large diffs that would disrupt reading or browser performance. Large diffs SHALL be summarized with changed-line counts and a `Render anyway` action so the user can explicitly opt in.

#### Scenario: Large diff hidden by default

- **WHEN** a changed file exceeds the reader's large-diff threshold
- **THEN** the file appears in the file list and summary counts as usual
- **AND** the diff body is replaced by a large-diff placeholder
- **AND** the placeholder shows the changed-line count and a `Render anyway` action

#### Scenario: Render large diff on demand

- **WHEN** a user activates `Render anyway` for a hidden large diff
- **THEN** that file's diff content renders in the reading pane without changing the availability status of the overall page

### Requirement: Advanced reading modes support deeper diff inspection

The changed-files page SHALL support deeper reading controls beyond the default unified diff. These controls SHALL include split diff, prev/next hunk navigation, preserved reading position, commit-scoped reading, raw patch view, full-file view with changed-line highlighting, and diff search.

#### Scenario: Split diff and hunk navigation

- **WHEN** a user switches to split diff mode and navigates by hunk
- **THEN** the reader shows side-by-side old/new content for the current file
- **AND** prev/next hunk controls move between diff hunks without leaving the page

#### Scenario: Reading position restored

- **WHEN** a user leaves the changed-files page and later returns to the same issue
- **THEN** the reader restores the prior reading position for that issue

#### Scenario: Commit-scoped reading

- **WHEN** a user chooses to inspect a specific commit from the changed-files page
- **THEN** the reader can switch to that commit's file changes within the same reading surface
- **AND** it remains a reading-only experience without review or decision controls

#### Scenario: Raw and full-file modes

- **WHEN** a user switches a file from diff mode to raw or full-file mode
- **THEN** raw mode shows copyable patch text
- **AND** full-file mode shows the whole file with changed lines highlighted

#### Scenario: Search within diff

- **WHEN** a user searches inside the visible diff content
- **THEN** the reader highlights or navigates matching diff content within the current reading surface

