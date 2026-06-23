### Requirement: Session followup message endpoint

Server SHALL provide `POST /api/projects/{projectRef}/issues/{number}/sessions/{name}/followup` to let a client inject a free-text followup message into a running agent session. The request body SHALL be `{ text: string }` where `text` is non-empty. On success the response SHALL be `200` with `{ status: "sent" }`. The server SHALL validate session state and runner connectivity before accepting the message.

#### Scenario: Active session accepts followup

- **WHEN** a client sends `POST /api/projects/{projectRef}/issues/{number}/sessions/{name}/followup` with `{ text: "加个登出" }`
- **AND** the session exists and is in an active (running) state
- **AND** the associated runner is connected
- **THEN** the server SHALL push the message to the runner via SignalR
- **AND** the response SHALL be `200` with `{ status: "sent" }`

#### Scenario: Terminal session rejects followup with 409

- **WHEN** a client sends a followup request for a session in a terminal state (completed, failed)
- **THEN** the server SHALL return `409 Conflict`
- **AND** the error SHALL indicate the session is no longer active

#### Scenario: Runner offline rejects followup with 503

- **WHEN** a client sends a followup request for an active session
- **AND** the associated runner has no active SignalR connection
- **THEN** the server SHALL return `503 Service Unavailable`
- **AND** the error SHALL indicate the runner is offline

#### Scenario: Unknown session rejects followup with 404

- **WHEN** a client sends a followup request and no session exists for the given `{name}`
- **THEN** the server SHALL return `404 Not Found`

#### Scenario: Empty text rejected with 400

- **WHEN** a client sends a followup request with `{ text: "" }`, whitespace-only text, or a missing `text` field
- **THEN** the server SHALL return `400 Bad Request`