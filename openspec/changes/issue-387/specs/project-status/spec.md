### Requirement: The `project` command group exposes a `status` subcommand as a peer of the other project verbs

The `mo project` command group SHALL expose a `status` subcommand alongside the existing `list`, `create`, `show`, `use`, `delete`, and `workflow` subcommands. `status` is the project-aggregate status verb: it reports server status across all projects. It SHALL NOT accept a project argument or `--project` / `--project-id` flags — the underlying endpoint aggregates across all projects by design (`all=true`).

#### Scenario: status appears in the project command group help

- **WHEN** a caller runs `mo project --help`
- **THEN** the listed subcommands SHALL include `status`
- **AND** the listed subcommands SHALL still include `list`, `create`, `show`, `use`, `delete`, and `workflow`

#### Scenario: status takes no project argument

- **WHEN** a caller runs `mo project status --help`
- **THEN** the command SHALL NOT advertise a positional `project` argument
- **AND** SHALL NOT advertise `--project` or `--project-id` flags

### Requirement: `mo project status` reproduces the previous `mo status` behavior exactly

`mo project status` SHALL be a pure path relocation of the legacy root-level `mo status`. It SHALL issue `GET /api/status?all=true` and render the response identically to the legacy command — same default output (no `-o` flag, raw response rendering), same exit codes, and same error handling. No behavior, endpoint, flag, or rendering SHALL change as part of the relocation.

#### Scenario: the relocated command hits the same endpoint

- **WHEN** a caller runs `mo project status`
- **THEN** the CLI SHALL issue `GET /api/status?all=true`
- **AND** SHALL render the response body to stdout exactly as the legacy `mo status` did
- **AND** SHALL exit 0 on a successful response

#### Scenario: server-unreachable handling carries over

- **WHEN** a caller runs `mo project status` and the server cannot be reached
- **THEN** the CLI SHALL emit the same server-unavailable guidance the legacy `mo status` emitted
- **AND** SHALL exit non-zero
