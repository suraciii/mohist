## MODIFIED Requirements

### Requirement: Coder session transcript detail
The HTTP API SHALL expose structured coder session transcript data for session detail pages. The response SHALL include trustworthy session metadata, ordered conversation turns, assistant parts, incomplete-data markers, and enough raw context for debugging without forcing the frontend to infer turns from raw logs.

#### Scenario: Detail endpoint returns transcript
- **WHEN** the frontend requests coder session detail for `/issue/:number/session/:sessionId`
- **THEN** the API returns session metadata and a transcript containing ordered conversation turns
- **AND** each turn contains a Mohist user message and assistant parts for text, reasoning, tools, or errors

#### Scenario: API uses persisted session stream first
- **WHEN** `session_stream_log` contains events for the coder session
- **THEN** the API builds the transcript from those events

#### Scenario: API falls back for legacy history
- **WHEN** `session_stream_log` has no events for an old session but `workflow_log` has session stream events
- **THEN** the API uses filtered legacy stream events to build the transcript

#### Scenario: Metadata distinguishes running and terminal sessions
- **WHEN** the API returns session metadata
- **THEN** it includes title, status, model, stage, coderSessionId, acpSessionId, executionId, createdAt, and available context
- **AND** completedAt is present only for terminal sessions
