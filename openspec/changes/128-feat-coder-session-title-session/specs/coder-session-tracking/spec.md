## MODIFIED Requirements

### Requirement: Coder session mapping persisted on spawn
When `spawn_coder` tool executes and creates an ACP session, the system SHALL record the mapping of issue_id, acp_session_id, execution_id, title, and a truncated task description to the `coder_session` table with status 'running'. The `coder_tool_call` SSE event SHALL additionally carry `rawInput`, `rawOutput`, and `title` fields so that the WebUI can display tool call details without querying the workflow_log API.

#### Scenario: Spawn coder creates ACP session
- **WHEN** runAcpSession successfully initializes ACP and obtains a sessionId (after `connection.newSession` succeeds)
- **THEN** a coder_session row is created with issue_id (UUID), acp_session_id, execution_id, title, truncated task (max 200 chars), status='running', and created_at

#### Scenario: Spawn coder creates ACP session with title
- **WHEN** runAcpSession is called with `title: "T-004: Create Plan"` and successfully creates a session
- **THEN** the coder_session row has `title: "T-004: Create Plan"`

#### Scenario: createAcpConnection creates session with title
- **WHEN** createAcpConnection is called with `title: "Plan stage"` and successfully creates a session
- **THEN** the coder_session row has `title: "Plan stage"`
