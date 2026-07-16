## MODIFIED Requirements

### Requirement: `mo repo` is the single repository-management command

The CLI SHALL expose project repository management through the top-level `mo repo` command, with `repository` accepted as an alias. The command SHALL provide exactly `list`, `add`, `update`, `set-default`, and `delete`. Repository scope SHALL be expressed with `--project <name-or-id>` or `--project-id <id>`, with active project fallback as defined by the `cli-project-ref` capability. The former nested project repository command group SHALL be parser-rejected and SHALL NOT be retained as an alias.

#### Scenario: Repo help lists the complete command surface
- **WHEN** the user runs `mo repo --help`
- **THEN** the usage SHALL advertise `repo` as the command name
- **AND** the output SHALL list `list`, `add`, `update`, `set-default`, and `delete`
- **AND** `repository` SHALL remain accepted as an alias for the same command group

#### Scenario: Repo list returns the repository list
- **WHEN** the user runs `mo repo list --project mohist-local`
- **THEN** the CLI sends `GET /api/projects/mohist-local/repositories` to the server
- **AND** displays the repositories according to the selected output mode

#### Scenario: Repo add creates a repository
- **WHEN** the user runs `mo repo add api --git-url git@example.com:api.git --set-default --project mohist-local`
- **THEN** the CLI sends `POST /api/projects/mohist-local/repositories` with `name`, `gitUrl`, `baseBranch`, and `setDefault` fields
- **AND** the CLI SHALL NOT send legacy `path`, `remote`, or `resolvedPath` fields

#### Scenario: Repo update patches supplied fields
- **WHEN** the user runs `mo repo update api --git-url git@example.com:backend.git --project mohist-local`
- **THEN** the CLI sends `PATCH /api/projects/mohist-local/repositories/api` with only the supplied fields

#### Scenario: Repo set-default sets the default
- **WHEN** the user runs `mo repo set-default api --project mohist-local`
- **THEN** the CLI sends `PATCH /api/projects/mohist-local/repositories/api` with `{ setDefault: true }`

#### Scenario: Repo delete removes a repository
- **WHEN** the user runs `mo repo delete api --project mohist-local`
- **THEN** the CLI sends `DELETE /api/projects/mohist-local/repositories/api` to the server
- **AND** `remove` and `rm` SHALL be accepted as aliases for `delete`

### Requirement: Repository subcommands accept project scope and output options consistently

Every `mo repo` subcommand SHALL accept both `--project` and `--project-id`. When neither flag is passed, the CLI SHALL fall back to the active project stored by `mo project use`. When no project can be resolved, the CLI SHALL print the standard project selection diagnostic, exit with a non-zero status, and make no server request. Every subcommand SHALL accept `--output table|json` through the shared output option.

#### Scenario: Explicit project is used
- **WHEN** the user runs `mo repo list --project mohist-local`
- **THEN** the CLI resolves `mohist-local` to the project id
- **AND** sends the request to the resolved project route

#### Scenario: Active project is used as fallback
- **WHEN** the user has previously run `mo project use mohist-local`
- **AND** runs `mo repo list` with no project flag
- **THEN** the CLI SHALL use the active project
- **AND** SHALL send the request to the resolved active project route

#### Scenario: No active project and no option yields the standard diagnostic
- **WHEN** the user runs `mo repo list` with no project flag
- **AND** no active project is set
- **THEN** the CLI prints `Run 'mo project use <name-or-id>' or pass --project <name-or-id>` (or clearly equivalent wording)
- **AND** exits with a non-zero status
- **AND** does not make a server request

#### Scenario: `--project-id` alias is accepted
- **WHEN** the user runs `mo repo list --project-id proj_f6c141d63b6243bfbb481737b2243b87`
- **THEN** the CLI SHALL treat `--project-id` as a backwards-compatible alias for `--project`
- **AND** SHALL send the request to the resolved project route

#### Scenario: Repo add rejects the removed default flag
- **WHEN** the user runs `mo repo add api --git-url git@example.com:api.git --default --project mohist-local`
- **THEN** the CLI SHALL reject `--default`
- **AND** SHALL make no server request

### Requirement: Repository subcommands surface server conflicts and not-found errors

When a repository subcommand receives a server error (e.g. duplicate name, repository not found, project not found), the CLI SHALL surface the server error message verbatim (or with a clearly equivalent readable wrapper) and SHALL exit with a non-zero status. The CLI SHALL NOT retry on conflict and SHALL NOT swallow not-found errors.

#### Scenario: Duplicate repository name surfaces the server error
- **WHEN** the user runs `mo repo add api --git-url git@example.com:api.git --project mohist-local`
- **AND** a repository named `api` already exists
- **THEN** the server returns a conflict error
- **AND** the CLI prints a clear conflict message mentioning the duplicate name
- **AND** exits with a non-zero status

#### Scenario: Missing repository surfaces the server error
- **WHEN** the user runs `mo repo delete missing-repo --project mohist-local`
- **AND** no repository named `missing-repo` exists on the project
- **THEN** the server returns a not-found error
- **AND** the CLI prints a clear not-found message mentioning the repository name
- **AND** exits with a non-zero status

#### Scenario: Missing project surfaces the server error
- **WHEN** the user runs `mo repo list --project does-not-exist`
- **AND** the project does not resolve
- **THEN** the server returns a not-found error
- **AND** the CLI prints a clear not-found message mentioning the project ref
- **AND** exits with a non-zero status
