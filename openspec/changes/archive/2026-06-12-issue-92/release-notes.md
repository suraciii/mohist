# Issue #92: Breaking change — projects are pathless, repositories use Git URL

This release removes the project and repository local-path contracts introduced
in earlier Mohist versions. Mohist no longer models a project as a local
checkout, and a repository is now identified by its remote Git URL and base
branch. Workflow execution is performed inside runner-managed **workspaces**
under `MOHIST_RUNNER_ROOT` and is not a git worktree attached to a user
checkout.

This is a **deliberate, non-migrating breaking change**. Existing projects and
repositories that were created with `project.path` or `repository.path` /
`repository.remote` must be re-created using the new contracts.

## What changed

### Projects are pathless

- `POST /api/projects` accepts `{ "name": "..." }` only.
- `project.path`, `project.effectivePath`, and any project-level
  `baseBranch` / `checkoutPath` fields are removed from the domain, EF
  persistence rows, API DTOs, read models, Web UI, and CLI.
- The backend no longer executes Git commands during project creation and no
  longer creates a default repository from a project path.
- `ProjectInfo`, `ProjectQuerier`, `ProjectGrain`, `MohistProjectList`, and the
  `/api/projects` endpoints are free of local checkout fields.

### Repositories require a Git URL

- `POST /api/projects/{id}/repositories` requires `gitUrl` and rejects
  path-only requests with a 400-class validation error.
- Repository model contains `name`, `gitUrl`, `baseBranch`, and `isDefault`.
- `path`, `remote`, `resolvedPath`, and `remoteUrl` are removed from the
  domain, persistence, API, read models, and Web UI.
- The `Remote` field is renamed to `gitUrl` everywhere public (no internal
  `Remote` field is preserved on the wire).
- `PATCH /api/projects/{id}/repositories/{name}` accepts `gitUrl`/`baseBranch`
  updates and rejects path-only bodies.

### Runner owns workflow workspaces

- Workflow workspace preparation clones/fetches the repository `gitUrl` into
  a runner-owned cache under `MOHIST_RUNNER_ROOT` and creates a separate
  runner-managed workspace directory for the active run or issue.
- The runner no longer uses `git worktree add`, `git worktree list`,
  `git worktree remove`, or `git worktree prune` for workflow execution.
- Repository cache paths are runner implementation details and are never
  exposed as project/repository config, dispatch variables, or action cwd.
- The workflow workspace identity is persisted on `WorkflowRun` for resume,
  review data, and cleanup.
- Workflow dispatch variables expose only `repository.gitUrl`,
  `repository.baseBranch`, and `workspace.path` (plus `workspace.branch`,
  `workspace.changeDir`, `project.id`, `project.name`).
  No `project.path`, `project.effectivePath`, `repository.path`,
  `repository.remote`, `repository.resolvedPath`, repository cache path, or
  user checkout path fields are included.
- ACP agent sessions, named session reuse, scripts, checks, OpenSpec
  sync/archive, rebase, repair, merge, and conflict resolution actions all
  execute with `context.workDir == workspace.path`.
- `mohist/merge` performs the authoritative squash merge inside the workflow
  workspace; the conflict resolver inherits the workspace cwd and never
  operates in `project.path`, the repository cache, or a user checkout.

### Review and workspace APIs use workspace terminology

- `GET /api/projects/{id}/issues/{n}/workspace-status` replaces the old
  `worktree-status` route; the legacy route returns 404.
- Issue diff, commits, commit diff, file content, status, and cleanup
  responses operate on the workflow workspace and use workspace terminology.
- Diff, commit, and status payloads use `merge-base` comparison and
  availability-aware reasons (`workspace_removed`, `not_started`,
  `branch_missing`, `git_error`).

### Web UI uses workspace terminology

- `CreateProjectDialog` accepts only a project name; there is no path input,
  no filesystem browser, no recent-directory selector, and no
  `Path is required` validation.
- `Settings > Repositories` shows and edits `name`, `gitUrl`, `baseBranch`,
  and `isDefault`. There is no Local Path input.
- Review surfaces (diff, commits, changed files, file content, status) use
  `Workspace` headings, workspace path data, and workspace removal copy.
- `WorkspacePanel` replaces the legacy `WorktreePanel` widget.

### CLI is pathless and uses Git URL

- `mohist project create <name>` sends `POST /api/projects` with a name-only
  body; there is no `--path`, `--base-branch`, or `--git-url` flag for
  project creation.
- `mohist project list` prints project names with a current marker only; no
  local path column.
- `mohist repository add <name> --git-url <url> --base-branch <branch>` is
  the only supported repository create flow; path-only input is rejected
  before the API call.
- `mohist repository update` accepts `--git-url`, `--base-branch`,
  `--new-name`, and `--set-default`; no `--path` or `--remote` flag.

## Migration guidance

There is **no compatibility migration**. Users upgrading from a previous
version must:

1. Re-create projects with `mohist project create <name>` (no path).
2. Re-add each repository with the new contract:
   `mohist repository add <name> --git-url <repo-url> --base-branch <branch>`.
3. Recreate or resume any in-flight workflows; old `mo/issue-N` worktree
   references are not preserved.

## Why this is breaking

Mohist is still in active development. The legacy project-path and
repository-path contracts tied workflow execution to the user's main checkout
and allowed merge, repair, and conflict resolution work to escape the
intended workflow boundary (see issue #82). The new model mirrors the
GitHub Actions and Azure Pipelines separation: repository identity is
remote/ref metadata, while local checkout/workspace is runner-managed
runtime state.

## Verification

- Verification is recorded by the issue workflow task ledger in
  `openspec/changes/issue-92/tasks.json`; each issue task is marked passing
  after its focused backend, runner, CLI, or Web checks completed.
- `PathContractRegressionSpecs` covers the public surface for the removed
  fields.
- Existing runner `workspace.spec.ts` includes the negative assertion
  `WorkspacePreparation_DoesNotUseGitWorktreeCommands` to enforce the
  no-worktree invariant.
