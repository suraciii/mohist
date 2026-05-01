## ADDED Requirements

### Requirement: Agent sessions API endpoint
Server SHALL provide `GET /api/agent/sessions` endpoint that returns a cross-issue list of coder sessions for the current project. Each session entry SHALL include: issue info (number, title, stage), session status (running/completed/failed), model, taskDescription (truncated to 200 chars), createdAt, completedAt, and lastActivityAt.

#### Scenario: List all sessions
- **WHEN** `GET /api/agent/sessions` is called
- **THEN** returns an array of session objects for all issues in the current project
- **AND** each object contains `{ issueNumber, issueTitle, issueStage, sessionId, status, model, taskDescription, createdAt, completedAt, lastActivityAt }`
- **AND** results are ordered by `createdAt` descending

#### Scenario: Filter by status
- **WHEN** `GET /api/agent/sessions?status=running` is called
- **THEN** returns only sessions with `status = 'running'`
- **AND** the `status` query parameter supports values: `running`, `completed`, `failed`

#### Scenario: Limit results
- **WHEN** `GET /api/agent/sessions?limit=10` is called
- **THEN** returns at most 10 session objects
- **AND** default limit is 50 when parameter is omitted

#### Scenario: Last activity derived from workflow_log
- **WHEN** a coder session has workflow_log entries
- **THEN** `lastActivityAt` is the `created_at` of the most recent workflow_log record for that session
- **AND** if no workflow_log entries exist, `lastActivityAt` is `null`

#### Scenario: No sessions exist
- **WHEN** `GET /api/agent/sessions` is called and no coder sessions exist
- **THEN** returns an empty array `[]`

#### Scenario: Combined filter and limit
- **WHEN** `GET /api/agent/sessions?status=running&limit=5` is called
- **THEN** returns at most 5 running sessions
