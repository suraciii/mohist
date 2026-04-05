## ADDED Requirements

### Requirement: Issue detail page displays full information
The system SHALL display a detail page for each issue showing: number, title, stage, status, labels, description, and creation/update timestamps.

#### Scenario: View issue detail
- **WHEN** user clicks on an issue card or navigates to `/issue/:number`
- **THEN** the detail page displays all issue fields

### Requirement: Stage progress bar
The system SHALL display a horizontal progress bar showing the 5 workflow stages (draft → plan → build → check → done) with the current stage highlighted.

#### Scenario: Issue at build stage
- **WHEN** an issue is in `build` stage
- **THEN** the progress bar highlights draft, plan, and build as completed/active

### Requirement: Comments list
The system SHALL display all comments for an issue in chronological order (oldest first), each with timestamp.

#### Scenario: Issue with comments
- **WHEN** an issue has 3 comments
- **THEN** all 3 comments are displayed in order with timestamps

#### Scenario: Issue with no comments
- **WHEN** an issue has no comments
- **THEN** an empty state message is displayed

### Requirement: Approval gate action area
When an issue is waiting at an approval gate, the detail page SHALL display the plan/design output and action buttons (Approve & Continue, Skip).

#### Scenario: Approval gate active
- **WHEN** Agent completed a stage with `approval: true`
- **THEN** detail page shows the last agent comment as plan output and displays "Approve & Continue" and "Skip" buttons

#### Scenario: No approval pending
- **WHEN** no approval gate is active for the issue
- **THEN** no approval action area is displayed

### Requirement: Git diff display
The system SHALL display git diff summary for the issue's worktree branch when available (build/check/done stages).

#### Scenario: Issue with file changes
- **WHEN** issue is in build stage and has uncommitted changes in worktree
- **THEN** detail page shows a diff summary listing changed files

#### Scenario: Issue in draft stage
- **WHEN** issue is in draft stage (no worktree yet)
- **THEN** no git diff section is displayed
