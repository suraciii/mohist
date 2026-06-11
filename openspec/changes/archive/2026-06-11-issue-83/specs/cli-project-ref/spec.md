## ADDED Requirements

### Requirement: Project-scoped issue commands accept canonical `--project <name-or-id>`

Every `mo issue` subcommand that currently accepts `--project-id` SHALL also accept a canonical `--project <name-or-id>` option. The value SHALL be resolvable as either a project name (e.g. `mohist-local`) or a project id (e.g. `proj_f6c141d63b6243bfbb481737b2243b87`) by the server's `ProjectResolutionEndpointFilter`. Help text SHALL describe `--project` as the canonical option and SHALL continue to document `--project-id` as a backwards-compatible alias.

The set of project-scoped `mo issue` subcommands that gain `--project` SHALL include: `list`, `show`, `create`, `update`, `start`, `approve`, `reject`, `close`, `reopen`, `retry`, `rerun`, `force-stop`, `resume`, `rebase`, `archive`, `unarchive`, `logs`, `events`, `diff`, `commits`, `sessions`, `workflow status`, and `workflow timeline`.

#### Scenario: Project name resolves an issue
- **WHEN** the user runs `mo issue show 83 --project mohist-local`
- **THEN** the CLI resolves `mohist-local` to the matching project id
- **AND** sends `GET /api/projects/mohist-local/issues/83` to the server
- **AND** displays the issue detail

#### Scenario: Project id resolves the same issue
- **WHEN** the user runs `mo issue show 83 --project proj_f6c141d63b6243bfbb481737b2243b87`
- **THEN** the CLI resolves the project id through the same `ProjectResolutionEndpointFilter`
- **AND** returns the same issue detail as the project-name invocation

#### Scenario: Project name and project id resolve identically
- **WHEN** the user runs `mo issue show 83 --project mohist-local`
- **AND** runs `mo issue show 83 --project proj_f6c141d63b6243bfbb481737b2243b87`
- **THEN** both invocations return the same issue
- **AND** the server route handling is identical aside from the resolved project id

#### Scenario: Help text documents `--project` as canonical
- **WHEN** the user runs `mo issue show --help`
- **THEN** the help text SHALL describe `--project` as a project name or id
- **AND** SHALL mention `--project-id` as a backwards-compatible alias
- **AND** SHALL NOT describe `--project-id` as the only or primary option

### Requirement: `--project-id` remains a backwards-compatible alias

`--project-id` SHALL continue to be accepted on every project-scoped `mo issue` subcommand that accepts `--project`. `--project-id` SHALL be treated as the same option as `--project` for the purpose of project resolution. The CLI SHALL NOT remove `--project-id` until a separate compatibility decision is made.

#### Scenario: Existing scripts using `--project-id` still work
- **WHEN** the user runs `mo issue show 83 --project-id proj_f6c141d63b6243bfbb481737b2243b87`
- **THEN** the CLI resolves the project id
- **AND** returns the same issue detail as the equivalent `--project` invocation

#### Scenario: Alias resolves through the same helper
- **WHEN** the user passes only `--project-id`
- **THEN** the CLI SHALL resolve the project through the same shared helper as `--project`
- **AND** SHALL NOT branch on the option name in the request path

### Requirement: `--project` and `--project-id` must agree or fail with a clear error

When `--project` and `--project-id` are both passed and resolve to different project ids (after server-side name resolution), the CLI SHALL return a clear validation error and exit with a non-zero status. When the two options resolve to the same project id, the CLI SHALL proceed normally.

#### Scenario: Matching values are accepted
- **WHEN** the user runs `mo issue show 83 --project mohist-local --project-id proj_f6c141d63b6243bfbb481737b2243b87`
- **AND** the project id resolves to the same project as `mohist-local`
- **THEN** the CLI proceeds with the resolved project
- **AND** exits with status 0 on success

#### Scenario: Conflicting values fail with a clear error
- **WHEN** the user runs `mo issue show 83 --project mohist-local --project-id proj_other1234567890`
- **AND** the two values resolve to different projects
- **THEN** the CLI prints a clear validation error explaining that `--project` and `--project-id` disagree
- **AND** exits with a non-zero status
- **AND** does not silently pick one of the two values

#### Scenario: Conflict error mentions the correct fix
- **WHEN** the CLI detects a conflict between `--project` and `--project-id`
- **THEN** the error message SHALL explain how to fix it (pass only one of the two options)
- **AND** SHALL mention the option names that conflicted

### Requirement: Active project is the fallback when neither project option is passed

When neither `--project` nor `--project-id` is passed on a project-scoped `mo issue` subcommand, the CLI SHALL fall back to the active project stored by `mo project use` (read from `cli-state.json`). The active project SHALL NOT override an explicit `--project` or `--project-id` value.

#### Scenario: Active project is used when no option is passed
- **WHEN** the user has previously run `mo project use mohist-local`
- **AND** runs `mo issue show 83` with no project option
- **THEN** the CLI resolves the project from the active project state
- **AND** sends the request with the resolved project id
- **AND** the server returns the matching issue

#### Scenario: Active project is overridden by explicit option
- **WHEN** the user has previously run `mo project use mohist-local`
- **AND** runs `mo issue show 83 --project other-project`
- **THEN** the CLI SHALL use `other-project` and SHALL NOT use the active project
- **AND** the active project state remains unchanged

#### Scenario: No active project and no option yields a guided error
- **WHEN** the user runs `mo issue show 83` with no project option
- **AND** no active project has been set
- **THEN** the CLI prints `Run 'mo project use <name-or-id>' or pass --project <name-or-id>` (or a clearly equivalent diagnostic)
- **AND** exits with a non-zero status
- **AND** does not make any project-scoped request

### Requirement: Standardized "no active project" diagnostic guides the user

The "no active project" error surfaced by any project-scoped `mo issue` subcommand SHALL mention both remediation options: setting an active project via `mo project use` and passing `--project` on the failing command.

#### Scenario: Error mentions both remediation options
- **WHEN** a project-scoped `mo issue` subcommand fails because no active project is set and no `--project`/`--project-id` is passed
- **THEN** the CLI error message SHALL mention `mo project use <name-or-id>`
- **AND** SHALL mention `pass --project <name-or-id>` (or its alias)
- **AND** SHALL NOT mention `mo project use <project-id>` or other identifier-specific forms

#### Scenario: Diagnostic wording is consistent across issue subcommands
- **WHEN** the user triggers the "no active project" path on different issue subcommands (e.g. `issue show`, `issue list`, `issue sessions`)
- **THEN** all such errors SHALL use the same remediation wording
- **AND** the wording SHALL be rendered by a single shared helper to avoid drift
