## ADDED Requirements

### Requirement: Changes panel visible in all workflow stages

IssueDetailPage SHALL display the Changes panel in every workflow stage (Backlog, Explore, Plan, Build, Check, Done) without stage-based gating. The `DIFF_STAGES` constant and its associated conditional check SHALL be removed.

#### Scenario: Backlog stage shows empty state
- **WHEN** user views an issue in Backlog stage
- **THEN** the Changes panel is visible
- **AND** it displays an empty state message (e.g., "No changes yet")

#### Scenario: Explore stage shows changes
- **WHEN** user views an issue in Explore stage
- **THEN** the Changes panel is visible
- **AND** it shows any file changes or commits made during exploration

#### Scenario: All other stages show changes
- **WHEN** user views an issue in Plan, Build, Check, or Done stage
- **THEN** the Changes panel is visible with the same behavior as before (files, commits, expandable diff)

### Requirement: Changes panel positioned after Description

The Changes section SHALL appear immediately after the Description section and before the TaskList in the IssueDetailPage main content area. The previous position (after Comments) SHALL be removed.

#### Scenario: Changes section appears in correct order
- **WHEN** user views an issue that has a description and changes
- **THEN** the page layout order in the main content column is: BranchBar, Description, Changes, TaskList, Comments
- **AND** no duplicate Changes section exists below Comments

#### Scenario: Changes section appears when Description is absent
- **WHEN** user views an issue that has no description but has changes
- **THEN** the Changes section still appears after the Description area (which is empty) and before TaskList

### Requirement: Changes panel shows summary statistics

The Changes panel SHALL display a summary header with aggregate statistics: file count, total additions, total deletions, and commit count.

#### Scenario: Summary displays when changes exist
- **WHEN** the Changes panel renders with file changes and/or commits
- **THEN** a summary line is displayed showing the count of changed files, total additions (+X), total deletions (-Y), and number of commits
- **AND** the summary uses data already available from the existing `getIssueDiff` and `getIssueCommits` API responses

#### Scenario: Summary displays zero-state
- **WHEN** the Changes panel renders with no file changes and no commits
- **THEN** the summary shows zero values (e.g., "0 files, 0 commits")

### Requirement: Approval sections show compact changes summary

The approval gate sections in IssueDetailPage's sidebar SHALL display a compact inline changes summary so users can see the scope of changes without scrolling the main content area. The summary SHALL be computed from the same diff/commits data already fetched by the page.

#### Scenario: Plan approval shows changes summary
- **WHEN** user views an issue in Plan stage awaiting approval
- **THEN** the approval gate section in the sidebar displays a compact summary (file count, +/- lines, commit count)
- **AND** the summary data comes from the same diff/commits data already fetched by the page

#### Scenario: Review approval shows changes summary
- **WHEN** user views an issue in Check/Done stage awaiting review
- **THEN** the approval gate section in the sidebar displays a compact summary (file count, +/- lines, commit count)
