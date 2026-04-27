## ADDED Requirements

### Requirement: IssueDetailPage shows live progress panel for active agents

When the user views an issue detail page and an agent is actively running on that issue (`isAgentRunningOnThis === true`), the sidebar action area SHALL display a live progress panel instead of the Close button. The progress panel SHALL show: current stage label, round type (for Plan/Review stages), task progress as "X/Y tasks" (for Build stage), and a relative-time "last activity" indicator (e.g. "12s ago", "2m ago").

#### Scenario: Agent running Plan stage
- **WHEN** the user views issue #5 and agent is running Plan stage, currently on `design` round
- **THEN** the sidebar shows a progress panel with: stage "Plan", round "design", and last-activity timestamp
- **AND** no Close button is shown

#### Scenario: Agent running Build stage
- **WHEN** the user views issue #5 and agent is running Build stage with 2/5 tasks complete
- **THEN** the sidebar shows a progress panel with: stage "Build", "2/5 tasks", and last-activity timestamp

#### Scenario: Progress panel updates in real-time
- **WHEN** the agent transitions from Plan `specs` round to `design` round
- **THEN** the progress panel updates to show round "design" without page refresh
- **AND** the update is driven by the `agentStatus` query refresh (not SSE)

### Requirement: IssueDetailPage provides Force Stop button

When an agent is actively running on the viewed issue, the progress panel SHALL include a Force Stop button. Clicking it SHALL require a single confirmation (inline "Are you sure?" toggle, not a dialog). Upon confirmation, the Force Stop API is called.

#### Scenario: Force Stop button shown
- **WHEN** the user views issue #5 and agent is running
- **THEN** a Force Stop button is visible in the progress panel

#### Scenario: Force Stop requires confirmation
- **WHEN** the user clicks Force Stop
- **THEN** the button text changes to "Confirm Force Stop" (or similar inline confirmation)
- **AND** no API call is made yet

#### Scenario: Force Stop confirmed
- **WHEN** the user clicks the confirmation button
- **THEN** `api.forceStopIssue(issueNumber)` is called
- **AND** on success, the issue status updates to `interrupted`
- **AND** the progress panel is replaced with the Resume Pipeline button

#### Scenario: Force Stop cancelled
- **WHEN** the user clicks Force Stop and then clicks elsewhere or waits 5 seconds
- **THEN** the confirmation state resets and the button returns to "Force Stop"

### Requirement: Frontend polling refreshes agentStatus during active runs

The `useAgentStatus` hook SHALL continue using its existing 5-second refetch interval. This already ensures the progress panel shows current data without requiring SSE for progress metadata. No polling interval change is needed — 5 seconds is appropriate for both running and idle states.

#### Scenario: Agent running — polling active
- **WHEN** the user views issue #5 and an agent is running on #5
- **THEN** `useAgentStatus` refetches every 5 seconds
- **AND** the progress panel reflects the latest `activeAgents[0].progress` data

#### Scenario: No agent running — polling continues
- **WHEN** no agent is running on the viewed issue
- **THEN** `useAgentStatus` continues refetching every 5 seconds (unchanged behavior)

### Requirement: Relative time display for last activity

The progress panel SHALL display the `lastActivityAt` timestamp as a relative time string (e.g. "just now", "12s ago", "2m ago"). The display SHALL update periodically (every 10 seconds) to stay current.

#### Scenario: Activity 5 seconds ago
- **WHEN** `lastActivityAt` was 5 seconds ago
- **THEN** the display shows "just now"

#### Scenario: Activity 2 minutes ago
- **WHEN** `lastActivityAt` was 2 minutes ago
- **THEN** the display shows "2m ago"
