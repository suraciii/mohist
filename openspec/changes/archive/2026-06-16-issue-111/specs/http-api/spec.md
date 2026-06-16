## ADDED Requirements

### Requirement: Runtime consistency verification API
The HTTP API SHALL expose a runtime consistency endpoint that reports whether the CLI, Server, Web assets, Runner, and managed skill assets are coherent and usable.

#### Scenario: All components consistent
- **WHEN** a client requests `GET /api/system/consistency`
- **THEN** the response SHALL report the consistency status of each component: CLI, Server, Web assets, Runner, managed skill assets
- **AND** the top-level status SHALL be `consistent` when all components agree

#### Scenario: Component mismatch detected
- **WHEN** the managed skill asset manifest version differs from the running server version
- **THEN** `GET /api/system/consistency` SHALL report managed skill assets as `mismatched`
- **AND** the top-level status SHALL be `inconsistent`

#### Scenario: Runner disconnected
- **WHEN** the runner service is not reporting active connection
- **THEN** `GET /api/system/consistency` SHALL report the runner as `unavailable`

### Requirement: Update outcome persistence
The Server SHALL persist the outcome of CLI-triggered `mo update` jobs so the Web UI can display the latest update outcome.

#### Scenario: CLI update outcome is persisted
- **WHEN** `mo update` completes via the CLI
- **THEN** the server SHALL persist the update outcome via `POST /api/system/update/outcome`
- **AND** `GET /api/system/update/status` SHALL return the latest outcome

#### Scenario: CLI update outcome supersedes stale Web-triggered job
- **WHEN** a CLI-triggered update completes
- **AND** an older Web-triggered job has status `waiting-for-reconnect`
- **THEN** `GET /api/system/update/status` SHALL return the CLI update as the latest outcome
- **AND** SHALL NOT present the stale Web job as current truth

## MODIFIED Requirements

### Requirement: System update status API reports latest durable job state
The HTTP API SHALL expose `GET /api/system/update/status` to report the latest update job state and bounded stage logs so clients can reconnect after server restart. The endpoint SHALL reconcile stale states: when a persisted `waiting-for-reconnect` job belongs to a runtime that has already advanced past that job's source HEAD, the endpoint SHALL mark the job as `superseded`.

#### Scenario: Update status returns latest job
- **WHEN** a client requests `GET /api/system/update/status`
- **THEN** the API SHALL return the latest persisted update job state when one exists
- **AND** the response SHALL include job id, status, current stage, stage logs, timestamps, and final confirmation fields when available

#### Scenario: No update job exists
- **WHEN** a client requests `GET /api/system/update/status`
- **AND** no update job has been recorded
- **THEN** the API SHALL return a clear empty state

#### Scenario: Stale waiting-for-reconnect is superseded
- **WHEN** `GET /api/system/update/status` fetches a job with status `waiting-for-reconnect`
- **AND** the running server git hash differs from the job's `sourceHead`
- **AND** the running server git hash is not empty
- **THEN** the API SHALL change the job status to `superseded`
- **AND** SHALL persist the superseded state
- **AND** the response SHALL indicate the job is no longer relevant

#### Scenario: Active waiting-for-reconnect is preserved
- **WHEN** `GET /api/system/update/status` fetches a job with status `waiting-for-reconnect`
- **AND** the running server git hash matches the job's `sourceHead` or is empty
- **THEN** the API SHALL preserve the `waiting-for-reconnect` status
- **AND** SHALL continue readiness probing
