## MODIFIED Requirements

### Requirement: Coder session tracking is observer-owned

`coder_session` persistence and status updates SHALL be performed by workflow/service observers, not by `AgentSession` or other agent-runtime modules directly.

#### Scenario: Coder session row is created through observer
- **WHEN** a visible agent session starts with an issue ID and coder session repository available
- **THEN** the workflow visibility observer creates the `coder_session` row with ACP session ID, execution ID, task description, stage, title, process PID, and model where available
- **AND** `AgentSession` does not import or call `CoderSessionRepo`

#### Scenario: Terminal session status is updated through observer
- **WHEN** a session transitions to completed, failed, timeout, or cancelled
- **THEN** the workflow visibility observer updates `coder_session.status`
- **AND** the Web UI can observe the terminal status without stale running state

### Requirement: Coder realtime events preserve payload compatibility

`coder_text_chunk` and `coder_tool_call` realtime events SHALL continue to be emitted by workflow visibility observers with the same payload semantics after the runtime boundary cleanup.

#### Scenario: Text chunk issue identity is preserved
- **WHEN** a text chunk is emitted for a session with `issueNumber`
- **THEN** the `coder_text_chunk.issueId` payload uses the issue number string
- **AND** persistence still uses the issue UUID where applicable

#### Scenario: Tool call payload remains complete and deduplicable
- **WHEN** ACP emits tool call start or completion data
- **THEN** `coder_tool_call` includes stable `toolCallId`, tool name, state, title, rawInput, and rawOutput where available
- **AND** the same tool call can be deduplicated by `toolCallId`
