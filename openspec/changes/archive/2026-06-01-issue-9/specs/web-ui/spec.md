## ADDED Requirements

### Requirement: Web UI shows runner status summary
The Web UI SHALL provide a compact runner status summary in a stable location such as the top status bar or Activity surface. The summary SHALL let users distinguish no runner, connected idle runner, and connected busy runner states without opening logs.

#### Scenario: No runner summary
- **WHEN** the runner status API returns no runners for the selected project
- **THEN** the Web UI shows a no-runner status
- **AND** the summary provides a clear command hint for starting or installing a runner

#### Scenario: Idle runner summary
- **WHEN** at least one runner is connected and no runner has active work
- **THEN** the Web UI shows that runner capacity is connected and idle
- **AND** it does not present the project as lacking runner capacity

#### Scenario: Busy runner summary
- **WHEN** at least one connected runner has active work
- **THEN** the Web UI shows that runner capacity is connected and busy
- **AND** it exposes enough summary text for users to understand that work is currently assigned

#### Scenario: Busy runner summary preserves connection diagnostics
- **WHEN** at least one runner has a fresh heartbeat and active work
- **AND** its workspace-query SignalR connection is disconnected
- **THEN** the Web UI still counts the runner as busy capacity
- **AND** the detailed runner row shows the disconnected connection state as a diagnostic

### Requirement: Web UI provides detailed runner list
The Web UI SHALL provide a detailed runner status list in a discoverable surface such as Activity or Settings. The list SHALL render runner id, kind, hostname, scope, status, last heartbeat, connection state when known, capabilities, coder model names or count, capacity or slot data when available, and active work when present.

#### Scenario: Detailed list renders idle runner
- **WHEN** the runner status API returns a connected idle runner
- **THEN** the detailed runner list shows the runner identity, host, scope, heartbeat freshness, capabilities, coder models, and idle status

#### Scenario: Detailed list renders active runner
- **WHEN** the runner status API returns a connected runner with active work
- **THEN** the detailed runner list shows the runner as busy
- **AND** it displays the active work reference provided by the API

#### Scenario: Detailed list renders stale or offline runner
- **WHEN** the runner status API returns a stale or offline runner row
- **THEN** the detailed runner list shows the runner as stale or offline
- **AND** it displays safe diagnostics such as hostname, last heartbeat, and connection state when known

#### Scenario: Detailed list renders empty state
- **WHEN** the runner status API returns no runners
- **THEN** the detailed runner list shows an empty state
- **AND** the empty state includes a clear command hint for starting or installing a runner

### Requirement: Board no-runner banner links to runner status
The existing board no-runner warning SHALL be preserved. It SHALL point users to the runner status surface for details and startup guidance.

#### Scenario: Board warning remains visible
- **WHEN** the current project has no connected runner capacity
- **THEN** the board still shows the no-runner warning
- **AND** the warning includes a link or pointer to the runner status surface

#### Scenario: Board warning does not appear for connected idle runner
- **WHEN** the current project has at least one connected idle runner that can serve it
- **THEN** the board does not show the no-runner warning

#### Scenario: Board warning does not appear for connected busy runner
- **WHEN** the current project has at least one connected busy runner that can serve it
- **THEN** the board does not show the no-runner warning solely because the runner is busy

#### Scenario: Board warning remains for stale or offline runners only
- **WHEN** the current project has only stale or offline runner rows
- **THEN** the board still shows the no-runner warning because stale or offline runners do not count as connected capacity

### Requirement: Runner status UI regression coverage
Runner status UI behavior SHALL have Web regression coverage for empty, idle, active, and stale or offline runner states.

#### Scenario: Empty runner state is tested
- **WHEN** Web UI tests render runner status with no runner rows
- **THEN** tests verify the no-runner message and startup command hint are visible

#### Scenario: Idle runner state is tested
- **WHEN** Web UI tests render runner status with a connected idle runner
- **THEN** tests verify the connected idle state and runner details are visible

#### Scenario: Active runner state is tested
- **WHEN** Web UI tests render runner status with a connected busy runner
- **THEN** tests verify the busy state and active work summary are visible

#### Scenario: Stale or offline runner state is tested
- **WHEN** Web UI tests render runner status with only stale or offline runner rows
- **THEN** tests verify stale or offline diagnostics are visible
- **AND** tests verify the board still treats the project as having no connected runner capacity
