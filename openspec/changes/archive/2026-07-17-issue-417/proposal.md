## Why

Projects can now declare multiple repository resources, but an issue does not yet have a complete, enforceable target-repository lifecycle: users cannot reassign or filter issues by repository, and repository removal can orphan issue execution. Completing that contract now lets each issue run predictably in one codebase while preserving the zero-configuration experience for single-repository Projects.

## What Changes

- Bind every issue to one stable target repository resource name at creation. `--repo <name>` selects it explicitly; omitting the option binds the Project's current default, and later default changes do not silently retarget the issue.
- Reject unknown target repository names, allow an issue that has never started to move to another declared repository, and make the binding immutable once workflow execution has started.
- **BREAKING** Standardize the issue CLI option as `--repo`, replacing the current partial `--repository` option across repository-aware issue commands. Creation selects a target, update changes an eligible binding, list filters by target, and show/detail output identifies the bound repository.
- Route the complete workflow lifecycle through the target repository, including workspace and branch creation, diff and commit reads, rebase, and Integrate or pull-request delivery to that repository's configured base branch. Issues targeting different repositories must not share repository state or redirect one another's Git operations.
- **BREAKING** Reject deletion of a repository while any non-terminal issue is bound to it. Reassignment or entry into a terminal state releases that repository from this guard; the existing default-repository deletion rule remains in force.
- Preserve the existing single-repository experience: callers may omit `--repo`, and the Runner continues to receive one repository per issue, provided it can access every repository declared by the Project.

## Capabilities

- `issue-repository-binding`: The target-repository lifecycle for an issue, covering explicit or default selection, canonical validation, pre-start reassignment, post-start immutability, repository-filtered listing, and repository visibility on issue reads.
- `issue-repository-execution`: Repository-scoped workflow execution, ensuring all workspace, review, rebase, and delivery operations use the issue's bound repository and its current Project-managed metadata without cross-repository interference.
- `project-management`: Extends Project repository lifecycle rules so a repository referenced by any non-terminal issue cannot be removed.
- `repository-cli-commands`: Extends `mo repo delete` to surface the in-use repository conflict clearly and leave Project repository state unchanged.

## Impact

- **Server**: Issue domain state and lifecycle events, issue create/update/list/read API contracts, read projections and queries, Project repository removal policy, workflow startup context, and workspace-facing issue endpoints under `packages/server/`.
- **Persistence**: Persisted issue repository references and the query paths needed for repository filtering and unfinished-binding checks.
- **CLI**: `mo issue create`, `update`, `list`, and `show` options, payloads, filtering, and table output, plus repository-delete conflict reporting under `packages/cli/`.
- **Runner and workflows**: Repository variables, workspace materialization, Git review actions, rebase, local integration, and GitHub PR delivery under `packages/runner/` and the built-in workflow profiles. No new Runner protocol or package dependency is required, but Runner access and credentials must cover every declared repository that an issue may target.
- **Web/API consumers**: Existing issue creation and detail repository surfaces, and repository settings deletion behavior, must remain aligned with the strengthened server contract.
