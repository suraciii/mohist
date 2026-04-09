## MODIFIED Requirements

### Requirement: RalphExecutor Pause Integrated with Session Pause

When RalphExecutor's onAskUser callback is invoked during Build stage execution, the system SHALL pause the agent session and resume it when the user provides a response.

#### Scenario: onAskUser triggers session pause
- **WHEN** RalphExecutor calls onAskUser with a question during Build stage
- **THEN** the agent session SHALL be paused via sessionManager.pause() and the question SHALL be stored for user retrieval

#### Scenario: resume provides answer to pending question
- **WHEN** the user calls resume with a message for an issue that has a pending onAskUser question
- **THEN** the stored Promise SHALL resolve with the user's message and RalphExecutor SHALL continue based on the response (retry/skip/abort)
