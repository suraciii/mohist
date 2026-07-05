### Requirement: The `system` command group exposes a `logs` subcommand for application logs

The `mo system` command group SHALL expose a `logs` subcommand that reports application logs (the Mohist server's own log tail). It SHALL be distinct in scope from `mo server logs`, which reports operational/service-manager logs (systemd journal or scheduled-task output). The `system` group description SHALL record this distinction so a reader of `mo system --help` can tell the two log surfaces apart.

#### Scenario: logs appears in the system command group

- **WHEN** a caller runs `mo system --help`
- **THEN** the listed subcommands SHALL include `logs`

#### Scenario: the system group documents the application-vs-operational distinction

- **WHEN** a caller runs `mo system --help`
- **THEN** the group description SHALL identify its logs as application logs
- **AND** SHALL distinguish them from the operational logs surfaced by `mo server logs`

### Requirement: `mo system logs` reproduces the previous `mo logs` behavior exactly

`mo system logs` SHALL be a pure path relocation of the legacy root-level `mo logs`. It SHALL issue `GET /api/logs/tail` and render the response identically to the legacy command — same default output, same exit codes, same error handling. No behavior, endpoint, flag, or rendering SHALL change as part of the relocation.

#### Scenario: the relocated command hits the same endpoint

- **WHEN** a caller runs `mo system logs`
- **THEN** the CLI SHALL issue `GET /api/logs/tail`
- **AND** SHALL render the response body to stdout exactly as the legacy `mo logs` did
- **AND** SHALL exit 0 on a successful response

#### Scenario: server-unreachable handling carries over

- **WHEN** a caller runs `mo system logs` and the server cannot be reached
- **THEN** the CLI SHALL emit the same server-unavailable guidance the legacy `mo logs` emitted
- **AND** SHALL exit non-zero
