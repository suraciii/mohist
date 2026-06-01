## ADDED Requirements

### Requirement: Project runner status read model
Mohist SHALL provide a stable runner status read model for the selected project. The read model MUST include project-scoped runners for the selected project and global runners that can serve the selected project.

Each runner row SHALL include runner id, kind, hostname when known, scope, status, registration or connection time when known, last heartbeat time, SignalR connection state when known, capabilities, coder model names and count, capacity or slot data when available, and active work assignment when present.

#### Scenario: Project-scoped runner is listed
- **WHEN** a runner is registered for the selected project
- **THEN** the runner status read model includes that runner
- **AND** the runner row identifies the selected project as its scope

#### Scenario: Global runner is listed for selected project
- **WHEN** a runner is registered without a project-specific scope
- **AND** the selected project can be served by global runners
- **THEN** the runner status read model includes that runner
- **AND** the runner row identifies its scope as `global`

#### Scenario: Other project runner is excluded
- **WHEN** a runner is registered for a different project only
- **THEN** the selected project's runner status read model does not include that runner

### Requirement: Runner status projection enriches registry data
Runner status projection SHALL be produced server-side by combining registered runner information with runtime runner state. The Web UI MUST NOT query individual runner grains directly to assemble runner rows.

The projection SHALL use registration data such as runner id, capabilities, hostname, project scope, and coder models, and enrich it with runtime state such as heartbeat freshness, connection state, assigned or in-flight work, and slot usage where available.

#### Scenario: Registered runner includes runtime state
- **WHEN** a registered runner has a recent heartbeat and no active work
- **THEN** the runner status projection marks the runner as connected idle
- **AND** the row includes the latest heartbeat time and no active work assignment

#### Scenario: Busy runner includes active work
- **WHEN** a registered runner has leased or assigned workflow work
- **THEN** the runner status projection marks the runner as connected busy
- **AND** the row includes the active workflow or work item identity available to the server

#### Scenario: Busy runner retains connection diagnostic
- **WHEN** a registered runner has a fresh heartbeat and leased or assigned workflow work
- **AND** its workspace-query SignalR connection is disconnected
- **THEN** the runner status projection marks the runner as busy
- **AND** the row includes the disconnected connection state as a diagnostic

#### Scenario: Stale runner is distinguishable
- **WHEN** a registered runner has no fresh heartbeat or live connection state
- **THEN** the runner status projection marks the runner as offline or stale according to the server liveness policy
- **AND** the row retains safe diagnostic fields such as last heartbeat and hostname when known

### Requirement: Runner status avoids sensitive data
Runner status rows SHALL expose operational runner metadata only. They MUST NOT expose runner environment variables, local secrets, authentication tokens, or agent session transcript details.

#### Scenario: Runner capabilities are shown without secrets
- **WHEN** a runner registers capabilities and coder models
- **THEN** the runner status row includes capability names and coder model names
- **AND** the row does not include environment variables, API keys, tokens, or other local secrets

#### Scenario: Active work is summarized without transcript details
- **WHEN** a runner is processing active work
- **THEN** the runner status row includes a concise active work reference
- **AND** the row does not include agent session transcript content
