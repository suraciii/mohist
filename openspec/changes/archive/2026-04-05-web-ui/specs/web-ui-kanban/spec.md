## ADDED Requirements

### Requirement: Kanban displays issues by stage columns
The system SHALL display a kanban board with columns for each workflow stage: draft, plan, build, check, done. Each issue MUST appear as a card in its current stage column.

#### Scenario: View kanban board
- **WHEN** user navigates to `/`
- **THEN** kanban board displays 5 columns (draft, plan, build, check, done) with issue cards

#### Scenario: Empty stage column
- **WHEN** a stage has no issues
- **THEN** the column displays an empty state indicator

### Requirement: Issue card displays key information
Each issue card SHALL display: issue number, title (truncated), labels (if any), and status indicator (active/paused/blocked).

#### Scenario: Card with labels
- **WHEN** an issue has labels `["bug", "urgent"]`
- **THEN** the card displays both label badges

#### Scenario: Card with long title
- **WHEN** an issue title exceeds the card width
- **THEN** the title is truncated with ellipsis

### Requirement: Agent running indicator on cards
The system SHALL display a visual indicator on issue cards when the Agent is actively working on that issue.

#### Scenario: Agent running on issue
- **WHEN** the Agent is running for issue #3
- **THEN** the card for issue #3 shows an animated running indicator

#### Scenario: No agent running
- **WHEN** no Agent is active on any issue
- **THEN** no cards display a running indicator

### Requirement: Approval gate indicator on cards
The system SHALL display a visual indicator when an issue is waiting for user approval at a gate stage.

#### Scenario: Issue waiting at approval gate
- **WHEN** Agent completed a stage with `approval: true` and is waiting
- **THEN** the card displays a "Waiting for approval" badge
