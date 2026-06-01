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
