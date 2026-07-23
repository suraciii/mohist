### Requirement: Server command surface is read-only over the connected application

The `server` command group SHALL expose only `status`, `health`, `info`, and `logs`. These commands SHALL read exclusively facts about the currently connected Mohist Server application over the Server API. The `server` group MUST NOT host any local managed-service lifecycle verb (`start`, `stop`, `restart`, `status`-as-local-unit, `logs`-as-service-manager, `uninstall`).

#### Scenario: Server help lists only read subcommands
- **WHEN** the user runs `mo server --help`
- **THEN** the listed subcommands include `status`, `health`, `info`, and `logs`, and MUST NOT include `start`, `stop`, `restart`, or `uninstall`

#### Scenario: Server health checks the connected application
- **WHEN** the user runs `mo server health`
- **THEN** the CLI issues `GET /api/health` against the connected Server and renders the response

### Requirement: `server status` reports overall Server status

`server status` SHALL report overall Server status by issuing the cross-project Server status read (`GET /api/status?all=true`). It MUST NOT inspect the local managed unit. The former `project status` command MUST be removed; overall Server status is reachable only via `mo server status`.

#### Scenario: Server status reads the overall status endpoint
- **WHEN** the user runs `mo server status`
- **THEN** the CLI issues `GET /api/status?all=true` and renders the Server's overall status

#### Scenario: Legacy project status no longer resolves
- **WHEN** the user runs `mo project status`
- **THEN** the command does not resolve, exits non-zero, and issues no HTTP request

### Requirement: `server logs` reads application logs

`server logs` SHALL return the connected Server's own application log tail by issuing `GET /api/logs/tail`. It MUST NOT read service-manager (systemd journal / scheduled-task) logs — those are reached via `service logs server`. The help, output, and errors for `server logs` MUST identify it as application logs and MUST NOT claim interchangeability with service-manager logs.

#### Scenario: Server logs reads the application log tail
- **WHEN** the user runs `mo server logs`
- **THEN** the CLI issues `GET /api/logs/tail` and renders the application log lines

#### Scenario: Server logs help distinguishes from service logs
- **WHEN** the user runs `mo server logs --help`
- **THEN** the help identifies the output as application logs and points readers to `mo service logs server` for service-manager logs

### Requirement: Application logs have a single entry point

The Server's application log tail SHALL be reachable only via `mo server logs`. The `system` command group, whose sole remaining member was the application-log `logs` read, MUST be removed; `mo system logs` MUST NOT resolve as a second path to the same source.

#### Scenario: Legacy system logs consolidated into server logs
- **WHEN** the user runs `mo system logs`
- **THEN** the command does not resolve, exits non-zero, and issues no HTTP request

#### Scenario: System group no longer present
- **WHEN** the user runs `mo system --help`
- **THEN** the command does not resolve and exits non-zero

### Requirement: Server reads require the connected application

When the Server application is unreachable, `server status`, `server health`, `server info`, and `server logs` SHALL exit non-zero and emit guidance indicating the Server is not running. The failure message MUST direct the user to start the local service via `mo service start server` rather than implying the verb itself starts a service.

#### Scenario: Server unreachable emits start-service guidance
- **WHEN** the connected Server is unreachable and the user runs `mo server status`
- **THEN** the CLI exits non-zero, prints a message that the Server is not running, and the guidance references `mo service start server`
