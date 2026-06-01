## ADDED Requirements

### Requirement: Server detects trusted local-source systemd installation
Server daemon behavior SHALL include detection of a trusted local-source Mohist installation from user systemd unit files. The server unit SHALL be considered local-source only when `mohist.service` exists in the user systemd unit directory, its `WorkingDirectory` points to a repository containing `Mohist.sln`, and its `ExecStart` uses a source-run shape such as `dotnet run --project ...Mohist.Server.csproj`.

#### Scenario: Local-source systemd install is detected
- **WHEN** the user systemd `mohist.service` unit exists
- **AND** the unit `WorkingDirectory` points to a repository containing `Mohist.sln`
- **AND** the unit `ExecStart` runs Mohist with `dotnet run --project` against `Mohist.Server.csproj`
- **THEN** the server SHALL classify the install as `local-source`
- **AND** it SHALL trust that `WorkingDirectory` as the source path for runtime info and update checks

#### Scenario: Non-source systemd install is not treated as local-source
- **WHEN** the server unit is absent, has no valid Mohist source `WorkingDirectory`, or does not use a source-run `ExecStart`
- **THEN** the server SHALL classify the install as `binary` or `unknown`
- **AND** Web-initiated update SHALL be unavailable

### Requirement: Server-side update restarts trusted Mohist services only
When a local-source System update is started, the server SHALL restart only the trusted Mohist user systemd units detected from installation facts. It MUST NOT accept service names, commands, paths, or environment values from Web clients.

#### Scenario: Server service restart uses fixed unit
- **WHEN** an eligible local-source update reaches the restart stage
- **THEN** the server SHALL run a user systemd restart for the trusted `mohist.service` unit
- **AND** it SHALL NOT restart arbitrary units requested by the client

#### Scenario: Runner restart keeps services aligned
- **WHEN** the trusted runner unit is present during update
- **THEN** the update flow SHALL restart `mohist-runner.service` so the runner and server versions stay aligned
- **AND** runner restart status SHALL be reflected in update job state or service state

### Requirement: Server daemon readiness supports post-update reconnect
After a Web-initiated update restarts the server, daemon readiness SHALL be observable through health and Web asset requests so the Web UI can reconnect safely.

#### Scenario: Restarted server becomes ready for Web reconnect
- **WHEN** the server has been restarted by a System update
- **THEN** `GET /api/health` SHALL return healthy status when the API is ready
- **AND** the Web root `/` and referenced `/assets/*` SHALL be served successfully before the update job reports Ready
