## Why

Project creation currently yields an empty repository list, while the partial repository API can admit no default or silently replace a deleted default, so a Project is not yet a reliable workspace for products split across several codebases. Completing the resource contract now gives later issue-to-repository binding a stable reference model and lets existing single-repository projects upgrade without interrupting their issues.

## What Changes

- Make every Project own one or more named repository resources, each with a project-local unique name, Git URL, and base branch, with exactly one repository marked as default.
- Complete repository lifecycle enforcement across the existing API: adding, updating, listing, and switching the default preserve the invariant, while duplicate resource names are rejected.
- **BREAKING** Reject deletion of the default repository instead of deleting it and silently promoting another repository; the user must first select a different default.
- **BREAKING** Replace repository-less Project creation with repository-backed creation. `mo project create <name> --path <path>` registers the supplied Git repository as that Project's default repository, preserving the one-command single-repository experience.
- Complete the top-level command surface: `mo repo list`; `mo repo add <name> --git-url <url> [--base-branch <branch>] [--set-default]`; `mo repo update <name> [--git-url <url>] [--base-branch <branch>]`; `mo repo set-default <name>`; and `mo repo delete <name>`. List output identifies the default repository and mutation failures remain actionable.
- Upgrade each existing Project's repository declaration into its default repository without changing its repository metadata or disrupting existing issue startup and execution.
- Do not add issue target-repository selection, cross-Project repository sharing, one-issue multi-repository execution, or multi-repository release coordination in this change. Issue execution that does not select a repository continues to use the Project default.

## Capabilities

- `project-management`: Project-owned repository declarations, repository-name uniqueness, the exactly-one-default invariant, repository lifecycle rules, repository-backed Project creation, and upgrade continuity for existing Projects and issues.
- `repository-cli-commands`: The `mo repo` management surface and the `mo project create --path` bootstrap, including project scoping, default identification, and actionable validation/conflict output.

## Impact

- **Server**: Project domain and grain behavior, Project/repository API contracts, Project query models, and focused issue/workflow default-repository regression paths under `packages/server/`.
- **Persistence**: Existing `Projects.RepositoriesJson` data and database upgrade logic must preserve repository metadata while establishing one default per Project.
- **CLI**: Project creation, repository commands, repository table rendering, HTTP payloads, and command specs under `packages/cli/`.
- **API consumers**: Project creation and repository-management consumers, including the Web project/settings surfaces, must align with repository-backed creation and default-delete conflicts; this issue adds no new Web management capability.
- **Execution plane and dependencies**: No new dependency or Runner protocol is required; workflows continue to consume the resolved repository's Git URL and base branch.
