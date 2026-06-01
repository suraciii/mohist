## ADDED Requirements

### Requirement: System info API exposes runtime, source, install, update, service, and path state
The HTTP API SHALL expose `GET /api/system/info` as the typed source of truth for Mohist runtime identity, trusted source repository state, install mode, update eligibility, service state, and existing Mohist paths.

#### Scenario: System info returns typed runtime payload
- **WHEN** a client requests `GET /api/system/info`
- **THEN** the response SHALL include `running.version`, `running.gitHash`, and `running.startedAt` when available
- **AND** the response SHALL include `source.path`, `source.branch`, `source.head`, and `source.dirty`
- **AND** the response SHALL include `install.mode`, `install.serviceManager`, `install.serverUnit`, and `install.runnerUnit`
- **AND** the response SHALL include `update.status`, `update.available`, and `update.reason`
- **AND** the response SHALL include `services.server` and `services.runner`
- **AND** the response SHALL include existing path fields for db, config, logs, and opencode

#### Scenario: Source newer than running reports update available
- **WHEN** the detected local-source repository HEAD differs from the running server git hash
- **AND** the source repository is clean
- **AND** Web update is enabled for the deployment
- **THEN** `GET /api/system/info` SHALL return `update.available = true`
- **AND** `update.status` SHALL be `update-available`

#### Scenario: Unsupported install reports clear reason
- **WHEN** the deployment is not a supported local-source install
- **THEN** `GET /api/system/info` SHALL return `update.available = false`
- **AND** `update.status` SHALL be `unsupported`
- **AND** `update.reason` SHALL explain why Web update is unavailable

### Requirement: System update API starts only trusted local-source updates
The HTTP API SHALL expose `POST /api/system/update` to start a guarded update job only for supported local-source deployments. The request MUST NOT accept or use arbitrary commands, repository paths, unit names, or environment input from the client.

#### Scenario: Eligible update is accepted
- **WHEN** a client requests `POST /api/system/update`
- **AND** the deployment is a supported clean local-source install with update available
- **THEN** the API SHALL start one update job using only the trusted detected source path and fixed command allowlist
- **AND** the response SHALL return an accepted status with a `jobId`

#### Scenario: Unsupported update is rejected
- **WHEN** a client requests `POST /api/system/update`
- **AND** the deployment is not a supported local-source install or System update is disabled
- **THEN** the API SHALL return a 4xx response
- **AND** the response SHALL include a clear unsupported-state message
- **AND** no build or restart command SHALL be started

#### Scenario: Concurrent update is rejected
- **WHEN** an update job is already running
- **AND** a client requests `POST /api/system/update`
- **THEN** the API SHALL return `409 Conflict`
- **AND** it SHALL NOT start a second update job

#### Scenario: Dirty source blocks update start
- **WHEN** the trusted source repository is dirty
- **AND** a client requests `POST /api/system/update`
- **THEN** the API SHALL reject the update for the first iteration
- **AND** the response SHALL explain that the source tree is dirty

### Requirement: System update status API reports latest durable job state
The HTTP API SHALL expose `GET /api/system/update/status` to report the latest update job state and bounded stage logs so clients can reconnect after server restart.

#### Scenario: Update status returns latest job
- **WHEN** a client requests `GET /api/system/update/status`
- **THEN** the API SHALL return the latest persisted update job state when one exists
- **AND** the response SHALL include job id, status, current stage, stage logs, timestamps, and final confirmation fields when available

#### Scenario: No update job exists
- **WHEN** a client requests `GET /api/system/update/status`
- **AND** no update job has been recorded
- **THEN** the API SHALL return a clear empty state

### Requirement: System update executes only fixed server-side commands
The System update API SHALL run only the fixed local-source update flow for the detected Mohist installation: validate local-source install, validate the repository path against the systemd `WorkingDirectory`, check dirty state, run `dotnet build Mohist.sln` in that repository, restart `mohist.service` using `systemctl --user restart mohist.service`, wait for health and Web asset readiness, and restart `mohist-runner.service` when present.

#### Scenario: Fixed build and restart flow is used
- **WHEN** an eligible System update job runs
- **THEN** it SHALL run `dotnet build Mohist.sln` only in the trusted detected repository
- **AND** it SHALL restart only the trusted Mohist server unit using user systemd
- **AND** it SHALL restart the trusted runner unit when present
- **AND** it SHALL NOT use command, path, unit, or environment values supplied by the Web request

#### Scenario: Readiness includes health root and assets
- **WHEN** the server restart command has been issued
- **THEN** the update job SHALL wait for `/api/health`, `/`, and referenced `/assets/*` readiness before reporting `Ready`
- **AND** after reconnect it SHALL confirm whether `running.gitHash` matches `source.head`



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
