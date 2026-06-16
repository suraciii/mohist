## ADDED Requirements

### Requirement: Server supports update outcome reporting
The Server SHALL accept and persist update outcomes from CLI-triggered updates so the Web UI and status API surfaces can read the latest outcome.

#### Scenario: CLI update outcome is persisted
- **WHEN** the CLI sends `POST /api/system/update/outcome` with stage, status, and outcome data
- **THEN** the server SHALL persist the outcome to the system update store
- **AND** `GET /api/system/update/status` SHALL return the CLI outcome as the latest job state

#### Scenario: CLI outcome supersedes stale Web job
- **WHEN** a CLI-triggered update outcome is persisted
- **AND** a Web-triggered job has status `waiting-for-reconnect` with an older source HEAD
- **THEN** the web job SHALL be marked as `superseded`
- **AND** the CLI outcome SHALL be returned as the latest status

## MODIFIED Requirements

### Requirement: Server-side update restarts trusted Mohist services only
When a local-source System update is started, the server SHALL restart only the trusted Mohist user systemd units detected from installation facts. It MUST NOT accept service names, commands, paths, or environment values from Web clients. After server restart, the update flow SHALL restore the trusted runner unit when it was running before the update began.

#### Scenario: Server service restart uses fixed unit
- **WHEN** an eligible local-source update reaches the restart stage
- **THEN** the server SHALL run a user systemd restart for the trusted `mohist.service` unit
- **AND** it SHALL NOT restart arbitrary units requested by the client

#### Scenario: Runner restart keeps services aligned
- **WHEN** the trusted runner unit is present during update
- **THEN** the update flow SHALL restart `mohist-runner.service` so the runner and server versions stay aligned
- **AND** runner restart status SHALL be reflected in update job state or service state

#### Scenario: Runner is restored after server restart failure
- **WHEN** the server restart or readiness check fails during a Web-triggered update
- **AND** the trusted runner unit was present before the update
- **THEN** the update flow SHALL attempt to restart `mohist-runner.service`
- **AND** the runner restore outcome SHALL be reflected in the update job state

### Requirement: Server daemon readiness supports post-update reconnect
After a Web-initiated update restarts the server, daemon readiness SHALL be observable through health and Web asset requests so the Web UI can reconnect safely. Readiness verification SHALL include confirmation that the running server identity matches the expected source HEAD after reconnect.

#### Scenario: Restarted server becomes ready for Web reconnect
- **WHEN** the server has been restarted by a System update
- **THEN** `GET /api/health` SHALL return healthy status when the API is ready
- **AND** the Web root `/` and referenced `/assets/*` SHALL be served successfully before the update job reports Ready

#### Scenario: Server identity matches source after restart
- **WHEN** the server becomes reachable after restart
- **THEN** the update job SHALL compare the running server git hash against the source HEAD
- **AND** SHALL report ready only when the identity matches
- **AND** SHALL report recovered-with-warning when identity does not match but health and assets are ready
