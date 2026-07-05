### Requirement: mo repo is the single repository-management entry point

The CLI SHALL expose repository management exclusively through the top-level
`mo repo` command (alias `repository`). `mo repo` SHALL provide exactly the
subcommands `list`, `add`, `update`, `set-default`, and `delete` — the complete
operation set, consolidated from the former double track. The nested
`mo project repo` command group SHALL NOT exist: it MUST be removed with no
alias and no hidden registration, so that any invocation beginning with
`mo project repo` is rejected by the parser as an unrecognized command. This is
a breaking removal; users migrate to `mo repo` with the project expressed via
`--project`/`--project-id`.

#### Scenario: mo repo help lists the complete subcommand set

- **WHEN** the user runs `mo repo --help`
- **THEN** the output SHALL list the subcommands `list`, `add`, `update`, `set-default`, and `delete`
- **AND** SHALL NOT list any other repository subcommand

#### Scenario: The nested mo project repo path is gone

- **WHEN** the user runs `mo project repo list` (or any `mo project repo <subcommand>`)
- **THEN** the CLI SHALL reject the invocation as an unrecognized command
- **AND** SHALL exit with a non-zero status
- **AND** SHALL NOT dispatch any request to `/api/projects/:projectId/repositories`

### Requirement: Repository name is a positional argument on every mutating subcommand

The `add`, `update`, `set-default`, and `delete` subcommands SHALL take the
repository name as a positional `<name>` argument — not as a `--name` option.
This SHALL be consistent across all four mutating verbs and SHALL match the
positional resource-identifier style used by `mo project`, `mo issue`,
`mo epic`, and `mo agent`.

#### Scenario: add takes the repository name positionally

- **WHEN** the user runs `mo repo add origin --git-url <url> --project <p>`
- **THEN** the CLI SHALL accept `origin` as the positional `<name>` argument
- **AND** SHALL NOT require a `--name` option

#### Scenario: set-default takes the repository name positionally

- **WHEN** the user runs `mo repo set-default origin --project <p>`
- **THEN** the CLI SHALL accept `origin` as the positional `<name>` argument

### Requirement: Project scope is expressed via flag on every subcommand

Every `mo repo` subcommand SHALL resolve the target project through the shared
`ProjectRefOption()`, accepting both `--project <name-or-id>` (canonical) and
`--project-id <id>` (backwards-compatible alias), in addition to the active
project fallback. No subcommand SHALL accept `--project-id` alone. When no
project can be resolved, the CLI SHALL print a clear error and exit with a
non-zero status without dispatching a request.

#### Scenario: --project resolves the project scope

- **WHEN** the user runs `mo repo list --project my-project`
- **THEN** the CLI SHALL resolve `my-project` to a project id
- **AND** SHALL dispatch the request against `/api/projects/<resolved-id>/repositories`

#### Scenario: --project-id resolves the project scope

- **WHEN** the user runs `mo repo list --project-id proj_123`
- **THEN** the CLI SHALL dispatch the request against `/api/projects/proj_123/repositories`

#### Scenario: Every subcommand accepts the project reference

- **WHEN** the user runs any of `mo repo list`, `mo repo add <name>`, `mo repo update <name>`, `mo repo set-default <name>`, or `mo repo delete <name>` with `--project <name>`
- **THEN** the CLI SHALL accept the `--project` flag on each subcommand
- **AND** SHALL resolve the project before dispatching

#### Scenario: No resolvable project fails clearly

- **WHEN** the user runs a `mo repo` subcommand with no resolvable project (no `--project`/`--project-id` and no active project)
- **THEN** the CLI SHALL print a clear error explaining no project is resolved
- **AND** SHALL exit with a non-zero status
- **AND** SHALL NOT dispatch any request

### Requirement: add requires a git URL and uses --set-default to mark the default

`mo repo add <name>` SHALL send `POST /api/projects/:projectId/repositories`
with a body containing at least `name`, `gitUrl`, `baseBranch`, and
`isDefault`. The command SHALL require `--git-url <url>` (short form `-u`); when
it is absent the CLI SHALL print a clear validation error and exit with a
non-zero status without dispatching. The command SHALL accept the optional
`--base-branch <branch>` (short form `-b`) and `--set-default` (short form
`-d`) flags. The "set as default" flag SHALL be named `--set-default`; the
former `--default` flag MUST NOT be accepted.

#### Scenario: add sends the repository metadata

- **WHEN** the user runs `mo repo add origin --git-url git@example.com:repo.git --base-branch main --set-default --project <p>`
- **THEN** the CLI sends `POST /api/projects/:projectId/repositories`
- **AND** the request body SHALL carry `name` = `origin`, `gitUrl` = `git@example.com:repo.git`, `baseBranch` = `main`, and `isDefault` = `true`

#### Scenario: add without --git-url is rejected

- **WHEN** the user runs `mo repo add origin --project <p>` without `--git-url`
- **THEN** the CLI SHALL print a clear validation error mentioning `--git-url`
- **AND** SHALL exit with a non-zero status
- **AND** SHALL NOT dispatch any request

#### Scenario: the dropped --default flag is rejected

- **WHEN** the user runs `mo repo add origin --git-url <url> --default`
- **THEN** the CLI SHALL reject the `--default` flag
- **AND** SHALL exit with a non-zero status

### Requirement: update patches an existing repository

`mo repo update <name>` SHALL send
`PATCH /api/projects/:projectId/repositories/<name>` with a body composed from
the optional flags `--git-url <url>` (`-u`), `--base-branch <branch>` (`-b`),
`--new-name <name>` (`-n`), and `--set-default` (`-d`). Only the flags present
SHALL be carried in the request body. The "set as default" flag SHALL be named
`--set-default`.

#### Scenario: update sends a patch with the supplied fields

- **WHEN** the user runs `mo repo update origin --new-name upstream --git-url <new-url> --base-branch develop --project <p>`
- **THEN** the CLI sends `PATCH /api/projects/:projectId/repositories/origin`
- **AND** the request body SHALL carry `newName`, `gitUrl`, and `baseBranch`

### Requirement: set-default marks a repository as the project default

`mo repo set-default <name>` SHALL send
`PATCH /api/projects/:projectId/repositories/<name>` with a body carrying
`setDefault` = `true`. This subcommand SHALL be present on `mo repo` (migrated
from the former nested group, where the top-level path previously lacked it).

#### Scenario: set-default sends the default flag

- **WHEN** the user runs `mo repo set-default origin --project <p>`
- **THEN** the CLI sends `PATCH /api/projects/:projectId/repositories/origin`
- **AND** the request body SHALL carry `setDefault` = `true`

### Requirement: delete is the primary delete verb with remove and rm aliases

`mo repo delete <name>` SHALL send
`DELETE /api/projects/:projectId/repositories/<name>`. `delete` SHALL be the
primary (canonical) verb name; `remove` and `rm` SHALL be accepted as aliases
that dispatch the identical request. The former arrangement where `remove` was
primary and `delete` an alias MUST be flipped.

#### Scenario: delete removes the repository

- **WHEN** the user runs `mo repo delete origin --project <p>`
- **THEN** the CLI sends `DELETE /api/projects/:projectId/repositories/origin`

#### Scenario: remove and rm alias delete

- **WHEN** the user runs `mo repo remove origin --project <p>` or `mo repo rm origin --project <p>`
- **THEN** the CLI SHALL dispatch the same `DELETE /api/projects/:projectId/repositories/origin` request as `delete`

### Requirement: Output goes through the shared output option

Every `mo repo` subcommand SHALL render its result through the shared
`OutputOption()` factory, exposing `-o table|json` (short form `-o`). `list`
SHALL render via the shared `PrintWithOutputAsync` path using the repository
list table shape for `-o table` and the raw server payload for `-o json`. The
subcommands SHALL NOT use raw, unformatted print calls.

#### Scenario: list renders table output

- **WHEN** the user runs `mo repo list -o table --project <p>`
- **THEN** the CLI sends `GET /api/projects/:projectId/repositories`
- **AND** the rendered output SHALL be the human-readable repository list table

#### Scenario: list renders JSON output

- **WHEN** the user runs `mo repo list -o json --project <p>`
- **THEN** the CLI SHALL print the raw server payload as JSON
