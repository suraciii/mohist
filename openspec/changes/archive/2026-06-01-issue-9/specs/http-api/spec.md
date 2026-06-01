## ADDED Requirements

### Requirement: Runner status API exposes UI-facing runner rows
The HTTP API SHALL expose a stable runner status endpoint for the selected project, such as `GET /api/runners` or `GET /api/agent/runners`. The endpoint SHALL return UI-facing runner rows backed by a server-side projection rather than requiring clients to query individual runner runtime actors.

Each row SHALL include runner id, kind, hostname when known, scope, status, registration or connection time when known, last heartbeat, SignalR connection state when known, capabilities, coder model names and count, capacity or slot data when available, and active work assignment when present.

#### Scenario: Query runner status for current project
- **WHEN** a client requests the runner status endpoint for the current project
- **THEN** the response includes runner rows for connected or known runners that can serve that project
- **AND** each row uses user-facing runner terminology rather than agent terminology

#### Scenario: Empty runner status response
- **WHEN** no runner is connected or known for the selected project
- **THEN** the response returns an empty runner list with enough metadata for the UI to render a no-runner state

#### Scenario: Busy runner response includes active work
- **WHEN** a runner is processing workflow work
- **THEN** the corresponding runner row includes active work information when available
- **AND** the response does not include agent session transcript details

### Requirement: Runner status API includes global and project-scoped runners
The runner status API SHALL include both global runners and runners scoped to the selected project. Runners scoped only to other projects SHALL NOT be returned for the selected project.

#### Scenario: Global and project runner included
- **WHEN** one runner is global
- **AND** another runner is scoped to the selected project
- **THEN** the runner status API returns both runners
- **AND** their rows distinguish `global` scope from the project-specific scope

#### Scenario: Different project runner excluded
- **WHEN** a runner is scoped only to a different project
- **THEN** the runner status API does not include that runner in the selected project's response

### Requirement: Agent status compatibility preserves existing consumers
`GET /api/agent/status` SHALL remain compatible for existing clients while runner status is added or migrated. If the response continues to include runner availability, it SHALL preserve the existing minimal runner fields or provide an explicitly compatible migration path covered by tests.

#### Scenario: Existing agent status runner fields remain readable
- **WHEN** an existing client requests `GET /api/agent/status`
- **THEN** the response remains parseable by clients expecting runner availability and a minimal runner list
- **AND** the addition of detailed runner status does not remove existing status fields without a tested compatibility migration

#### Scenario: Agent status points to runner status when migrated
- **WHEN** detailed runner information is served from a new runner status endpoint
- **THEN** `GET /api/agent/status` remains compatible
- **AND** clients can discover or continue using the stable runner status endpoint without breaking existing status behavior

### Requirement: Runner status API regression coverage
Runner status HTTP behavior SHALL have backend regression coverage for projection shape, global versus project-scoped inclusion, empty responses, active work, stale or offline liveness, sensitive-data exclusion, and agent status compatibility.

#### Scenario: Projection shape is covered
- **WHEN** backend API tests exercise runner status
- **THEN** tests verify runner id, host, scope, liveness or heartbeat, capabilities, coder models, and active work fields

#### Scenario: Scope filtering is covered
- **WHEN** backend API tests create global, selected-project, and other-project runners
- **THEN** tests verify that only global and selected-project runners are returned for the selected project

#### Scenario: Stale or offline liveness is covered
- **WHEN** backend API tests exercise a runner without fresh heartbeat or live connection state
- **THEN** tests verify the runner is distinguishable as stale or offline

#### Scenario: Sensitive data exclusion is covered
- **WHEN** backend API tests exercise runner status rows with capabilities, models, and active work
- **THEN** tests verify environment variables, tokens, secrets, and agent transcript details are not returned

#### Scenario: Compatibility is covered
- **WHEN** backend API tests request `GET /api/agent/status`
- **THEN** tests verify existing runner availability behavior remains compatible
