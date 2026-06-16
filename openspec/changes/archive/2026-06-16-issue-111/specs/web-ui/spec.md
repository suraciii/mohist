## ADDED Requirements

### Requirement: Settings System shows latest update outcome without stale states
The Settings > System view SHALL display the latest update outcome and SHALL NOT present stale `waiting-for-reconnect` states as current truth when the actual runtime has moved on.

#### Scenario: Stale state is not shown as current
- **WHEN** a user opens Settings > System
- **AND** the update status endpoint reports a `superseded` job
- **THEN** the UI SHALL display that the previous update job is no longer relevant
- **AND** SHALL show the current runtime identity (version, git hash)
- **AND** SHALL NOT present the superseded job as an active update

#### Scenario: Current update outcome is shown
- **WHEN** a user opens Settings > System after a completed update
- **THEN** the UI SHALL display the update outcome: succeeded, recovered with warnings, or failed
- **AND** when failed or recovered, SHALL show which capability is affected

#### Scenario: CLI update outcome appears in Web
- **WHEN** a CLI-triggered `mo update` completes and persists its outcome
- **THEN** Settings > System SHALL show that outcome when refreshed
- **AND** SHALL not show contradictory information from a prior Web-triggered job

### Requirement: Update progress uses same semantics for CLI and Web paths
The Web UI update progress display SHALL use the same product-level stages and outcome labels as the CLI update path.

#### Scenario: Web update progress shares stage names
- **WHEN** a Web-triggered update is running
- **THEN** the update progress SHALL display stages: Building, Restarting server, Waiting for reconnect, Restoring runner, Verifying runtime
- **AND** these SHALL match the product-level stages shown by `mo update`

#### Scenario: Web update outcome shares labels
- **WHEN** a Web-triggered update completes
- **THEN** the outcome label SHALL be one of: Succeeded, Recovered with warnings, Failed
- **AND** a failed outcome SHALL name the specific unavailable capability

## MODIFIED Requirements

### Requirement: Web UI starts and tracks System update jobs
The Web UI SHALL replace the unsupported rebuild mutation with a real System update mutation backed by `POST /api/system/update` and `GET /api/system/update/status`. The UI SHALL show progress stages for Building, Restarting server, Waiting for reconnect, Restoring runner, and Verifying runtime. The UI SHALL NOT present stale `waiting-for-reconnect` state as current truth after a newer runtime is detected.

#### Scenario: User starts an eligible update
- **WHEN** a user clicks `Update & Restart` for an eligible local-source deployment
- **THEN** the Web UI SHALL call `POST /api/system/update`
- **AND** it SHALL show update progress from the returned job and update status endpoint
- **AND** it SHALL not call any unsupported rebuild API or local-only placeholder mutation

#### Scenario: Progress stages are visible
- **WHEN** an update job is running
- **THEN** Settings > System SHALL show progress corresponding to Building, Restarting server, Waiting for reconnect, Restoring runner, and Verifying runtime as those stages are reached
- **AND** bounded stage logs or messages SHALL be available without exposing full shell output by default

#### Scenario: Server restart reconnect is handled
- **WHEN** the update restarts the server and the Web UI temporarily loses connection
- **THEN** the Web UI SHALL poll health until the server is reachable
- **AND** it SHALL refetch System info and update status after reconnect
- **AND** it SHALL confirm success when `running.gitHash` equals `source.head`

#### Scenario: Update status can be recovered after page reload
- **WHEN** the user reloads Settings > System during or after an update
- **THEN** the Web UI SHALL call `GET /api/system/update/status`
- **AND** it SHALL render the latest persisted job state rather than losing progress

#### Scenario: Stale waiting-for-reconnect is not shown as current
- **WHEN** the persisted update status returns a `superseded` state because the runtime has moved on
- **THEN** the Web UI SHALL display that the previous update is no longer relevant
- **AND** SHALL show the current runtime state instead
- **AND** SHALL NOT present the superseded job as an active or in-progress update
