### Requirement: Project-scoped commands expose one project reference

Every Project-scoped `mo` command SHALL accept exactly one project-selection option, `--project <name-or-id>`. The option SHALL accept either the Project's name or canonical id. `--project-id` and any other Project-selection alias MUST NOT resolve or appear in command help.

#### Scenario: A project name is supplied explicitly

- **WHEN** an operator invokes a Project-scoped command with `--project mohist-local`
- **THEN** the command SHALL use `mohist-local` as its Project reference

#### Scenario: A legacy project option is supplied

- **WHEN** an operator invokes a Project-scoped command with `--project-id proj_123`
- **THEN** the command SHALL reject the option as a usage error without contacting a Mohist service
- **AND** SHALL exit with code `2`

### Requirement: Project references resolve in a deterministic local order

For a Project-scoped command, the CLI SHALL resolve the Project from an explicit `--project` value first, then from the current-directory Project context, then from the locally selected Project. A later source MUST NOT replace a resolution from an earlier source.

#### Scenario: Explicit reference overrides local context

- **WHEN** the current directory and local selection identify Project A
- **AND** the command is invoked with `--project B`
- **THEN** the command SHALL target Project B

#### Scenario: Current-directory context precedes local selection

- **WHEN** no `--project` value is supplied
- **AND** the current directory identifies Project A and the local selection identifies Project B
- **THEN** the command SHALL target Project A

#### Scenario: Local selection is used as the final fallback

- **WHEN** no `--project` value is supplied
- **AND** the current directory has no Project context
- **AND** exactly one locally selected Project exists
- **THEN** the command SHALL target that locally selected Project

### Requirement: Unresolved and ambiguous references fail locally with recovery guidance

The CLI SHALL fail before issuing a domain request when no Project can be resolved or a selected source cannot identify exactly one Project. The diagnostic SHALL identify the failed reference source and include one executable next action that selects or explicitly supplies a Project.

#### Scenario: No Project can be resolved

- **WHEN** a Project-scoped command has no `--project` value, current-directory Project context, or local selection
- **THEN** the CLI SHALL write a diagnostic to stderr that includes `mo project use <name-or-id>` and `--project <name-or-id>`
- **AND** SHALL exit with code `1`
- **AND** SHALL NOT issue a domain request

#### Scenario: A name resolves ambiguously

- **WHEN** a Project reference or local Project source identifies more than one Project
- **THEN** the CLI SHALL write a diagnostic to stderr identifying the ambiguity and one executable disambiguating action
- **AND** SHALL exit with code `1`
- **AND** SHALL NOT issue the requested domain command

### Requirement: Local command discovery does not require a Project

Command help and any local argument or option validation SHALL be available without a selected Project, current-directory Project context, Server, or Runner.

#### Scenario: Help is requested without local or remote context

- **WHEN** an operator invokes a Project-scoped command with `--help` and no Project is available
- **THEN** the CLI SHALL display the command's help without resolving a Project or contacting a Mohist service
