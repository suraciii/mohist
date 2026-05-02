## ADDED Requirements

### Requirement: Changes panel visible in all workflow stages

The Changes panel SHALL be visible in all workflow stages (Backlog, Explore, Plan, Build, Check, Done) without stage-based gating. The `DIFF_STAGES` restriction SHALL be removed.

#### Scenario: Backlog stage shows Changes panel

- **WHEN** an issue is in the Backlog stage
- **THEN** the Changes panel is rendered in the main content area
- **AND** it displays "No changes yet" empty state when no commits or file diffs exist

#### Scenario: Explore stage shows Changes panel

- **WHEN** an issue is in the Explore stage
- **THEN** the Changes panel is rendered and shows any commits or file diffs created by the agent

#### Scenario: All other stages show Changes panel

- **WHEN** an issue is in Plan, Build, Check, or Done stage
- **THEN** the Changes panel is rendered with the same behavior as before (files/commits tabs, expandable diff viewer)

### Requirement: Changes panel positioned after Description, before TaskList

The Changes panel SHALL appear in the main content column directly after the Description section and before the TaskList section. The previous position (after Comments) SHALL be removed.

#### Scenario: Layout order with all sections present

- **WHEN** an issue has a description, changes, tasks, and comments
- **THEN** the section order in the main column is: BranchBar → Description → Changes → TaskList → Comments

#### Scenario: Layout order with no description

- **WHEN** an issue has no body/description
- **THEN** the Changes panel appears directly after BranchBar, before TaskList

### Requirement: Changes panel shows summary statistics

The Changes panel SHALL display a summary header with: file count, total additions (+X), total deletions (-Y), and commit count. Statistics SHALL be derived from the existing diff and commits API responses.

#### Scenario: Issue with changes

- **WHEN** an issue has 5 changed files with 120 additions and 45 deletions across 3 commits
- **THEN** the Changes panel header displays "5 files, +120/-45 lines, 3 commits" (or equivalent compact format)

#### Scenario: Issue with no changes

- **WHEN** an issue has no commits and no file diffs
- **THEN** the Changes panel displays "No changes yet" (or equivalent empty state)
- **AND** no summary statistics are shown

#### Scenario: Only commits, no file diffs

- **WHEN** commits API returns data but diff API returns empty files
- **THEN** summary shows commit count with "0 files" and no line statistics

### Requirement: Empty state for stages with no changes

When the Changes panel is visible but there are no commits or file diffs, it SHALL display a clear empty state message indicating no changes exist yet.

#### Scenario: Backlog issue with no agent activity

- **WHEN** an issue is in Backlog stage and has never had agent activity
- **THEN** the Changes panel shows "No changes yet" empty state
- **AND** no tabs or expandable sections are shown

### Requirement: Approval panels include compact changes summary

PlanApprovalPanel and ReviewApprovalPanel SHALL display a compact changes summary (file count, additions/deletions, commit count) inline, so users can see the scope of changes without scrolling to the main Changes panel.

#### Scenario: Plan approval with changes

- **WHEN** an issue is in Plan stage awaiting approval
- **AND** the agent has made file changes
- **THEN** the PlanApprovalPanel displays a compact summary line (e.g., "3 files, +50/-12 lines, 2 commits")

#### Scenario: Review approval with changes

- **WHEN** an issue is in Check stage awaiting review approval
- **AND** the agent has made file changes
- **THEN** the ReviewApprovalPanel displays a compact summary line

#### Scenario: Approval panel with no changes

- **WHEN** an issue is awaiting approval but has no changes
- **THEN** the approval panel shows "No changes yet" or omits the summary section
