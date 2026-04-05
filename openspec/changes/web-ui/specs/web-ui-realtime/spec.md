## ADDED Requirements

### Requirement: In-Process EventBus module
The system SHALL provide an in-process EventBus class with `emit(event, data)`, `on(event, listener)`, and `off(event, listener)` methods. The EventBus SHALL be a singleton shared across the Server process.

#### Scenario: EventBus emit and subscribe
- **WHEN** code calls `eventBus.emit('stage_changed', { issueId, from, to })`
- **THEN** all listeners subscribed to `stage_changed` are called with the data

#### Scenario: EventBus unsubscribe
- **WHEN** code calls `eventBus.off('stage_changed', listener)`
- **THEN** the listener is no longer called for subsequent events

### Requirement: SSE endpoint for real-time events
The system SHALL provide a `GET /api/events` SSE endpoint that pushes real-time events to connected clients. The endpoint SHALL support an optional `projectId` query parameter for project-scoped filtering.

#### Scenario: Client connects to SSE
- **WHEN** browser connects to `GET /api/events`
- **THEN** server keeps connection open and sends events as they occur

#### Scenario: Client connects with project filter
- **WHEN** browser connects to `GET /api/events?projectId=abc123`
- **THEN** server only sends events related to project `abc123`

#### Scenario: Client disconnects
- **WHEN** browser disconnects from `/api/events`
- **THEN** server cleans up the EventBus listener subscription

### Requirement: Stage changed event
The system SHALL emit a `stage_changed` event when an issue's stage is updated via the `advance_stage` tool. The event SHALL include `projectId` for filtering.

#### Scenario: Stage transition
- **WHEN** issue #1 transitions from `plan` to `build`
- **THEN** `stage_changed` event is pushed with `{ issueId, projectId, from: "plan", to: "build" }`

### Requirement: Comment added event
The system SHALL emit a `comment_added` event when a new comment is created via the `add_comment` tool.

#### Scenario: New comment
- **WHEN** a comment is added to issue #1
- **THEN** `comment_added` event is pushed with `{ issueId, projectId, commentId, body, createdAt }`

### Requirement: Agent lifecycle events
The system SHALL emit `agent_started`, `agent_completed`, and `agent_error` events via the AgentRunnerService.

#### Scenario: Agent starts
- **WHEN** the Agent is started for issue #1
- **THEN** `agent_started` event is pushed with `{ issueId, projectId }`

#### Scenario: Agent completes
- **WHEN** the Agent finishes successfully for issue #1
- **THEN** `agent_completed` event is pushed with `{ issueId, projectId }`

#### Scenario: Agent errors
- **WHEN** the Agent fails with an error
- **THEN** `agent_error` event is pushed with `{ issueId, projectId, error }`

### Requirement: Approval requested event
The system SHALL emit an `approval_requested` event when the `advance_stage` tool detects the target stage has `approval: true` in the workflow configuration.

#### Scenario: Agent reaches gate
- **WHEN** `advance_stage` moves issue to a stage with `approval: true`
- **THEN** `approval_requested` event is pushed with `{ issueId, projectId, stage }`

### Requirement: EventBus injected into Agent tools
The system SHALL inject EventBus into `advance_stage` and `add_comment` tools via their context objects. Tools emit events after successful database writes.

#### Scenario: advance_stage emits event
- **WHEN** `advance_stage` tool successfully updates an issue's stage in DB
- **THEN** `stage_changed` event is emitted via EventBus

#### Scenario: add_comment emits event
- **WHEN** `add_comment` tool successfully creates a comment in DB
- **THEN** `comment_added` event is emitted via EventBus

#### Scenario: Tool without EventBus
- **WHEN** EventBus is not provided in tool context (e.g., CLI usage)
- **THEN** tool functions normally without emitting events
