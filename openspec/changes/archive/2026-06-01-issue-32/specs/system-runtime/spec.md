## ADDED Requirements

### Requirement: Runtime identity is stable for the running process
Mohist SHALL expose a stable runtime identity for the current server process. The running version and running git hash MUST be captured once during server startup, preferring assembly or generated build metadata and falling back to the local git HEAD only for source-run development mode. The running git hash MUST remain fixed for the lifetime of the process even if the source repository advances.

#### Scenario: Running identity is captured at startup
- **WHEN** the server starts with build metadata containing version and git hash
- **THEN** System runtime info SHALL report that version and git hash as `running.version` and `running.gitHash`
- **AND** subsequent source repository changes SHALL NOT change `running.gitHash` until the server process restarts

#### Scenario: Source-run fallback captures git HEAD
- **WHEN** the server starts without build git metadata from a local source-run checkout
- **THEN** System runtime info SHALL fall back to the checkout HEAD as `running.gitHash`
- **AND** the fallback value SHALL remain fixed until process restart

### Requirement: Source repository state is inspected dynamically
Mohist SHALL inspect the trusted local source repository on each System runtime info request when a local-source install is detected. The source state SHALL include path, branch, HEAD hash, and dirty state without accepting source path input from the request.

#### Scenario: Source HEAD can differ from running hash
- **WHEN** the trusted source repository advances after the server process started
- **THEN** System runtime info SHALL report the new value as `source.head`
- **AND** `running.gitHash` SHALL still report the hash captured at server startup
- **AND** update availability SHALL be computed from the difference between `source.head` and `running.gitHash`

#### Scenario: Dirty source is surfaced
- **WHEN** the trusted source repository has uncommitted changes
- **THEN** System runtime info SHALL report `source.dirty = true`
- **AND** update status SHALL be `dirty-source`
- **AND** the update reason SHALL explain that the source tree is dirty

### Requirement: Install mode and update eligibility are derived from trusted installation facts
Mohist SHALL classify the current deployment as `local-source`, `binary`, or `unknown`. A deployment SHALL be `local-source` only when the user systemd server unit exists, its `WorkingDirectory` points to a repository containing `Mohist.sln`, and its `ExecStart` uses a source-run shape such as `dotnet run --project ...Mohist.Server.csproj`. Update eligibility MUST be derived from these trusted facts and configuration gates, not from request input.

#### Scenario: Local-source install is eligible when source is newer
- **WHEN** the trusted systemd unit points to a Mohist source repository
- **AND** the source repository is clean
- **AND** `source.head` differs from `running.gitHash`
- **AND** System update is enabled by configuration or local-source development defaults
- **THEN** System runtime info SHALL report `install.mode = local-source`
- **AND** update status SHALL be `update-available`
- **AND** `update.available` SHALL be `true`

#### Scenario: Non-local-source install is unsupported
- **WHEN** the systemd unit is absent or does not match the local-source install checks
- **THEN** System runtime info SHALL report `install.mode` as `binary` or `unknown`
- **AND** update status SHALL be `unsupported`
- **AND** `update.available` SHALL be `false`
- **AND** the update reason SHALL explain that Web update is unsupported for the detected deployment

#### Scenario: Missing runtime or source identity reports unknown update state
- **WHEN** the deployment facts are otherwise local-source but `running.gitHash` or `source.head` cannot be determined
- **THEN** System runtime info SHALL report `update.status = unknown`
- **AND** `update.available` SHALL be `false`
- **AND** the update reason SHALL explain which identity value is unavailable

### Requirement: Service state is included in runtime info
Mohist SHALL report server and runner service state in System runtime info when service management is detected. The response SHALL include service manager, server unit, runner unit, and per-service status without exposing sensitive environment data.

#### Scenario: Systemd services are reported
- **WHEN** the server is installed with user systemd units for Mohist server and runner
- **THEN** System runtime info SHALL include `install.serviceManager = systemd-user`
- **AND** it SHALL include the detected `install.serverUnit` and `install.runnerUnit`
- **AND** it SHALL include `services.server` and `services.runner` status values suitable for display

### Requirement: Update job state is durable across server restart
Mohist SHALL persist local-source update job state outside process memory so the restarted server can report the latest update status. Persisted state SHALL include job identity, current status, current stage, bounded stage logs, timestamps, and final runtime/source confirmation when available.

#### Scenario: Update status survives restart
- **WHEN** an update job restarts the Mohist server
- **THEN** the new server process SHALL be able to report the latest job state from persisted storage
- **AND** the reported status SHALL include whether the server reached the post-restart readiness and confirmation stage

#### Scenario: Captured output is bounded
- **WHEN** build or restart commands produce stdout or stderr
- **THEN** persisted update stage logs SHALL be bounded in length
- **AND** sensitive environment variables SHALL NOT be exposed in the default status payload
