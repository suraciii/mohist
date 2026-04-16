## ADDED Requirements

### Requirement: Coder session mapping persisted on spawn
When `spawn_coder` tool executes and creates an ACP session, the system SHALL record the mapping of issue_id, acp_session_id, execution_id, and a truncated task description to the `coder_session` table with status 'running'.

#### Scenario: Spawn coder creates ACP session
- **WHEN** runAcpSession successfully initializes ACP and obtains a sessionId (after `connection.newSession` succeeds)
- **THEN** a coder_session row is created with issue_id, acp_session_id, execution_id, truncated task (max 200 chars), status='running', and created_at
- **AND** AcpSessionOptions is extended to accept a `coderSessionRepo` instance

### Requirement: Coder session status updated on completion
When a coder ACP session completes (success, failure, or timeout after session creation), the system SHALL update the corresponding coder_session row status to 'completed' or 'failed' and set completed_at.

#### Scenario: Coder session completes successfully
- **WHEN** runAcpSession returns with success=true
- **THEN** the coder_session row is updated to status='completed' and completed_at is set

#### Scenario: Coder session fails or times out after creation
- **WHEN** runAcpSession returns with success=false, or times out during prompt/execution after the sessionId was obtained
- **THEN** the coder_session row is updated to status='failed' and completed_at is set

#### Scenario: ACP connection fails before session creation
- **WHEN** runAcpSession times out during initialize or newSession (before sessionId is obtained)
- **THEN** no coder_session row is created, because no valid ACP session was established

### Requirement: Coder sessions queryable by issue
The system SHALL expose a REST API endpoint `GET /issues/:number/coder-sessions` that returns all coder sessions for a given issue, including their workflow_log entries filtered by acp_session_id.

#### Scenario: Query coder sessions for an issue
- **WHEN** client requests GET /issues/1/coder-sessions
- **THEN** the API returns an array of coder sessions, each with id, acp_session_id, task_description, status, created_at, completed_at, and an array of workflow_log entries for that session

#### Scenario: Query coder sessions for issue with none
- **WHEN** client requests GET /issues/1/coder-sessions and no coder sessions exist
- **THEN** the API returns an empty array

### Requirement: Database schema for coder session mapping
The system SHALL create a `coder_session` table with columns: id (TEXT PK), issue_id (TEXT NOT NULL), acp_session_id (TEXT NOT NULL), execution_id (TEXT), task_description (TEXT), status (TEXT NOT NULL DEFAULT 'running'), created_at (TEXT NOT NULL), completed_at (TEXT). An index SHALL be created on issue_id.

#### Scenario: Database initialization
- **WHEN** the server starts and initializes the database
- **THEN** the coder_session table exists with the correct schema and index
