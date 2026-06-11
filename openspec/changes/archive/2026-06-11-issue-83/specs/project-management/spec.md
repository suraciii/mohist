## ADDED Requirements

### Requirement: `mo project` exposes a `repo` subcommand group

The CLI SHALL expose a `mo project repo` subcommand group with `list`, `add`, `set-default`, and `remove` subcommands. The subcommand group SHALL wrap the existing server repository API at `/api/projects/{ref}/repositories` and SHALL NOT introduce new server semantics — it only adds a CLI surface to the existing server endpoints.

#### Scenario: Subcommand group is available
- **WHEN** the user runs `mo project --help`
- **THEN** the help output SHALL list `repo` alongside the existing subcommands (list, create, show, use, delete)

#### Scenario: Subcommand group is a thin client
- **WHEN** the user runs any `mo project repo` subcommand
- **THEN** the CLI SHALL send the corresponding HTTP request to the existing server endpoint
- **AND** SHALL NOT introduce a new server route, schema, or domain method
- **AND** SHALL NOT maintain local repository state

#### Scenario: Each subcommand maps to an existing server endpoint
- **WHEN** the user runs `mo project repo list --project mohist-local`
- **THEN** the CLI sends `GET /api/projects/mohist-local/repositories`
- **WHEN** the user runs `mo project repo add --project mohist-local --name api`
- **THEN** the CLI sends `POST /api/projects/mohist-local/repositories`
- **WHEN** the user runs `mo project repo set-default --project mohist-local api`
- **THEN** the CLI sends `PATCH /api/projects/mohist-local/repositories/api` with `{ setDefault: true }`
- **WHEN** the user runs `mo project repo remove --project mohist-local api`
- **THEN** the CLI sends `DELETE /api/projects/mohist-local/repositories/api`

### Requirement: `mo project repo` subcommands accept `--project` with active project fallback

Each `mo project repo` subcommand SHALL accept `--project <name-or-id>` for explicit project selection. When `--project` is not passed, the CLI SHALL fall back to the active project stored by `mo project use`. `--project-id` SHALL remain a backwards-compatible alias. The full conflict/validation rules and the standardized "no active project" diagnostic are defined in the `cli-project-ref` capability.

#### Scenario: Explicit project is used
- **WHEN** the user runs `mo project repo list --project mohist-local`
- **THEN** the CLI resolves `mohist-local` to the project id
- **AND** sends the request to the resolved project route

#### Scenario: Active project is used as fallback
- **WHEN** the user has previously run `mo project use mohist-local`
- **AND** runs `mo project repo list` with no project option
- **THEN** the CLI SHALL use the active project
- **AND** SHALL send the request to the resolved active project route

#### Scenario: No active project and no option yields a guided error
- **WHEN** the user runs `mo project repo list` with no project option
- **AND** no active project is set
- **THEN** the CLI prints a clear error mentioning `mo project use` and `--project`
- **AND** exits with a non-zero status
- **AND** does not make a server request
