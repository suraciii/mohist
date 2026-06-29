## ADDED Requirements

### Requirement: Agent-scoped session list endpoint

Server SHALL provide `GET /api/projects/{projectRef}/agents/{agentRef}/sessions` to list the generic `AgentSession`s belonging to a project-scoped Agent profile. The endpoint SHALL resolve `{agentRef}` (agent name or `agent_*` id) within the project, and SHALL return only that agent's generic `agent-launch` sessions ordered by recency. The endpoint SHALL accept an optional `status` query parameter that filters the result to sessions whose status is within the requested set (covering at least `running`, `completed`, `failed`, `stopped`). On success the response SHALL be `200` with a list of session summaries. Each summary SHALL carry the session id, the agent id and agent name, the status, the created timestamp, the last-activity timestamp, and the resolved model. The endpoint SHALL be distinct from the existing workflow-session list endpoint and SHALL NOT return workflow-shaped sessions. Unknown `{agentRef}` SHALL be rejected with `404`.

#### Scenario: List an agent's sessions

- **WHEN** a client sends `GET /api/projects/{projectRef}/agents/{agentRef}/sessions`
- **AND** the agent resolves in the project
- **THEN** the server SHALL return `200` with that agent's generic sessions ordered by recency
- **AND** each entry SHALL carry the session id, agent id, agent name, status, created timestamp, last-activity timestamp, and resolved model

#### Scenario: List with a status filter

- **WHEN** a client sends `GET /api/projects/{projectRef}/agents/{agentRef}/sessions?status=failed`
- **THEN** the server SHALL return only that agent's generic sessions whose status is `failed`
- **AND** sessions with other statuses SHALL be excluded

#### Scenario: List is distinct from the workflow-session list

- **WHEN** a client requests the agent-scoped list
- **THEN** the response SHALL contain only generic `agent-launch` sessions
- **AND** SHALL NOT contain workflow-shaped sessions

#### Scenario: Unknown agent is rejected with 404

- **WHEN** a client sends `GET /api/projects/{projectRef}/agents/{agentRef}/sessions` and `{agentRef}` does not resolve to an Agent in the project
- **THEN** the server SHALL return `404 Not Found`

### Requirement: Generic AgentSession summary endpoint

Server SHALL provide `GET /api/projects/{projectRef}/agent-sessions/{sessionId}` to read the summary of a generic `AgentSession`. The response SHALL carry the agent id and agent name, the status, the created and last-activity timestamps, the resolved model, the usage metrics, the failure category (when present), the tool call count and tool error count, and any recorded context references (issue, epic, repository, workspace path). The response SHALL NOT fabricate workflow-only fields; workflow-shaped fields that have no value for a generic session SHALL be absent or null. The endpoint SHALL be distinct from the existing issue-scoped session metadata endpoint (`GET /api/projects/{projectRef}/issues/{number}/sessions/{name}`). A `{sessionId}` that does not resolve to a generic `agent-launch` session in the project SHALL return `404`, and SHALL NOT return a workflow session even if the id matches.

#### Scenario: Summary returns the enriched generic session read

- **WHEN** a client sends `GET /api/projects/{projectRef}/agent-sessions/{sessionId}` for an existing generic session
- **THEN** the server SHALL return `200` with the session summary
- **AND** the summary SHALL carry agent id, agent name, status, created and last-activity timestamps, resolved model, usage, failure category (when present), tool call count, tool error count, and recorded context references

#### Scenario: Summary omits fabricated workflow fields

- **WHEN** a client reads a generic session that does not belong to a workflow run
- **THEN** the response SHALL NOT present a fabricated workflow run id, session name, work id, work type, or stage
- **AND** any workflow-shaped field with no value SHALL be absent or null

#### Scenario: Summary is distinct from the issue-scoped session endpoint

- **WHEN** a client requests the generic session summary
- **THEN** the endpoint SHALL be `GET /api/projects/{projectRef}/agent-sessions/{sessionId}`
- **AND** SHALL NOT be the existing `GET /api/projects/{projectRef}/issues/{number}/sessions/{name}` route
- **AND** the issue-scoped route SHALL remain unchanged

#### Scenario: Unknown session id is rejected with 404

- **WHEN** a client sends `GET /api/projects/{projectRef}/agent-sessions/{sessionId}` and `{sessionId}` does not resolve to a generic `agent-launch` session in the project
- **THEN** the server SHALL return `404 Not Found`
- **AND** SHALL NOT return a workflow session even if the id matches

### Requirement: Activity feed attributes generic sessions by agent

The activity endpoint SHALL return generic `agent-launch` sessions as Agent activity attributed to their Agent profile, and SHALL NOT synthesize an `issue_{projectId}_0` (or any issue-number-zero) identity for a generic session that has no issue reference. Each activity card for a generic session SHALL carry the agent id and agent name of the producing Agent profile. A generic session with an issue context reference MAY appear associated with that issue, but its card attribution SHALL reflect the Agent profile. Workflow-session activity cards SHALL remain unchanged.

#### Scenario: Generic session card carries agent identity

- **WHEN** the activity endpoint returns a card for a generic `agent-launch` session
- **THEN** the card SHALL carry the agent id and agent name of the producing Agent profile

#### Scenario: Generic session without an issue reference produces no synthetic issue card

- **WHEN** the activity endpoint returns a card for a generic session that has no issue context reference
- **THEN** the card SHALL NOT use an `issue_{projectId}_0` or issue-number-zero identity
- **AND** the card SHALL be attributable by agent identity

#### Scenario: Workflow activity cards are preserved

- **WHEN** the activity endpoint returns cards for workflow sessions
- **THEN** those cards SHALL behave exactly as before this change

### Requirement: Active-agents readout includes generic agent-launch sessions

The active-agents readout endpoint SHALL include generic `agent-launch` sessions that are currently active, and SHALL NOT exclude a session solely because it has a blank workflow run id or work id. An active-agent entry for a generic session SHALL attribute the session to its Agent profile and SHALL NOT require a workflow-run-derived work item to report progress. Workflow-session active-agent entries SHALL remain unchanged.

#### Scenario: Active generic session is included

- **WHEN** the active-agents readout is requested for a project that has an active generic `agent-launch` session
- **THEN** the response SHALL include that session
- **AND** SHALL NOT exclude it for having a blank workflow run id or work id

#### Scenario: Generic active-agent entry is agent-attributed

- **WHEN** the active-agents readout includes a generic session
- **THEN** the entry SHALL attribute the session to its Agent profile
- **AND** SHALL NOT require a workflow-run-derived work item to report progress

#### Scenario: Workflow active-agent entries are preserved

- **WHEN** the active-agents readout includes workflow sessions
- **THEN** those entries SHALL behave exactly as before this change

### Requirement: Issue and epic agent-session association read endpoints

Server SHALL provide read endpoints that surface the generic `AgentSession`s associated with an issue or an epic via their recorded `agent-launch/*` context references, so a client can discover related Agent sessions and navigate back to them. The issue endpoint SHALL be `GET /api/projects/{projectRef}/issues/{number}/agent-sessions` and the epic endpoint SHALL be `GET /api/projects/{projectRef}/epics/{epicRef}/agent-sessions`. Each SHALL return a list of lightweight association entries, where each entry carries the session id, the agent id and agent name, the status, and the created timestamp, and a link back to the session summary. The endpoints SHALL be read-only and SHALL NOT create scope, mount, supervisor, ownership, or workflow lifecycle. An issue or epic with no associated sessions SHALL return `200` with an empty list.

#### Scenario: Issue association list returns related sessions

- **WHEN** a client sends `GET /api/projects/{projectRef}/issues/{number}/agent-sessions`
- **AND** generic sessions reference that issue via the `mohist.io/agent-launch/issue-number` label
- **THEN** the server SHALL return `200` with a list of association entries
- **AND** each entry SHALL carry the session id, agent id, agent name, status, created timestamp, and a link back to the session

#### Scenario: Epic association list returns related sessions

- **WHEN** a client sends `GET /api/projects/{projectRef}/epics/{epicRef}/agent-sessions`
- **AND** generic sessions reference that epic via the `mohist.io/agent-launch/epic-number` label
- **THEN** the server SHALL return `200` with a list of association entries
- **AND** each entry SHALL carry the session id, agent id, agent name, status, created timestamp, and a link back to the session

#### Scenario: No associated sessions returns an empty list

- **WHEN** a client requests an issue or epic agent-session association list and no generic session references that entity
- **THEN** the server SHALL return `200` with an empty list

#### Scenario: Association read is read-only

- **WHEN** a client requests an association list
- **THEN** the endpoint SHALL NOT create scope, mount, supervisor, ownership, or workflow lifecycle
- **AND** the endpoint SHALL NOT mutate the issue or epic

### Requirement: Generic AgentSession summary reuses the existing transcript read path

Server SHALL expose the transcript and runtime-event read path for a generic `AgentSession` through the generic-session route, reusing the existing transcript query capability so a direct-Agent session's transcript, runtime events, and failure detail are readable the same way workflow sessions are. The transcript endpoint SHALL be `GET /api/projects/{projectRef}/agent-sessions/{sessionId}/transcript`. The endpoint SHALL NOT require a workflow run id or session name to resolve the transcript. A `{sessionId}` that does not resolve to a generic `agent-launch` session in the project SHALL return `404`.

#### Scenario: Transcript is reachable by session id

- **WHEN** a client sends `GET /api/projects/{projectRef}/agent-sessions/{sessionId}/transcript` for an existing generic session
- **THEN** the server SHALL return `200` with the transcript turns and runtime events
- **AND** SHALL NOT require a workflow run id or session name

#### Scenario: Transcript endpoint rejects unknown session with 404

- **WHEN** a client requests the transcript for a `{sessionId}` that does not resolve to a generic session in the project
- **THEN** the server SHALL return `404 Not Found`
