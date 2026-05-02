## ADDED Requirements

### Requirement: Changes panel visible in all workflow stages

IssueDetailPage SHALL render the Changes panel in all workflow stages (Backlog, Explore, Plan, Build, Check, Done) without stage-based gating. The panel SHALL NOT be conditionally hidden based on `DIFF_STAGES` or similar sets.

#### Scenario: Backlog stage shows Changes panel

- **WHEN** user views an issue in Backlog stage
- **THEN** the Changes panel is visible after the Description section
- **AND** it displays an empty state (e.g., "No changes yet")

#### Scenario: Explore stage shows Changes panel

- **WHEN** user views an issue in Explore stage
- **THEN** the Changes panel is visible after the Description section
- **AND** if the agent has created/modified files, those changes are displayed

#### Scenario: Plan stage shows Changes panel

- **WHEN** user views an issue in Plan stage
- **THEN** the Changes panel is visible after the Description section
- **AND** openspec file changes and any other modifications are displayed

### Requirement: Changes panel positioned after Description, before TaskList

IssueDetailPage main content column SHALL render sections in this order: BranchBar, Description (if present), Changes panel, TaskList (if applicable), Comments. The Changes panel SHALL NOT appear after Comments.

#### Scenario: Full content layout order

- **WHEN** user views an issue with a body, tasks, and comments
- **THEN** the page renders in order: BranchBar, Description, Changes panel, TaskList, Comments

#### Scenario: Issue without body

- **WHEN** user views an issue with no body text
- **THEN** the Changes panel appears directly after BranchBar, before TaskList

### Requirement: Changes panel shows summary statistics

The Changes panel SHALL display a summary header with file count, total additions, total deletions, and commit count.

#### Scenario: Changes exist

- **WHEN** the issue has 3 changed files with +120/-45 lines and 2 commits
- **THEN** the Changes panel header shows "3 files changed, +120, -45, 2 commits" (or equivalent compact format)

#### Scenario: No changes exist

- **WHEN** the issue has no file changes and no commits
- **THEN** the Changes panel shows an empty state message (e.g., "No changes yet")

#### Scenario: Only commits, no diff files

- **WHEN** the issue has commits but diff data is empty or loading
- **THEN** the summary shows commit count and indicates files are loading or unavailable

### Requirement: Existing Files/Commits tabs and diff viewer preserved

The Changes panel SHALL continue to provide Files and Commits tabs with the same expandable diff viewer behavior. This requirement does not change existing tab or diff rendering logic.

#### Scenario: Files tab displays expandable diffs

- **WHEN** user clicks the Files tab
- **THEN** files are listed with change indicators
- **AND** clicking a file expands to show the inline diff using DiffViewer

#### Scenario: Commits tab displays commit list

- **WHEN** user clicks the Commits tab
- **THEN** commits are listed with hash, message, and metadata
- **AND** clicking a commit expands to show its file changes
