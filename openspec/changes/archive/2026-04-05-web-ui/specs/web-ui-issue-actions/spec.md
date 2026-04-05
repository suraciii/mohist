## ADDED Requirements

### Requirement: Create issue from Web UI
The system SHALL allow users to create a new issue with title, optional body, and optional labels via a dialog form.

#### Scenario: Create issue with title only
- **WHEN** user clicks "+ New Issue" and enters a title
- **THEN** a new issue is created in `draft` stage with `active` status and the kanban refreshes

#### Scenario: Create issue with labels
- **WHEN** user creates an issue with labels `["feature", "v2"]`
- **THEN** the issue is created with those labels attached

### Requirement: Start issue from Web UI
The system SHALL allow users to start an issue (draft → plan, triggering the Agent) from the issue detail page.

#### Scenario: Start issue successfully
- **WHEN** user clicks "Start" on a draft issue and no other agent is running
- **THEN** the issue transitions to plan stage and the Agent starts

#### Scenario: Agent already running
- **WHEN** user tries to start an issue while another agent is running
- **THEN** the Start button is disabled and a message indicates an agent is already active

### Requirement: Approve gate from Web UI (Stop & Resume)
The system SHALL allow users to approve an issue at an approval gate, starting a new agent session to continue the workflow. The previous agent session has already completed; the new session resumes from the approved stage with context from the latest agent comment.

#### Scenario: Approve and continue
- **WHEN** user clicks "Approve & Continue" on an issue at an approval gate
- **THEN** server starts a new agent session for the next stage, injecting context from the previous stage's output (stored as the latest agent comment)
- **AND** the agent begins executing the approved stage's prompt

#### Scenario: Approve button disabled
- **WHEN** an agent is already running on another issue
- **THEN** the Approve button is disabled

### Requirement: Close and reopen issue from Web UI
The system SHALL allow users to close (set status to blocked) and reopen (set status to active) issues.

#### Scenario: Close issue
- **WHEN** user clicks "Close" on an active issue
- **THEN** the issue status changes to blocked

#### Scenario: Reopen issue
- **WHEN** user clicks "Reopen" on a blocked issue
- **THEN** the issue status changes to active

### Requirement: Edit issue from Web UI
The system SHALL allow users to edit issue title, body, and labels from the detail page.

#### Scenario: Edit title
- **WHEN** user edits the issue title and saves
- **THEN** the title is updated and the kanban card refreshes

### Requirement: Add comment from Web UI
The system SHALL allow users to add comments to an issue from the detail page.

#### Scenario: Add comment
- **WHEN** user enters comment text and submits
- **THEN** the comment appears in the comments list with current timestamp
