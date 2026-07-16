### Requirement: `mo repo delete` honors Issue repository bindings

`mo repo delete <name>` SHALL remove the named repository only when the selected Project permits deletion: the repository exists, is non-default, and has no non-terminal Issue bindings. The command SHALL honor `--project`, `--project-id`, and active-Project resolution, SHALL report the server's deletion outcome, and MUST NOT reassign Issues or treat terminal historical references as active blockers.

#### Scenario: Delete an eligible repository

- **WHEN** a user runs `mo repo delete web` and `web` is non-default with no non-terminal Issue bindings
- **THEN** the command SHALL exit successfully
- **AND** it SHALL report the resulting repository state in the selected output mode

#### Scenario: Reject deletion of an in-use repository

- **WHEN** a user runs `mo repo delete web` and a non-terminal Issue in the selected Project is bound to `web`
- **THEN** the command SHALL print the in-use conflict
- **AND** it SHALL exit with a non-zero status
- **AND** it MUST NOT print a successful deletion result

#### Scenario: Terminal history does not block the command

- **WHEN** only `done` or `cancelled` Issues retain target `web`
- **AND** `web` is non-default
- **THEN** `mo repo delete web` SHALL succeed

#### Scenario: Explicit Project scope controls the guard

- **WHEN** a user runs `mo repo delete web --project product-a`
- **THEN** only repository and Issue bindings in `product-a` SHALL determine the result
- **AND** a same-named repository or Issue binding in another Project SHALL have no effect

### Requirement: In-use deletion failures are actionable

When repository deletion is rejected because of non-terminal Issue bindings, the CLI SHALL surface stable code `repository_in_use_deletion_conflict` and identify the Project, repository, and blocking Issue numbers. Table output SHALL provide a readable message directing the user to inspect blockers with `mo issue list --repo <name>` in the same Project and explaining that an Issue that has never started can be reassigned while other blockers must reach `done` or `cancelled`. JSON output SHALL be valid structured JSON containing the stable code, Project reference, repository name, and blocking Issue numbers. Failure output SHALL leave no ambiguity that the repository was not deleted.

#### Scenario: Table output explains how to release the repository

- **WHEN** `mo repo delete web --output table` is rejected because Issues 12 and 19 are non-terminal blockers
- **THEN** the CLI SHALL identify repository `web` and Issues 12 and 19
- **AND** it SHALL show an applicable `mo issue list --repo web` command or equivalent Project-scoped form
- **AND** it SHALL exit non-zero without reporting success

#### Scenario: JSON output preserves the stable conflict code

- **WHEN** `mo repo delete web --output json` is rejected because `web` is in use
- **THEN** the failure output SHALL be valid JSON with code `repository_in_use_deletion_conflict`
- **AND** it SHALL contain the selected Project reference, repository name `web`, and a structured list of blocking Issue numbers
- **AND** the command SHALL exit non-zero

### Requirement: Existing repository deletion errors retain their meaning

The Issue-binding guard SHALL NOT replace existing Project-resolution, not-found, or default-repository deletion behavior. A default repository deletion SHALL continue to instruct the user to select another default first, even when that repository is also in use. An unknown repository or unresolved Project SHALL remain an actionable failure and SHALL NOT be reported as an in-use conflict.

#### Scenario: Default conflict is reported before in-use conflict

- **WHEN** a user attempts to delete a repository that is both default and bound by non-terminal Issues
- **THEN** the CLI SHALL report the default-repository deletion conflict
- **AND** it SHALL instruct the user to run `mo repo set-default <other-name>` first

#### Scenario: Retry after changing the default exposes active bindings

- **WHEN** the user selects another default and retries deletion while non-terminal Issues still bind the repository
- **THEN** the CLI SHALL report `repository_in_use_deletion_conflict`
- **AND** the repository SHALL remain declared

#### Scenario: Unknown repository remains not found

- **WHEN** a user runs `mo repo delete missing` for a Project that does not declare `missing`
- **THEN** the CLI SHALL report a not-found error identifying `missing`
- **AND** it SHALL exit non-zero

#### Scenario: No Project can be resolved

- **WHEN** a user runs `mo repo delete web` without an explicit or active Project
- **THEN** the CLI SHALL explain how to select or pass a Project
- **AND** it SHALL exit non-zero without sending a deletion request
