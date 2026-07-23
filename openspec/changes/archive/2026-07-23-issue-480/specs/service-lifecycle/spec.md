### Requirement: Unified local managed-process lifecycle command

`mo service <start|stop|restart|status|logs|uninstall> <target>` SHALL be the only entry point for local managed-process lifecycle. Each action SHALL act solely on the OS-level managed service (the systemd unit on Linux, the scheduled task on Windows) for the selected target. No local managed-service lifecycle verb SHALL remain reachable under `runner` or `server`.

#### Scenario: Service start acts on the local managed service
- **WHEN** the user runs `mo service start server`
- **THEN** the CLI drives the local managed service for the server target and issues no request to the Server API

#### Scenario: Local lifecycle not reachable under runner
- **WHEN** the user runs `mo runner start`
- **THEN** the command does not resolve, exits non-zero, and invokes no local service manager action

#### Scenario: Local lifecycle not reachable under server
- **WHEN** the user runs `mo server restart`
- **THEN** the command does not resolve, exits non-zero, and invokes no local service manager action

### Requirement: Target restricted to server or runner

Every `service` verb SHALL take a positional `target` argument whose value MUST be exactly `server` or `runner`. Any other value SHALL be rejected as a usage error that exits non-zero without performing any action.

#### Scenario: Valid target accepted
- **WHEN** the user runs `mo service status runner`
- **THEN** the CLI reports the lifecycle status of the local runner managed service

#### Scenario: Invalid target rejected as usage error
- **WHEN** the user runs `mo service status database`
- **THEN** the CLI exits non-zero with a usage error, performs no service-manager action, and indicates that target must be `server` or `runner`

### Requirement: `service logs` reads service-manager logs

`mo service logs <target>` SHALL read the service-manager logs (the systemd journal or scheduled-task output) for the selected target. It MUST NOT read application logs — those are reached via `mo server logs`. The help, output, and errors for `service logs` MUST NOT claim interchangeability with application logs, and no `--source` flag SHALL merge the two log sources.

#### Scenario: Service logs reads the service-manager journal
- **WHEN** the user runs `mo service logs server --lines 200 --follow`
- **THEN** the CLI tails the service-manager logs for the server target with the requested line count and follow behavior

#### Scenario: Service logs help distinguishes from server logs
- **WHEN** the user runs `mo service logs server --help`
- **THEN** the help identifies the output as service-manager logs and points readers to `mo server logs` for application logs

### Requirement: Service commands do not parse Project

`service` commands SHALL accept no `--project` or `--project-id` option and SHALL NOT resolve a Project. Local managed-process lifecycle is an operating-system concept, not a Project-scoped resource.

#### Scenario: Service help advertises no project option
- **WHEN** the user runs `mo service start server --help`
- **THEN** the advertised options MUST NOT include `--project` or `--project-id`

### Requirement: Service commands do not mutate remote domain state

`service` actions SHALL NOT call the Server API and SHALL NOT alter remote Runner or Server domain state. They SHALL only drive the local managed process and report local service-manager facts.

#### Scenario: Service action issues no Server API request
- **WHEN** the user runs `mo service stop runner`
- **THEN** the CLI stops the local runner managed service and issues no HTTP request to the Server

### Requirement: Lifecycle options preserved

`service` lifecycle verbs SHALL support `--dry-run` (preview without executing) and `--unit-dir` (Linux service unit directory override). `service logs` SHALL additionally support `--lines`/`-n` and `--follow`/`-f`. `--dry-run` SHALL produce a faithful, complete preview of the service-manager command rather than executing it.

#### Scenario: Dry run previews without executing
- **WHEN** the user runs `mo service start runner --dry-run`
- **THEN** the CLI prints the service-manager command it would run and performs no state-changing action

### Requirement: Install and update remain root-level only

`install` and `update` SHALL remain root-level commands. No install or update verb SHALL be duplicated under `runner`, `server`, or `service`.

#### Scenario: No install entry under service or runner
- **WHEN** the user runs `mo service install server` or `mo runner install`
- **THEN** the command does not resolve and exits non-zero, because install lives only at `mo install <server|runner>`
