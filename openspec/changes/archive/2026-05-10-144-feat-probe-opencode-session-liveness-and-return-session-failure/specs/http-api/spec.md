## MODIFIED Requirements

### Requirement: REQ-API-001 API exposes current session liveness data

Issue/session API responses SHALL expose current session call state and liveness fields needed by CLI and Web clients.

#### Scenario: Coder session list includes liveness fields
- **WHEN** a client requests an issue's coder sessions
- **THEN** each session item SHALL include status, `lastDataAt`, `probeSentAt`, `probeDeadlineAt`, and `failureReason`

#### Scenario: Coder session detail includes liveness metadata
- **WHEN** a client requests a coder session detail transcript
- **THEN** metadata SHALL include status, `lastDataAt`, `probeSentAt`, `probeDeadlineAt`, and `failureReason`
- **AND** `probing` SHALL be represented as the current checking-session state

#### Scenario: Agent/session status exposes active session state
- **WHEN** a client requests agent or issue status data for an issue
- **THEN** the response SHALL include enough current-session data to distinguish Running, Checking session, Session failed, and No active session

#### Scenario: API does not expose health taxonomy
- **WHEN** API responses include session liveness state
- **THEN** they SHALL NOT expose healthy, quiet, stale, hung-suspected, or recoverable as authoritative session states
