### Requirement: `mo repo` provides the complete project repository command surface

The CLI SHALL expose repository management through the top-level `mo repo` command. It SHALL provide the canonical subcommands `list`, `add`, `update`, `set-default`, and `delete`. Every subcommand SHALL accept `--project <name-or-id>` and the `--project-id <id>` alias, SHALL use the active Project when neither option is present, and SHALL support the shared `--output table|json` option.

#### Scenario: Repository help lists the canonical commands
- **WHEN** the user runs `mo repo --help`
- **THEN** the output SHALL list `list`, `add`, `update`, `set-default`, and `delete`

#### Scenario: Use an explicitly scoped Project
- **WHEN** the user runs `mo repo list --project product-a`
- **THEN** the CLI SHALL target the Project resolved from `product-a`
- **AND** SHALL NOT read repositories from any other Project

#### Scenario: Use the active Project
- **WHEN** the user runs a `mo repo` subcommand without `--project` or `--project-id`
- **AND** an active Project is configured
- **THEN** the CLI SHALL target that active Project

#### Scenario: No Project can be resolved
- **WHEN** the user runs a `mo repo` subcommand without an explicit or active Project
- **THEN** the CLI SHALL print an actionable message that tells the user to select or pass a Project
- **AND** SHALL exit with a non-zero status
- **AND** SHALL make no repository request

### Requirement: `mo project create` bootstraps the default repository from a Git path

`mo project create <name> --path <path>` SHALL create a repository-backed Project from the existing Git repository at `<path>`. The `--path` option MUST be supplied. Before requesting creation, the CLI MUST resolve a deterministic non-empty repository resource name, a Git URL usable by a Runner to access the same repository, and a base branch from that Git repository. The path SHALL be bootstrap input only and MUST NOT be sent or stored as a project-level path field; the creation request SHALL contain the resolved repository declaration instead.

#### Scenario: Create a Project from an explicit path
- **WHEN** the user runs `mo project create product-a --path /work/product-a` for a valid Git repository
- **THEN** the CLI SHALL request creation of `product-a` with one repository declaration describing that Git repository
- **AND** the created repository SHALL be marked default
- **AND** the successful result SHALL expose the repository's resource name, Git URL, and base branch

#### Scenario: Reject Project creation without a path
- **WHEN** the user runs `mo project create product-a` without `--path`
- **THEN** the CLI SHALL print an actionable validation error mentioning `--path`
- **AND** SHALL exit with a non-zero status
- **AND** SHALL NOT request Project creation

#### Scenario: Reject a path that cannot produce repository metadata
- **WHEN** the selected path is missing, is not a Git repository, has no commit, or cannot provide a usable Git URL or base branch
- **THEN** the CLI SHALL print an actionable validation error identifying the repository metadata it could not resolve
- **AND** SHALL exit with a non-zero status
- **AND** SHALL NOT create a Project

### Requirement: `mo repo list` identifies the default repository

`mo repo list` SHALL list the selected Project's repositories with their resource name, Git URL, base branch, and default status. Human-readable output MUST visibly identify the sole default repository, and JSON output MUST expose the `isDefault` value for every repository. List output SHALL NOT present legacy repository path or remote-alias fields in place of the Git URL.

#### Scenario: List two repositories in table output
- **WHEN** the selected Project contains default repository `server` and non-default repository `web`
- **AND** the user runs `mo repo list --output table`
- **THEN** the output SHALL display both repository names, Git URLs, and base branches
- **AND** SHALL visibly mark `server` as default and `web` as non-default

#### Scenario: List repositories in JSON output
- **WHEN** the user runs `mo repo list --output json`
- **THEN** the output SHALL contain each repository's `name`, `gitUrl`, `baseBranch`, and `isDefault`
- **AND** exactly one returned repository SHALL have `isDefault` set to `true`

### Requirement: Repository command output remains coherent

Every `mo repo` subcommand SHALL render successful results according to the selected output mode. Table output SHALL describe repositories or report an unambiguous successful mutation, while JSON output SHALL expose the successful server result as JSON. Repository commands MUST NOT render unrelated Project-list empty states.

#### Scenario: Repository mutations use repository-appropriate output
- **WHEN** a repository mutation succeeds with `--output table`
- **THEN** the CLI SHALL render the resulting repository state or an unambiguous success result
- **AND** SHALL NOT render an unrelated empty Project list

#### Scenario: Repository mutation returns JSON
- **WHEN** a repository mutation succeeds with `--output json`
- **THEN** the CLI SHALL render its successful result as JSON

### Requirement: `mo repo add` declares a repository

`mo repo add <name> --git-url <url> [--base-branch <branch>] [--set-default]` SHALL add a repository to the selected Project. `--git-url` MUST be present and non-empty. If `--base-branch` is omitted, the added repository SHALL use `main`. If `--set-default` is present, the added repository SHALL become the sole default; otherwise the current default SHALL remain selected.

#### Scenario: Add a non-default repository
- **WHEN** the user runs `mo repo add web --git-url https://example.com/web.git`
- **THEN** the CLI SHALL request a repository named `web` with that Git URL and base branch `main`
- **AND** the Project's existing default SHALL remain selected

#### Scenario: Add and select a new default
- **WHEN** the user runs `mo repo add web --git-url https://example.com/web.git --base-branch develop --set-default`
- **THEN** the CLI SHALL request the supplied name, Git URL, and base branch with default selection enabled
- **AND** the successful result SHALL identify `web` as the sole default

#### Scenario: Add without a Git URL
- **WHEN** the user runs `mo repo add web` without `--git-url`
- **THEN** the CLI SHALL print an actionable validation error mentioning `--git-url`
- **AND** SHALL exit with a non-zero status
- **AND** SHALL make no repository mutation request

### Requirement: `mo repo update` changes repository metadata without changing identity

`mo repo update <name> [--git-url <url>] [--base-branch <branch>]` SHALL update only the supplied Git URL and base branch of the named repository. The command MUST require at least one of those options. It SHALL NOT accept repository renaming or default selection; the resource name remains stable and `mo repo set-default` is the sole command for changing the default of an existing repository.

#### Scenario: Update one metadata field
- **WHEN** the user runs `mo repo update web --base-branch release`
- **THEN** the CLI SHALL request only the base branch change for repository `web`
- **AND** the repository's name, Git URL, and default status SHALL remain unchanged

#### Scenario: Update both metadata fields
- **WHEN** the user runs `mo repo update web --git-url https://example.com/new-web.git --base-branch develop`
- **THEN** the CLI SHALL request both supplied changes for repository `web`
- **AND** SHALL NOT request a name or default-status change

#### Scenario: Update without any supported change
- **WHEN** the user runs `mo repo update web` without `--git-url` or `--base-branch`
- **THEN** the CLI SHALL print an actionable validation error
- **AND** SHALL exit with a non-zero status
- **AND** SHALL make no repository mutation request

#### Scenario: Reject identity and default options on update
- **WHEN** the user passes `--new-name` or `--set-default` to `mo repo update`
- **THEN** the CLI SHALL reject the unsupported option
- **AND** SHALL make no repository mutation request

### Requirement: `mo repo set-default` switches the Project default

`mo repo set-default <name>` SHALL select an existing repository as the sole default for the selected Project. The command SHALL succeed without changing repository metadata when the named repository is already default.

#### Scenario: Switch the default repository
- **WHEN** the user runs `mo repo set-default web`
- **AND** `web` is a non-default repository in the selected Project
- **THEN** the successful result SHALL identify `web` as the sole default

#### Scenario: Select the current default
- **WHEN** the user runs `mo repo set-default server`
- **AND** `server` is already the default repository
- **THEN** the command SHALL succeed
- **AND** repository metadata and membership SHALL remain unchanged

### Requirement: `mo repo delete` removes only non-default repositories

`mo repo delete <name>` SHALL delete an existing non-default repository from the selected Project. The command MUST NOT delete the default repository. When default deletion is attempted, the CLI SHALL exit unsuccessfully and display an actionable conflict that tells the user to run `mo repo set-default <other-name>` first.

#### Scenario: Delete a non-default repository
- **WHEN** the user runs `mo repo delete web`
- **AND** `web` is not the selected Project's default
- **THEN** the CLI SHALL delete `web`
- **AND** the existing default repository SHALL remain default

#### Scenario: Reject deletion of the default repository
- **WHEN** the user runs `mo repo delete server`
- **AND** `server` is the selected Project's default
- **THEN** the CLI SHALL print a conflict identifying `server` as the default
- **AND** SHALL tell the user to select another default first
- **AND** SHALL exit with a non-zero status
- **AND** no repository SHALL be deleted

### Requirement: Repository command failures are actionable

Repository commands SHALL surface validation, conflict, not-found, and Project-resolution failures as readable error output and SHALL exit with a non-zero status. An error MUST identify the affected Project or repository when one was supplied, and a failed mutation MUST NOT be reported as successful or silently retried.

#### Scenario: Duplicate name is reported
- **WHEN** the user attempts to add `server` to a Project that already contains repository `server`
- **THEN** the CLI SHALL print a conflict that identifies `server` as already declared
- **AND** SHALL exit with a non-zero status

#### Scenario: Repository is not found
- **WHEN** the user attempts to update, select, or delete repository `missing`
- **THEN** the CLI SHALL print a not-found error identifying `missing`
- **AND** SHALL exit with a non-zero status

#### Scenario: Project is not found
- **WHEN** the user runs a repository command with `--project missing-project`
- **AND** that Project cannot be resolved
- **THEN** the CLI SHALL print a not-found error identifying `missing-project`
- **AND** SHALL exit with a non-zero status
