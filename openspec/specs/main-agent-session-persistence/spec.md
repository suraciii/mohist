## ADDED Requirements

### Requirement: Main Agent messages persist to SQLite
After each agent loop step completes, the system SHALL write all messages from that step to the `agent_session_message` table, including role, content, tool_calls, tool results, and step_index.

#### Scenario: Agent completes a step with tool calls
- **WHEN** runAgentLoop processes a step that contains an assistant message with tool_calls followed by tool results
- **THEN** the system writes the assistant message (with tool_calls JSON) and each tool result message to agent_session_message table with the correct step_index and message_index

#### Scenario: Agent loop finishes all steps
- **WHEN** runAgentLoop completes all steps
- **THEN** all steps' messages are persisted, ordered by step_index and message_index
- **AND** step_index aligns with `result.steps` array index (0-based)
- **AND** message_index aligns with the message position within `step.response.messages`

### Requirement: Agent session messages queryable by issue
The system SHALL expose a REST API endpoint `GET /issues/:number/agent-session` that returns all persisted messages for a given issue, ordered by step_index and created_at.

#### Scenario: Query agent session for an issue with history
- **WHEN** client requests GET /issues/1/agent-session
- **THEN** the API returns an array of messages with id, role, content, tool_calls, tool_call_id, tool_name, tool_result, step_index, created_at, ordered chronologically

#### Scenario: Query agent session for an issue with no history
- **WHEN** client requests GET /issues/1/agent-session and no messages exist
- **THEN** the API returns an empty array

### Requirement: Database schema for agent session messages
The system SHALL create an `agent_session_message` table with columns: id (TEXT PK), issue_id (TEXT NOT NULL), session_id (TEXT NOT NULL), role (TEXT NOT NULL), content (TEXT), tool_calls (TEXT), tool_call_id (TEXT), tool_name (TEXT), tool_result (TEXT), step_index (INTEGER NOT NULL), message_index (INTEGER NOT NULL), created_at (TEXT NOT NULL). An index SHALL be created on (issue_id, step_index, message_index).

#### Scenario: Database initialization
- **WHEN** the server starts and initializes the database
- **THEN** the agent_session_message table exists with the correct schema and index
