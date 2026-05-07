## MODIFIED Requirements

### Requirement: Workflow logs are written by workflow visibility observers

Workflow-level session log persistence SHALL be performed through workflow/service observer adapters, not by `AgentSession` directly.

#### Scenario: Session lifecycle logs remain visible
- **WHEN** an agent session starts, completes, fails, times out, or reports process/model lifecycle information
- **THEN** the workflow visibility observer writes the corresponding workflow log entries
- **AND** `AgentSession` does not import or call `WorkflowLogRepo`

#### Scenario: Observer preserves workflow log payload compatibility
- **WHEN** existing consumers query workflow logs for session lifecycle and workflow events
- **THEN** event names and payload shapes remain compatible with the current backend and UI expectations

### Requirement: Session stream logs are written by workflow visibility observers

Session stream event persistence SHALL be performed through workflow/service observer adapters, not by `AgentSession` directly.

#### Scenario: Stream events remain persisted
- **WHEN** ACP emits `agent_thought_chunk`, `agent_message_chunk`, `tool_call`, `tool_call_update`, or `user_message_chunk`
- **THEN** the workflow visibility observer writes the event to `session_stream_log`
- **AND** `AgentSession` does not import or call `SessionStreamLogRepo`
