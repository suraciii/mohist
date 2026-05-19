## MODIFIED Requirements

### Requirement: Dedicated changed-files page provides the primary reading surface

Mohist SHALL provide a dedicated issue changed-files page at `/issue/:number/files` for reading the merge-base-to-head file changes of an issue worktree. The page SHALL present issue number/title, base branch, head branch, files changed, additions, deletions, and current stage/status in a reading-focused layout. The page SHALL render a usable loading, content, empty, unavailable, or recoverable error state for direct URL loads, browser refreshes, and SPA navigation instead of leaving the React root blank.

#### Scenario: Open changed-files page

- **WHEN** a user opens `/issue/:number/files` for an issue with available diff data
- **THEN** the page shows the issue number and title
- **AND** shows base/head metadata, files changed, additions, deletions, and current stage/status
- **AND** the page is distinct from Issue Detail rather than an inline section below issue description and comments

#### Scenario: Direct route load and refresh

- **WHEN** a user directly loads or refreshes `/issue/:number/files` for an issue with available diff data
- **THEN** the files page renders the same usable reading surface as SPA navigation
- **AND** the React root is not left blank

#### Scenario: Recoverable route or API error

- **WHEN** the files route has an invalid issue number or required issue, diff, commits, or commit-diff data cannot be loaded
- **THEN** the page shows a visible recoverable error state
- **AND** the state includes a path back to the issue detail page when the issue number is known

#### Scenario: Reading-only scope

- **WHEN** a user is on the changed-files page
- **THEN** the page shows file and diff reading controls only
- **AND** it does not show approval/reject actions, review reports, line comments, or merge decision actions

### Requirement: Large diffs are protected by default

The changed-files page SHALL avoid eagerly rendering very large diffs that would disrupt reading or browser performance. Large, generated, dependency-heavy, and lockfile diffs SHALL be summarized with changed-line counts and a `Render anyway` action so the user can explicitly opt in. This protection SHALL apply in the default reading flow and in single-file, split, search, raw patch, and full-file modes where applicable.

#### Scenario: Large diff hidden by default

- **WHEN** a changed file exceeds the reader's large-diff threshold or is classified as generated, dependency-heavy, or a lockfile
- **THEN** the file appears in the file list and summary counts as usual
- **AND** the diff body is replaced by a collapsed placeholder by default
- **AND** the placeholder shows the changed-line count and a `Render anyway` action

#### Scenario: Render large diff on demand

- **WHEN** a user activates `Render anyway` for a hidden large diff
- **THEN** that file's diff content renders in the reading pane without changing the availability status of the overall page
- **AND** unrelated collapsed files remain protected by default

#### Scenario: Large-diff protection across reader modes

- **WHEN** a user reaches a protected file through default reading, single-file unified diff, split diff, search, raw patch, full-file, or commit-scoped reading where the mode could render expensive content
- **THEN** the reader shows the collapsed placeholder until the user explicitly chooses `Render anyway` for that file

### Requirement: Changed-files page defaults to lightweight file-focused reading

The changed-files page SHALL present changed files in a directory-grouped tree and SHALL keep that tree visible as the primary orientation surface. On initial load, the page SHALL NOT render every line of every changed file into the DOM. The reader SHALL either select a sensible first non-generated, non-large, non-binary file or show a lightweight summary/empty reader prompting the user to choose a file.

#### Scenario: Initial reader avoids eager all-files rendering

- **WHEN** a user opens the changed-files page with a multi-file diff
- **THEN** the initial reader does not render every line of every changed file into the DOM
- **AND** the changed-files tree remains visible and usable for navigation

#### Scenario: First readable file is selected

- **WHEN** a changed-file diff contains at least one non-generated, non-large, non-binary readable file and no valid restored selection is available
- **THEN** the reader may select a sensible readable file by default
- **AND** generated files, lockfiles, large files, and binary files are not selected as the first readable source file

#### Scenario: Summary prompt fallback

- **WHEN** every changed file is generated, large, binary, or otherwise unsuitable for default rendering
- **THEN** the reader shows a lightweight summary or empty state that asks the user to choose a file
- **AND** it does not render a full all-files patch stream

#### Scenario: Restored selection is validated

- **WHEN** the page restores a previous file selection
- **THEN** the selection is validated against the current changed files before rendering
- **AND** a missing or stale selection falls back to the first-readable heuristic or the summary prompt

### Requirement: Unified diff reader supports sticky file context without duplicate headers

The changed-files page SHALL render unified diff by default in the reading pane. Each visible file SHALL show one sticky header with path, status, additions, and deletions, and the page SHALL support expand-all and collapse-all controls where those controls are applicable to the current reader flow.

#### Scenario: Unified diff default

- **WHEN** a user opens the changed-files page with available file diffs
- **THEN** unified diff is the default reader mode for rendered diff content
- **AND** line-numbered patch content is visible for eligible expanded files

#### Scenario: Sticky file header

- **WHEN** a user scrolls through a long file diff
- **THEN** the current file keeps one sticky header showing path, status, additions, and deletions

#### Scenario: File header is not duplicated

- **WHEN** a file diff is rendered in the reading pane
- **THEN** the visible file block shows a single file header
- **AND** nested pane or wrapper components do not duplicate the same file header

#### Scenario: Expand and collapse all

- **WHEN** the user activates expand-all or collapse-all where those controls are available
- **THEN** the changed-files reader expands or collapses eligible file diff sections consistently
