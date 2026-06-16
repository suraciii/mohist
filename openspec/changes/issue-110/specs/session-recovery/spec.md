## ADDED Requirements

### Requirement: Session page provides Compact button for manual compaction

The session page SHALL display a Compact button that triggers manual context compaction. The button SHALL be visible on both active and inactive session pages. Clicking Compact SHALL send a request to the server to compact the session's context window using the summary-based strategy. After compaction completes, the page SHALL refresh context usage data.

#### Scenario: Compact button triggers compaction
- **WHEN** a user clicks the Compact button on an inactive session page
- **THEN** the system SHALL send a compact request for that session
- **AND** the session context SHALL be compacted using summary-based strategy
- **AND** the context usage bar SHALL update to reflect reduced usage

#### Scenario: Compact button disabled while session is actively running
- **WHEN** a session is actively running (agent is executing)
- **THEN** the Compact button SHALL be disabled
- **AND** a tooltip SHALL explain that compaction is unavailable during active execution

#### Scenario: Compact button enabled for paused or completed sessions
- **WHEN** a session is paused or completed
- **THEN** the Compact button SHALL be enabled
- **AND** clicking it SHALL trigger compaction

### Requirement: Session page provides Reset button for full context reset

The session page SHALL display a Reset button that clears all context and starts a fresh session state. The button SHALL be disabled while the session is actively running. Clicking Reset SHALL show a confirmation dialog warning about irreversible context loss before proceeding.

#### Scenario: Reset button shows confirmation dialog
- **WHEN** a user clicks the Reset button on an inactive session
- **THEN** a confirmation dialog SHALL appear with text warning "This will clear all session context. The agent will lose all conversation history."
- **AND** the dialog SHALL have Cancel and Confirm actions

#### Scenario: Reset confirmed clears all context
- **WHEN** a user confirms the reset dialog
- **THEN** the system SHALL clear all session context
- **AND** the context window usage SHALL reset to a minimal value (only system prompt remains)
- **AND** the page SHALL update to reflect the fresh session state

#### Scenario: Reset cancelled preserves context
- **WHEN** a user cancels the reset confirmation dialog
- **THEN** the session context SHALL remain unchanged
- **AND** the page SHALL not reload or refresh

#### Scenario: Reset button disabled while session is actively running
- **WHEN** a session is actively running
- **THEN** the Reset button SHALL be disabled
- **AND** a tooltip SHALL explain that reset is unavailable during active execution

### Requirement: API provides session compact endpoint

The HTTP API SHALL provide an endpoint to trigger session compaction. The endpoint SHALL validate that the session exists and is not currently active before initiating compaction.

#### Scenario: Compact request succeeds for inactive session
- **WHEN** a client sends `POST /api/projects/{ref}/issues/:number/sessions/:name/compact`
- **AND** the session exists and is not actively running
- **THEN** the server SHALL initiate summary-based compaction
- **AND** the response SHALL return updated context window usage (`contextWindowSize`, `contextWindowUsed`)

#### Scenario: Compact request rejected for active session
- **WHEN** a client sends `POST /api/projects/{ref}/issues/:number/sessions/:name/compact`
- **AND** the session is actively running
- **THEN** the server SHALL return HTTP 409 with message "Cannot compact while session is active"

#### Scenario: Compact request for nonexistent session
- **WHEN** a client sends compact request for a session that does not exist
- **THEN** the server SHALL return HTTP 404 with message "Session not found"

### Requirement: API provides session reset endpoint

The HTTP API SHALL provide an endpoint to reset session context. The endpoint SHALL validate that the session exists and is not currently active before clearing context. After reset, the session SHALL retain its identifier but have empty conversation history and minimal context usage.

#### Scenario: Reset request succeeds for inactive session
- **WHEN** a client sends `POST /api/projects/{ref}/issues/:number/sessions/:name/reset`
- **AND** the session exists and is not actively running
- **THEN** the server SHALL clear all context from the session
- **AND** the response SHALL return reset context window usage (system prompt only)
- **AND** the session status SHALL remain unchanged

#### Scenario: Reset request rejected for active session
- **WHEN** a client sends `POST /api/projects/{ref}/issues/:number/sessions/:name/reset`
- **AND** the session is actively running
- **THEN** the server SHALL return HTTP 409 with message "Cannot reset while session is active"

#### Scenario: Reset request for nonexistent session
- **WHEN** a client sends reset request for a session that does not exist
- **THEN** the server SHALL return HTTP 404 with message "Session not found"

### Requirement: Retry verifies session health before resuming

The workflow retry path SHALL verify the target session's context window usage before resuming execution. Retry SHALL be rejected when usage exceeds 90% with guidance to compact or reset first. When usage is between 80% and 90%, a warning SHALL be logged but retry SHALL proceed. Below 80%, retry SHALL proceed normally without warnings.

#### Scenario: Retry accepted when session has healthy context
- **WHEN** a user triggers retry for a failed task
- **AND** the associated session has context usage at 45%
- **THEN** retry SHALL proceed normally

#### Scenario: Retry rejected when session context is exhausted
- **WHEN** a user triggers retry for a failed task
- **AND** the associated session has context usage at 92%
- **THEN** retry SHALL be rejected with error "Session context is near capacity (92%). Compact or reset the session before retrying."
- **AND** the error SHALL suggest Compact or Reset as recovery actions

#### Scenario: Retry proceeds after session recovery
- **WHEN** a user compacts a session from 92% to 50%
- **AND** then triggers retry
- **THEN** retry SHALL be accepted because context usage is below threshold
