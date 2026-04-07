## ADDED Requirements

### Requirement: Create explore session API
The system SHALL expose `POST /api/explore` to create a new explore session.

#### Scenario: Create session with project
- **WHEN** client sends POST /api/explore with { projectId, title }
- **THEN** system creates a session and returns { success: true, data: session }

### Requirement: List explore sessions API
The system SHALL expose `GET /api/explore?projectId=<id>` to list explore sessions for a project.

#### Scenario: List sessions
- **WHEN** client sends GET /api/explore with projectId query parameter
- **THEN** system returns { success: true, data: [sessions...] } ordered by updated_at desc

### Requirement: Get explore session API
The system SHALL expose `GET /api/explore/:id` to retrieve a session with its messages.

#### Scenario: Get existing session
- **WHEN** client sends GET /api/explore/:id
- **THEN** system returns { success: true, data: { session, messages } }

#### Scenario: Get non-existent session
- **WHEN** client sends GET /api/explore/:id for a non-existent session
- **THEN** system returns { success: false, error: "Session not found" } with 404 status

### Requirement: Delete explore session API
The system SHALL expose `DELETE /api/explore/:id` to delete a session.

#### Scenario: Delete session
- **WHEN** client sends DELETE /api/explore/:id
- **THEN** system deletes the session and all messages, returns { success: true }

### Requirement: Send message API with streaming response
The system SHALL expose `POST /api/explore/:id/messages` that accepts a user message and returns an SSE stream with the agent's response.

#### Scenario: Successful streaming response
- **WHEN** client sends POST /api/explore/:id/messages with { content: "message" }
- **THEN** system returns text/event-stream with events: tool_call (for each tool invocation), chunk (for each text fragment), and done (on completion)

#### Scenario: Tool call event format
- **WHEN** agent invokes a tool during response
- **THEN** system emits SSE event: `{ type: "tool_call", tool: "<name>", args: <object>, result: <string> }`

#### Scenario: Text chunk event format
- **WHEN** agent produces text output
- **THEN** system emits SSE event: `{ type: "chunk", content: "<text fragment>" }`

#### Scenario: Done event format
- **WHEN** agent finishes responding
- **THEN** system emits SSE event: `{ type: "done", issueId: <string|null> }` where issueId is set if agent created an issue

#### Scenario: Send to non-existent session
- **WHEN** client sends a message to a non-existent session
- **THEN** system returns 404 error

### Requirement: Explore events on EventBus
The system SHALL emit explore-related events on the existing EventBus for SSE pub-sub integration.

#### Scenario: Session crystallized event
- **WHEN** an explore session is crystallized (issue created)
- **THEN** system emits `explore_crystallized` event with { sessionId, issueId, projectId }
