### Requirement: The `server` command group exposes an `info` subcommand for server-side system diagnostics

The `mo server` command group SHALL expose an `info` subcommand as a peer of the existing `health`, `install`, `update`, `start`, `stop`, `restart`, `status`, `logs`, and `uninstall` subcommands. `mo server info` reports server-side system diagnostics (identity / source / install / update / services / paths). The `system` command group SHALL NOT expose an `info` subcommand after this change — `info` is relocated from `system` to `server`.

#### Scenario: info appears in the server command group

- **WHEN** a caller runs `mo server --help`
- **THEN** the listed subcommands SHALL include `info`
- **AND** SHALL still include `health`, `install`, `update`, `start`, `stop`, `restart`, `status`, `logs`, and `uninstall`

#### Scenario: info is removed from the system command group

- **WHEN** a caller runs `mo system --help`
- **THEN** the listed subcommands SHALL NOT include `info`

### Requirement: `mo server info` reproduces the previous `mo system info` behavior exactly

`mo server info` SHALL be a pure path relocation of the legacy `mo system info`. It SHALL invoke the same handler (`PrintSystemInfoAsync`), issue `GET /api/system/info`, and render the same six sections (Identity, Source, Install, Update, Services, Paths). It SHALL support the `-o` / `--output` flag with the same default (`table`) and accepted values (`table`, `json`) that the legacy command accepted, including the degraded-mode rendering (CLI-local version line + `Server is not running` guidance) when the server is unreachable. The command's description SHALL distinguish it from `mo info` (which reports the CLI binary's own local environment and install source).

#### Scenario: the relocated command hits the same endpoint and renders the same sections

- **WHEN** a caller runs `mo server info` against a running server
- **THEN** the CLI SHALL issue `GET /api/system/info`
- **AND** SHALL render the Identity, Source, Install, Update, Services, and Paths sections to stdout exactly as the legacy `mo system info` did
- **AND** SHALL exit 0

#### Scenario: output-format flag carries over

- **WHEN** a caller runs `mo server info -o json`
- **THEN** the CLI SHALL render the diagnostics envelope as JSON to stdout, matching the legacy `mo system info -o json` output
- **AND** the default format (`-o table`) SHALL match the legacy default

#### Scenario: server-unreachable degraded mode carries over

- **WHEN** a caller runs `mo server info` and the server cannot be reached
- **THEN** the CLI SHALL render the same degraded-mode output the legacy `mo system info` rendered (CLI-local version line plus `Server is not running. Start with: mo server start` guidance)
- **AND** SHALL exit 0

#### Scenario: the command description disambiguates from mo info

- **WHEN** a caller runs `mo server info --help`
- **THEN** the description SHALL identify the command as server-side system diagnostics
- **AND** SHALL distinguish it from `mo info` (CLI-local environment and install source)
