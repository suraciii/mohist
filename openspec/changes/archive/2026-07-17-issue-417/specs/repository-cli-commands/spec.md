### Requirement: `mo repo delete` honors Issue repository bindings

`mo repo delete <name>` SHALL remove the named repository only when the selected Project permits deletion. The command SHALL use the Project selected by `--project`, `--project-id`, or active-Project resolution. It MUST NOT reassign Issues, treat terminal historical bindings as active blockers, or report success when the server rejects deletion.

#### Scenario: Delete an eligible repository

- **WHEN** a user runs `mo repo delete web` and `web` is non-default with no non-terminal Issue bindings in the selected Project
- **THEN** the command SHALL exit successfully and report the resulting repository state

#### Scenario: Reject deletion of an in-use repository

- **WHEN** a user runs `mo repo delete web` and a non-terminal Issue in the selected Project is bound to `web`
- **THEN** the command SHALL report an in-use conflict, exit non-zero, and MUST NOT print a successful deletion result

#### Scenario: Terminal history does not block deletion

- **WHEN** only `done` or `cancelled` Issues retain target `web` and `web` is non-default
- **THEN** `mo repo delete web` SHALL succeed

#### Scenario: Project scope controls the guard

- **WHEN** a user runs `mo repo delete web --project product-a`
- **THEN** only repository state and Issue bindings in `product-a` SHALL determine the result

### Requirement: Repository deletion failures remain actionable

When deletion is rejected because of non-terminal Issue bindings, the CLI SHALL identify the selected repository and state that deletion failed because it is still referenced by unfinished Issues. The command SHALL preserve the server failure, use a non-zero exit status, and MUST NOT report successful deletion. Existing Project-resolution, repository-not-found, and default-repository deletion failures SHALL retain their distinct meanings.

#### Scenario: In-use output identifies the repository

- **WHEN** deletion of repository `web` is rejected because it has non-terminal Issue bindings
- **THEN** the CLI SHALL state that deletion of `web` failed because it is in use, exit non-zero, and MUST NOT report that `web` was deleted

#### Scenario: Default deletion retains its existing meaning

- **WHEN** a user attempts to delete a repository that is both default and bound by non-terminal Issues
- **THEN** the CLI SHALL report that the default must be changed before deletion and SHALL NOT report successful deletion

#### Scenario: Unknown repository remains not found

- **WHEN** a user runs `mo repo delete missing` for a Project that does not declare `missing`
- **THEN** the CLI SHALL report a not-found failure identifying `missing` and exit non-zero

#### Scenario: No Project can be resolved

- **WHEN** a user runs `mo repo delete web` without an explicit or active Project
- **THEN** the CLI SHALL explain how to select a Project, exit non-zero, and send no deletion request
