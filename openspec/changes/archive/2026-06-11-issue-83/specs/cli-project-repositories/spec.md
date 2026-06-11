## ADDED Requirements

### Requirement: `mo project repo` exposes a subcommand group for project repositories

The CLI SHALL expose a `mo project repo` subcommand group with four subcommands: `list`, `add`, `set-default`, and `remove`. The subcommand group SHALL wrap the existing server endpoints at `/api/projects/{ref}/repositories` and SHALL NOT introduce new server semantics.

#### Scenario: Project repo help lists the four subcommands
- **WHEN** the user runs `mo project repo --help`
- **THEN** the output SHALL list `list`, `add`, `set-default`, and `remove`
- **AND** SHALL document that `--project <name-or-id>` (or active project) is required

#### Scenario: Project repo list returns the repository list
- **WHEN** the user runs `mo project repo list --project mohist-local`
- **THEN** the CLI sends `GET /api/projects/mohist-local/repositories` to the server
- **AND** displays the repositories (or JSON, depending on `--output`)

#### Scenario: Project repo add creates a repository
- **WHEN** the user runs `mo project repo add --project mohist-local --name api --path /path/to/api --set-default`
- **THEN** the CLI sends `POST /api/projects/mohist-local/repositories` with the repository payload
- **AND** prints success output

#### Scenario: Project repo set-default sets the default
- **WHEN** the user runs `mo project repo set-default --project mohist-local api`
- **THEN** the CLI sends `PATCH /api/projects/mohist-local/repositories/api` with `{ setDefault: true }`
- **AND** prints success output

#### Scenario: Project repo remove deletes a repository
- **WHEN** the user runs `mo project repo remove --project mohist-local api`
- **THEN** the CLI sends `DELETE /api/projects/mohist-local/repositories/api` to the server
- **AND** prints success output

### Requirement: Repository subcommands accept `--project` and active project fallback

Each `mo project repo` subcommand SHALL accept `--project <name-or-id>` for explicit project selection. When `--project` is not passed, the CLI SHALL fall back to the active project stored by `mo project use`. The same-value/different-value rule with `--project-id` SHALL apply as defined by the `cli-project-ref` capability.

#### Scenario: Explicit project is used
- **WHEN** the user runs `mo project repo list --project mohist-local`
- **THEN** the CLI resolves `mohist-local` to the project id
- **AND** sends the request to the resolved project route

#### Scenario: Active project is used as fallback
- **WHEN** the user has previously run `mo project use mohist-local`
- **AND** runs `mo project repo list` with no `--project`
- **THEN** the CLI SHALL use the active project
- **AND** SHALL send the request to the resolved active project route

#### Scenario: No active project and no option yields the standard diagnostic
- **WHEN** the user runs `mo project repo list` with no project option
- **AND** no active project is set
- **THEN** the CLI prints `Run 'mo project use <name-or-id>' or pass --project <name-or-id>` (or clearly equivalent wording)
- **AND** exits with a non-zero status
- **AND** does not make a server request

#### Scenario: `--project-id` alias is accepted
- **WHEN** the user runs `mo project repo list --project-id proj_f6c141d63b6243bfbb481737b2243b87`
- **THEN** the CLI SHALL treat `--project-id` as a backwards-compatible alias for `--project`
- **AND** SHALL send the request to the resolved project route

### Requirement: Repository subcommands surface server conflicts and not-found errors

When a repository subcommand receives a server error (e.g. duplicate name, repository not found, project not found), the CLI SHALL surface the server error message verbatim (or with a clearly equivalent readable wrapper) and SHALL exit with a non-zero status. The CLI SHALL NOT retry on conflict and SHALL NOT swallow not-found errors.

#### Scenario: Duplicate repository name surfaces the server error
- **WHEN** the user runs `mo project repo add --project mohist-local --name api`
- **AND** a repository named `api` already exists
- **THEN** the server returns a conflict error
- **AND** the CLI prints a clear conflict message mentioning the duplicate name
- **AND** exits with a non-zero status

#### Scenario: Missing repository surfaces the server error
- **WHEN** the user runs `mo project repo remove --project mohist-local missing-repo`
- **AND** no repository named `missing-repo` exists on the project
- **THEN** the server returns a not-found error
- **AND** the CLI prints a clear not-found message mentioning the repository name
- **AND** exits with a non-zero status

#### Scenario: Missing project surfaces the server error
- **WHEN** the user runs `mo project repo list --project does-not-exist`
- **AND** the project does not resolve
- **THEN** the server returns a not-found error
- **AND** the CLI prints a clear not-found message mentioning the project ref
- **AND** exits with a non-zero status

### Requirement: Repository subcommands support `--output` for `list`

`mo project repo list` SHALL accept `--output table|json` consistent with the `cli-output-modes` capability. `add`, `set-default`, and `remove` SHALL always print success/failure text and SHALL NOT accept `--output` (their response is a simple confirmation, not a dataset).

#### Scenario: Repo list supports table output
- **WHEN** the user runs `mo project repo list --project mohist-local --output table`
- **THEN** the CLI SHALL render the repository list as a columnar human-readable table
- **AND** the underlying API request SHALL be identical to the no-flag invocation

#### Scenario: Repo add does not accept `--output`
- **WHEN** the user runs `mo project repo add --project mohist-local --name api`
- **THEN** the CLI SHALL NOT advertise an `--output` option
- **AND** SHALL print a plain success or error message
