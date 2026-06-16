## ADDED Requirements

### Requirement: API provides session compact endpoint

The HTTP API SHALL expose `POST /api/projects/{ref}/issues/{number}/sessions/{name}/compact` to trigger context compaction for a session. The endpoint SHALL validate the session exists and is not actively running before initiating compaction. On success, it SHALL return the updated context window metrics.

#### Scenario: Compact succeeds for inactive session
- **WHEN** a client sends `POST /api/projects/{ref}/issues/{number}/sessions/{name}/compact`
- **AND** the session exists and is not actively running
- **THEN** the server SHALL initiate summary-based compaction on the ACP session
- **AND** the response SHALL return `{ contextWindowSize, contextWindowUsed, contextUsagePercent }`
- **AND** response status SHALL be 200

#### Scenario: Compact rejected for active session
- **WHEN** a client sends compact request for an actively running session
- **THEN** the server SHALL return HTTP 409 with `{ error: "Cannot compact while session is active" }`

#### Scenario: Compact for nonexistent session returns 404
- **WHEN** a client sends compact request for a session that does not exist
- **THEN** the server SHALL return HTTP 404 with `{ error: "Session not found" }`

### Requirement: API provides session reset endpoint

The HTTP API SHALL expose `POST /api/projects/{ref}/issues/{number}/sessions/{name}/reset` to clear all context from a session. The endpoint SHALL validate the session exists and is not actively running before resetting. On success, it SHALL return the cleared context window metrics.

#### Scenario: Reset succeeds for inactive session
- **WHEN** a client sends `POST /api/projects/{ref}/issues/{number}/sessions/{name}/reset`
- **AND** the session exists and is not actively running
- **THEN** the server SHALL clear the session's conversation history
- **AND** the response SHALL return `{ contextWindowSize, contextWindowUsed: <system prompt tokens only>, contextUsagePercent }`
- **AND** response status SHALL be 200

#### Scenario: Reset rejected for active session
- **WHEN** a client sends reset request for an actively running session
- **THEN** the server SHALL return HTTP 409 with `{ error: "Cannot reset while session is active" }`

#### Scenario: Reset for nonexistent session returns 404
- **WHEN** a client sends reset request for a session that does not exist
- **THEN** the server SHALL return HTTP 404 with `{ error: "Session not found" }`

### Requirement: Retry endpoint verifies session context health before proceeding

The `POST /api/issues/{number}/retry` endpoint SHALL check the context window usage of the session associated with the current task before accepting the retry. If context usage exceeds 90%, the endpoint SHALL reject the retry with a clear error message suggesting Compact or Reset. If usage is between 80% and 90%, a warning SHALL be logged but retry SHALL proceed. Below 80%, retry SHALL proceed normally.

#### Scenario: Retry accepted when session context is healthy
- **WHEN** a client requests retry for an issue
- **AND** the associated session has context usage at 45%
- **THEN** retry SHALL proceed as normal

#### Scenario: Retry rejected when session context is near capacity
- **WHEN** a client requests retry for an issue
- **AND** the associated session has context usage at 92%
- **THEN** the server SHALL return HTTP 409 with `{ error: "Session context is near capacity (92%). Compact or reset the session before retrying.", suggestedActions: ["compact", "reset"] }`

#### Scenario: Retry accepted after session recovery
- **WHEN** a client compacts a session from 92% to 50%
- **AND** then requests retry
- **THEN** retry SHALL be accepted

### Requirement: Compaction and reset endpoints require valid issue context

The compact and reset endpoints SHALL require the issue identified by `:number` to exist and belong to the current project. Requests for non-existent issues SHALL return 404. Cross-project access SHALL be rejected.

#### Scenario: Compact for non-existent issue returns 404
- **WHEN** a client sends compact request with an issue number that does not exist
- **THEN** the server SHALL return HTTP 404 with `{ error: "Issue not found" }`

#### Scenario: Session does not belong to the specified issue
- **WHEN** a client sends compact request for a session that exists but belongs to a different issue
- **THEN** the server SHALL return HTTP 404 with `{ error: "Session not found for this issue" }`
