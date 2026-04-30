## ADDED Requirements

### Requirement: opencode ACP model parameter forwarding
The system SHALL forward the `model` parameter through ACP's `session.setConfigOption` method after `session/new` succeeds, so that opencode uses the user-selected model instead of its internal default.

--- createAcpConnection forwards model
- **WHEN** `createAcpConnection({ model: "anthropic/claude-3-5-sonnet-20241022" })` is called
- **AND** `connection.newSession()` succeeds and returns a sessionId
- **THEN** the system calls `connection.setSessionConfigOption({ scope: 'agent', option: 'model', value: 'anthropic/claude-3-5-sonnet-20241022' })`
- **AND** opencode uses the specified model for this session

--- runAcpSession forwards model
- **WHEN** `runAcpSession({ model: "gpt-4o" })` is called
- **AND** `connection.newSession()` succeeds
- **THEN** the system calls `connection.setSessionConfigOption({ scope: 'agent', option: 'model', value: 'gpt-4o' })`
- **AND** the coding agent uses the specified model

--- model parameter is optional
- **WHEN** `createAcpConnection` or `runAcpSession` is called without a `model` parameter
- **THEN** no `setSessionConfigOption` call is made
- **AND** opencode uses its own default model without error

--- setSessionConfigOption failure does not block session
- **WHEN** `setSessionConfigOption` throws or rejects after `session/new` succeeds
- **THEN** the session continues with opencode's default model
- **AND** a warning is logged but the session is not aborted

### Requirement: opencode ACP oneshot prompt forwards model
Oneshot ACP sessions (explore/fix-build style prompts via `session/prompt` without persistent session) SHALL also forward the `model` parameter via `setSessionConfigOption` after `session/new`.

--- oneshot prompt with model
- **WHEN** `runAcpSession({ model: "anthropic/claude-sonnet-4" })` is called for a oneshot explore task
- **AND** `connection.newSession()` succeeds
- **THEN** `setSessionConfigOption` is called before `session/prompt`
- **AND** the oneshot agent uses the specified model

## MODIFIED Requirements

### Requirement: Coder session mapping persisted on spawn

When `spawn_coder` tool executes and creates an ACP session, the system SHALL record the mapping of issue_id, acp_session_id, execution_id, and a truncated task description to the `coder_session` table with status 'running'. The `coder_tool_call` SSE event SHALL additionally carry `rawInput`, `rawOutput`, and `title` fields so that the WebUI can display tool call details without querying the workflow_log API.

#### Scenario: Spawn coder creates ACP session
- **WHEN** runAcpSession successfully initializes ACP and obtains a sessionId (after `connection.newSession` succeeds)
- **THEN** a coder_session row is created with issue_id (UUID), acp_session_id, execution_id, truncated task (max 200 chars), status='running', and created_at

--- coder_session_started event includes model
- **WHEN** runAcpSession creates a new ACP session
- **AND** a `model` parameter was provided
- **THEN** the `coder_session_started` event includes the `model` field with the selected model identifier
- **AND** the `coder_session` table row records the model if the database schema supports it