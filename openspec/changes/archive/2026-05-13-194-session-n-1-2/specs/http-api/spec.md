## MODIFIED Requirements

### Requirement: Issue coder session list endpoint returns summary metadata only

`GET /api/issues/:number/coder-sessions` SHALL return only the session summary metadata needed by the issue detail surface and SHALL NOT load or embed per-session transcript or workflow log payloads.

#### Scenario: List response excludes workflow logs and transcript payloads

- **WHEN** the client requests the coder session list for an issue
- **THEN** the response includes only lightweight session metadata needed for the list surface
- **AND** the response does not include `workflowLogs`, transcript fragments, or other per-session log payloads

#### Scenario: List path does not perform per-session log loading

- **WHEN** the server handles `GET /api/issues/:number/coder-sessions`
- **THEN** it reads session summaries without issuing per-session `session_stream_log` or `workflow_log` queries

#### Scenario: Dedicated detail endpoint remains the source of full session data

- **WHEN** the client requests `GET /api/issues/:number/coder-sessions/:sessionId`
- **THEN** the response still includes the full transcript and log-backed detail needed for session inspection

#### Scenario: High-session-count issue stays within the latency budget

- **WHEN** an issue has 50 or more coder sessions
- **THEN** `GET /api/issues/:number/coder-sessions` completes within 1 second in the project verification environment
