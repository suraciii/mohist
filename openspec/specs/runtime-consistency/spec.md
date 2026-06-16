# OpenSpec Capability: runtime-consistency

### Requirement: Post-update runtime verification

After a successful update, `mo update` SHALL verify that the installed CLI, running Server, served Web assets, connected Runner, and managed skill assets form a coherent usable runtime.

#### Scenario: All components pass verification

- **WHEN** `mo update` completes all update stages
- **THEN** the CLI SHALL verify that the installed `mo` binary is callable and reports the expected version
- **AND** SHALL verify the Server `/api/health` endpoint responds
- **AND** SHALL verify the Server identity (git hash or version) matches the source HEAD
- **AND** SHALL verify the Web root `/` serves HTML referencing the expected asset paths
- **AND** SHALL verify the Runner service is active and connected
- **AND** SHALL verify managed skill assets exist at the expected `~/.mohist/cli/skill-data` path
- **AND** SHALL report "Update complete. Mohist is ready."

#### Scenario: Server identity mismatch

- **WHEN** server readiness passes but the running server identity does not match the expected source HEAD
- **THEN** the CLI SHALL report that the Server is running an unexpected version
- **AND** the outcome SHALL be "recovered with warnings"

#### Scenario: Runner not connected after update

- **WHEN** the server is ready but the runner is not active or not connected
- **THEN** the CLI SHALL report that the Runner is unavailable
- **AND** the outcome SHALL be "failed" with "Runner unavailable" as the specific capability

#### Scenario: Managed skill assets missing after update

- **WHEN** managed skill asset data is missing from `~/.mohist/cli/skill-data` after update
- **THEN** the CLI SHALL report that managed skill assets are not installed
- **AND** the outcome SHALL be "recovered with warnings"

### Requirement: Update stages display product-level progress

`mo update` SHALL display user-facing product-level stages rather than raw implementation logs during the update process.

#### Scenario: Stages are displayed in order

- **WHEN** `mo update` executes
- **THEN** the output SHALL show stages in this order:
  - "Updating CLI"
  - "Preparing workflow runner" (stopping runner for server update)
  - "Updating Mohist Server" (building and restarting)
  - "Waiting for Mohist to become usable" (readiness checks)
  - "Restoring workflow runner" (restarting runner)
  - "Verifying workflow runtime"

#### Scenario: Long waits show progress

- **WHEN** any stage takes longer than a bounded interval (e.g., waiting for server readiness)
- **THEN** the CLI SHALL display the current wait reason
- **AND** SHALL update the reason when the stage transitions (e.g., from "waiting for Mohist API" to "waiting for Web assets")

#### Scenario: Runner-stopped window is visible

- **WHEN** the runner has been stopped for the server update
- **THEN** the CLI SHALL display that the runner is stopped and workflows cannot run
- **AND** this state SHALL remain visible until the runner is restored

### Requirement: System update status reconciles stale states

The system update status endpoint SHALL detect when a persisted `waiting-for-reconnect` job belongs to a runtime that has already advanced, and SHALL mark that job as superseded rather than presenting it as current truth.

#### Scenario: Stale waiting-for-reconnect is superseded

- **WHEN** `GET /api/system/update/status` returns a job with status `waiting-for-reconnect`
- **AND** the running server git hash differs from the job's recorded `sourceHead`
- **AND** the running server git hash is not empty
- **THEN** the endpoint SHALL change the job status to `superseded`
- **AND** the response SHALL indicate the job is no longer relevant

#### Scenario: Stale state is replaced on CLI-triggered update

- **WHEN** a CLI-triggered update completes (via `mo update`)
- **AND** a persisted Web-triggered update job has status `waiting-for-reconnect`
- **AND** the running server identity has moved past that job's source HEAD
- **THEN** reading the update status SHALL return the job as `superseded`

#### Scenario: CLI update outcome is persisted

- **WHEN** `mo update` completes (success or failure)
- **THEN** the update outcome SHALL be persisted via the system update status mechanism
- **AND** the Web UI SHALL be able to read the latest outcome from `GET /api/system/update/status`

### Requirement: CLI and Web update paths share product semantics

CLI-triggered and Web-triggered update paths SHALL use the same product-level stages, outcome labels, and persistence mechanism wherever practical.

#### Scenario: Both paths use same stages

- **WHEN** an update is triggered from CLI or Web
- **THEN** the observed progress SHALL follow the same product-level stages: Building, Restarting server, Waiting for reconnect, Verifying runtime
- **AND** the final outcome SHALL use the same labels: succeeded, recovered, or failed

#### Scenario: CLI update outcome is visible from Web

- **WHEN** a CLI-triggered `mo update` completes
- **THEN** the Web UI system status view SHALL show the latest update outcome
- **AND** the outcome SHALL not be overridden by stale Web-triggered job state
