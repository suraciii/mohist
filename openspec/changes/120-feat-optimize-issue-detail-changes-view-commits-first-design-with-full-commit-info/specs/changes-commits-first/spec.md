## ADDED Requirements

### Requirement: Commits tab is default view in Changes section

The Changes section SHALL default to the Commits tab instead of the Files tab. The tab state SHALL initialize to `'commits'`.

#### Scenario: User opens issue detail page with changes

- **WHEN** user navigates to an issue detail page that has changes (commits or file diffs)
- **THEN** the Changes section displays the Commits tab by default
- **AND** the Commits tab button shows active/selected styling

#### Scenario: User switches to Files tab and back

- **WHEN** user clicks the Files tab button
- **THEN** the Files tab content is displayed
- **AND** clicking the Commits tab button returns to the Commits view

### Requirement: Commit rows display file name list

Each commit row in the Commits tab SHALL display the list of file paths changed by that commit, fetched from the `files` field in the commits API response. File names SHALL be shown inline below or beside the commit message, without requiring the user to expand the diff.

#### Scenario: Commit with 3 changed files

- **WHEN** a commit entry has `files: ["src/foo.ts", "src/bar.ts", "README.md"]`
- **THEN** the commit row displays the three file names (e.g., as truncated mono-spaced text)
- **AND** the user can see which files were changed without expanding

#### Scenario: Commit with many changed files

- **WHEN** a commit entry has more than 5 files
- **THEN** the commit row displays the first 5 file names with a "+N more" indicator
- **AND** expanding the commit shows the full file list

#### Scenario: Commit with no file list from API

- **WHEN** the commits API response does not include `files` for a commit (backward compatibility)
- **THEN** the file list area is omitted for that row
- **AND** the commit row still displays hash, message, stats, and date

### Requirement: Commit expanded view uses DiffViewer

When a commit row is expanded, the diff content SHALL be rendered using the existing `DiffViewer` component (which provides file-level grouping, line numbers, and expand/collapse per file). The inline `CommitDiffView` component SHALL NOT be used.

#### Scenario: Expand a commit to view diff

- **WHEN** user clicks a commit row to expand it
- **THEN** the diff for that commit is fetched via `GET /api/issues/:number/commits/:hash/diff`
- **AND** the raw unified diff is rendered using `DiffViewer`
- **AND** each file block shows line numbers in old/new columns
- **AND** file blocks are independently expandable/collapsible

#### Scenario: Binary file in commit diff

- **WHEN** a commit includes a binary file change
- **THEN** DiffViewer renders "Binary file" placeholder for that file block

### Requirement: Files tab uses DiffViewer for inline diff

The Files tab SHALL display each file with precise `--numstat` statistics. Clicking a file row SHALL expand it to show the inline diff using `DiffViewer`, with the diff content sourced from the enhanced diff API response's per-file `diff` field.

#### Scenario: Files tab with diff expansion

- **WHEN** user is on the Files tab and clicks a file row
- **THEN** the file row expands to show the full diff for that file using `DiffViewer`
- **AND** line numbers are displayed in old/new columns

#### Scenario: Files tab shows precise statistics

- **WHEN** the diff API returns per-file additions/deletions from `--numstat`
- **THEN** each file row shows the exact number of additions and deletions
- **AND** the numbers are NOT derived from symbol counting

### Requirement: No noise commit filtering

The Commits tab SHALL display all commits returned by the commits API without filtering, grouping, or auto-collapsing any commits (including `chore(tasks)`, `WIP`, or `chore: commit remaining`). All commits are part of the audit trail.

#### Scenario: Display chore commits

- **WHEN** the commits API returns commits with messages like "chore(tasks): ..." or "WIP: ..."
- **THEN** all such commits are displayed inline in the same list as all other commits
- **AND** no commits are grouped under "Auto commits" or similar collapsible sections

#### Scenario: Display all commits in chronological order

- **WHEN** the commits API returns 10 commits
- **THEN** all 10 commits are visible in the Commits tab without any being hidden or collapsed by default
