## MODIFIED Requirements

### Requirement: Changes panel visible in all workflow stages

IssueDetailPage SHALL display the Changes panel in all workflow stages (Backlog, Explore, Plan, Build, Check, Done) without stage-based gating. The `DIFF_STAGES` restriction SHALL be removed entirely.

#### Scenario: Backlog stage shows empty Changes panel

- **WHEN** the issue is in Backlog stage
- **THEN** the Changes panel is visible
- **AND** displays "No changes yet" empty state
- **AND** no Files/Commits tabs are shown

#### Scenario: Explore stage shows Changes panel

- **WHEN** the issue is in Explore stage
- **AND** the agent has modified or created files
- **THEN** the Changes panel is visible with the diff/commits data
- **AND** Files/Commits tabs and expandable diff viewer work as in other stages

#### Scenario: All other stages show Changes panel unchanged

- **WHEN** the issue is in Plan, Build, Check, or Done stage
- **THEN** the Changes panel is visible with full functionality
- **AND** behavior is identical to the current implementation

### Requirement: Changes panel positioned after Description

IssueDetailPage SHALL render the Changes panel immediately after the Description section and before the TaskList section in the main content column. The Changes panel SHALL NOT appear after Comments.

#### Scenario: Layout order with all sections present

- **WHEN** an issue has a description body, file changes, tasks, and comments
- **THEN** the main content column renders sections in this order: BranchBar, Description, Changes, TaskList, Comments

#### Scenario: Layout order without Description

- **WHEN** an issue has no description body
- **THEN** the main content column renders: BranchBar, Changes, TaskList, Comments
- **AND** the Changes panel is the first content section after BranchBar

#### Scenario: No duplicate diff section

- **WHEN** the Changes panel is rendered in its new position
- **THEN** no additional diff/commits section exists at the bottom of the page
- **AND** the old diff section position (after Comments) is empty
