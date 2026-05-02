## MODIFIED Requirements

### Requirement: Coder session mapping persisted on spawn
When `spawn_coder` tool executes and creates an ACP session, the system SHALL record the mapping of issue_id, acp_session_id, execution_id, a truncated task description, and a human-readable title to the `coder_session` table with status 'running'. The `coder_tool_call` SSE event SHALL additionally carry `rawInput`, `rawOutput`, and `title` fields so that the WebUI can display tool call details without querying the workflow_log API.

#### Scenario: Spawn coder creates ACP session
- **WHEN** runAcpSession successfully initializes ACP and obtains a sessionId (after `connection.newSession` succeeds)
- **THEN** a coder_session row is created with issue_id (UUID), acp_session_id, execution_id, truncated task (max 200 chars), title (from options.title or NULL), status='running', and created_at

#### Scenario: Spawn coder creates ACP session with title
- **WHEN** runAcpSession is called with `options.title = "T-004: Create Plan"`
- **THEN** the coder_session row is created with `title = "T-004: Create Plan"`
