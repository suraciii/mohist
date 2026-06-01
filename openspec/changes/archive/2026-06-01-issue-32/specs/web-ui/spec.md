## ADDED Requirements

### Requirement: Settings System shows server runtime and source state
The Web UI SHALL add a Server Runtime section under Settings > System that displays the running Mohist server identity, trusted source repository state, install mode, update status, service state, and existing system paths using `/api/system/info` as the source of truth.

#### Scenario: Runtime fields are displayed
- **WHEN** a user opens Settings > System
- **THEN** the Server Runtime section SHALL show running version, running git hash, source path, source branch, source HEAD, source dirty state, install mode, update status, server service status, and runner service status
- **AND** git hashes SHALL be shown in short form with the full hash available through tooltip or copyable text

#### Scenario: Existing paths remain visible
- **WHEN** Settings > System renders system path information
- **THEN** it SHALL continue to show db, config, logs, and opencode paths from the System info API

### Requirement: Settings System presents local-source update eligibility clearly
The Web UI SHALL show `Update & Restart` only when the System info API reports a local-source deployment whose source HEAD differs from the running git hash. Non-local-source deployments SHALL show an unsupported deployment note instead of an update button. Dirty source state SHALL be clearly visible and SHALL block the first-iteration update action.

#### Scenario: Update button appears for eligible local-source update
- **WHEN** System info reports `install.mode = local-source`
- **AND** `source.head` differs from `running.gitHash`
- **AND** update status is `update-available`
- **THEN** Settings > System SHALL show an `Update & Restart` button
- **AND** it SHALL NOT use `Rebuild & Restart` wording

#### Scenario: Unsupported deployment hides update button
- **WHEN** System info reports `install.mode = binary` or `install.mode = unknown`
- **THEN** Settings > System SHALL NOT show the update button
- **AND** it SHALL show a note explaining that Web update is unsupported for the detected deployment

#### Scenario: Dirty source blocks update button
- **WHEN** System info reports `source.dirty = true`
- **THEN** Settings > System SHALL show a clear dirty-source warning
- **AND** the update action SHALL be disabled for the first iteration

### Requirement: Web UI starts and tracks System update jobs
The Web UI SHALL replace the unsupported rebuild mutation with a real System update mutation backed by `POST /api/system/update` and `GET /api/system/update/status`. The UI SHALL show progress stages for Building, Restarting server, Waiting for reconnect, and Ready.

#### Scenario: User starts an eligible update
- **WHEN** a user clicks `Update & Restart` for an eligible local-source deployment
- **THEN** the Web UI SHALL call `POST /api/system/update`
- **AND** it SHALL show update progress from the returned job and update status endpoint
- **AND** it SHALL not call any unsupported rebuild API or local-only placeholder mutation

#### Scenario: Progress stages are visible
- **WHEN** an update job is running
- **THEN** Settings > System SHALL show progress corresponding to Building, Restarting server, Waiting for reconnect, and Ready as those stages are reached
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

### Requirement: System API client and hooks support runtime update operations
The Web UI API client and query hooks SHALL provide typed support for `/api/system/info`, `/api/system/update`, and `/api/system/update/status`.

#### Scenario: System info hook loads runtime state
- **WHEN** Settings > System renders
- **THEN** it SHALL load typed System info through the Web API client
- **AND** render loading and error states without showing misleading `unknown` runtime values as confirmed facts

#### Scenario: System update mutation surfaces failures
- **WHEN** `POST /api/system/update` returns an unsupported, dirty-source, conflict, or other error response
- **THEN** the Web UI SHALL show the server-provided message near the update control
- **AND** it SHALL not present the update as running or completed
