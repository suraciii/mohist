## ADDED Requirements

### Requirement: Launch generic AgentSession from an Agent profile endpoint

Server SHALL provide `POST /api/projects/{projectRef}/agents/{agentRef}/sessions` to launch a generic `AgentSession` from a project-scoped Agent profile. The request body SHALL be `{ prompt: string }` where `prompt` is non-empty, and MAY include an optional `context` object carrying context references (issue, epic, repository, workspace path). On success the response SHALL be `201` with `{ sessionId, agentId, agentName, status }`. The endpoint SHALL resolve the Agent in the project, combine the Agent's `Instructions` and `AgentConfig` with the caller's prompt, execute the prompt via a standalone AgentJob that records a generic `AgentSession`, and return the new session identity and current status. The endpoint SHALL be distinct from the validation-only `POST /api/agent-jobs/validate` route, which remains a developer smoke-test surface and SHALL NOT be treated as the product API.

#### Scenario: Launch returns the new session id and status

- **WHEN** a client sends `POST /api/projects/{projectRef}/agents/{agentRef}/sessions` with `{ prompt: "Refactor the auth module" }`
- **AND** the agent resolves in the project
- **THEN** the server SHALL combine the Agent's `Instructions` and `AgentConfig` with the prompt
- **AND** SHALL execute the prompt via a standalone AgentJob that records a generic `AgentSession`
- **AND** the response SHALL be `201` with `{ sessionId, agentId, agentName, status }`

#### Scenario: Launch with optional context references records them as metadata

- **WHEN** a client sends `POST /api/projects/{projectRef}/agents/{agentRef}/sessions` with `{ prompt: "...", context: { issueNumber: 42, repository: "feature-repo", workspacePath: "/repo" } }`
- **THEN** the server SHALL record the supplied context references in the resulting `AgentSession` metadata as prompt context
- **AND** the context references SHALL NOT create scope, mount, or supervisor lifecycle

#### Scenario: Unknown agent is rejected with 404

- **WHEN** a client sends `POST /api/projects/{projectRef}/agents/{agentRef}/sessions` and `{agentRef}` does not resolve to an Agent in the project
- **THEN** the server SHALL return `404 Not Found`
- **AND** no `AgentSession` SHALL be created

#### Scenario: Empty prompt rejected with 400

- **WHEN** a client sends a launch request with `{ prompt: "" }`, whitespace-only prompt, or a missing `prompt` field
- **THEN** the server SHALL return `400 Bad Request`
- **AND** no AgentJob SHALL be submitted

#### Scenario: Launch is distinct from the validation-only agent-jobs route

- **WHEN** a client uses the product launch endpoint
- **THEN** the endpoint SHALL be `POST /api/projects/{projectRef}/agents/{agentRef}/sessions`
- **AND** the endpoint SHALL NOT be the validation-only `POST /api/agent-jobs/validate` route
- **AND** the validation-only route SHALL remain unchanged as a developer smoke-test surface

### Requirement: Generic AgentSession followup endpoint

Server SHALL provide `POST /api/projects/{projectRef}/agent-sessions/{sessionId}/followup` to inject a free-text followup message into a running generic `AgentSession`. The request body SHALL be `{ text: string }` where `text` is non-empty. On success the response SHALL be `200` with `{ status: "sent" }`. The server SHALL validate session state and runner connectivity before accepting the message. This endpoint SHALL be distinct from the existing issue-scoped `POST /api/projects/{projectRef}/issues/{number}/sessions/{name}/followup` route, which SHALL remain unchanged.

#### Scenario: Active generic session accepts followup

- **WHEN** a client sends `POST /api/projects/{projectRef}/agent-sessions/{sessionId}/followup` with `{ text: "add a logout route" }`
- **AND** the generic session exists and is in an active (running) state
- **AND** the associated runner is connected
- **THEN** the server SHALL push the message to the runner via SignalR using a session target that identifies the generic session
- **AND** the response SHALL be `200` with `{ status: "sent" }`

#### Scenario: Terminal generic session rejects followup with 409

- **WHEN** a client sends a followup request for a generic session in a terminal state (completed, failed, stopped)
- **THEN** the server SHALL return `409 Conflict`
- **AND** the error SHALL indicate the session is no longer active

#### Scenario: Runner offline rejects followup with 503

- **WHEN** a client sends a followup request for an active generic session
- **AND** the associated runner has no active SignalR connection
- **THEN** the server SHALL return `503 Service Unavailable`
- **AND** the error SHALL indicate the runner is offline

#### Scenario: Unknown session rejects followup with 404

- **WHEN** a client sends a followup request and no generic session exists for the given `{sessionId}`
- **THEN** the server SHALL return `404 Not Found`

#### Scenario: Empty text rejected with 400

- **WHEN** a client sends a followup request with `{ text: "" }`, whitespace-only text, or a missing `text` field
- **THEN** the server SHALL return `400 Bad Request`

#### Scenario: Issue-scoped followup route remains unchanged

- **WHEN** a client sends `POST /api/projects/{projectRef}/issues/{number}/sessions/{name}/followup`
- **THEN** the server SHALL behave exactly as before this change
- **AND** the issue-scoped route SHALL remain unchanged and distinct from the generic-session followup route

### Requirement: Generic AgentSession cancel endpoint

Server SHALL provide `POST /api/projects/{projectRef}/agent-sessions/{sessionId}/cancel` to request cancellation of a running generic `AgentSession`. The server SHALL attempt to cancel the running turn and SHALL return the resulting session state explicitly. If the underlying agent cannot be cancelled the response SHALL state that the session is not currently cancellable; if the session is already terminal the response SHALL return the current terminal state. The response SHALL NOT pretend success when cancellation is not possible.

#### Scenario: Cancellable active session returns resulting state

- **WHEN** a client sends `POST /api/projects/{projectRef}/agent-sessions/{sessionId}/cancel` for an active generic session whose underlying agent supports cancellation
- **THEN** the server SHALL attempt to cancel the running turn
- **AND** the response SHALL reflect the resulting session state

#### Scenario: Non-cancellable agent is reported honestly

- **WHEN** a client sends a cancel request for an active session whose underlying agent does not support cancellation
- **THEN** the server SHALL return a state indicating the session is not currently cancellable
- **AND** the response SHALL NOT pretend the cancellation succeeded

#### Scenario: Terminal session returns its terminal state

- **WHEN** a client sends a cancel request for a generic session that is already in a terminal state (completed, failed, stopped)
- **THEN** the server SHALL return the current terminal state
- **AND** the response SHALL NOT report a fresh cancellation

#### Scenario: Unknown session rejects cancel with 404

- **WHEN** a client sends a cancel request and no generic session exists for the given `{sessionId}`
- **THEN** the server SHALL return `404 Not Found`
