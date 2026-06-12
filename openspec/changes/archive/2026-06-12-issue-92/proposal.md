## Why

Mohist currently models projects and repositories as local filesystem paths, which ties workflow execution to the user's checkout and lets merge, repair, and conflict-resolution work escape the intended workflow boundary. Removing path-based configuration now establishes the correct product model before more workflow and repository features build on the wrong abstraction.

## What Changes

- **BREAKING** Remove `project.path`, `project.effectivePath`, and equivalent local checkout fields from project domain models, API contracts, persisted rows, dispatch variables, CLI/Web project creation, and project settings.
- **BREAKING** Replace repository `path`/`remote` configuration with `gitUrl`; repository configuration contains `name`, `gitUrl`, `baseBranch`, and `isDefault`, and path-only repositories are rejected.
- **BREAKING** Stop exposing project or repository local execution paths to workflows; issue start variables expose repository metadata such as `repository.gitUrl` and `repository.baseBranch`, plus `workspace.path` as the only execution directory.
- Replace user-facing `worktree` language and assumptions with `workspace` language for workflow execution, review data, cleanup, and status surfaces.
- Runner prepares repositories from `gitUrl` into runner-owned cache/clone state under `MOHIST_RUNNER_ROOT`, then creates an isolated workflow workspace for each run or issue.
- Remove workflow execution dependence on `git worktree add`, `git worktree remove`, and `git worktree list`; workflow workspaces are runner-managed directories, not worktrees attached to the user's main checkout.
- Ensure workflow tasks, checks, scripts, OpenSpec sync/archive, ACP agent sessions, merge, rebase, repair, and conflict-resolution actions execute only inside `workspace.path` / `context.workDir`.
- Reshape workspace status, diff, commits, file content, and cleanup APIs so they operate on the workflow workspace model instead of project path plus `mo/issue-N` worktree assumptions.

## Capabilities

### New Capabilities
- None.

### Modified Capabilities
- `project-management`: Projects become Mohist scopes for issues, workflow configuration, and repository references without local filesystem paths; repository references require Git URL and base branch metadata.
- `http-api`: Project, repository, issue-start, review-data, workspace-status, file-content, and cleanup endpoints remove path/worktree contracts and expose Git URL plus workspace semantics instead.
- `web-ui`: Project creation and repository settings remove local path inputs and show Git URL/base branch configuration; user-facing worktree terminology becomes workspace terminology.
- `workflow-run`: Workflow start and runtime state expose repository metadata and `workspace.path` as the execution boundary without project or repository path variables.
- `workflow-agent`: Agent-backed workflow tasks execute inside the workflow workspace and cannot use project paths, repository cache paths, or user checkouts as working directories.
- `worktree-manager`: The old git worktree capability is replaced by runner-owned repository cache and isolated workflow workspace management, including workspace cleanup and review-data operations.

## Impact

- Backend project and repository domain models, EF persistence, API DTOs, validation, issue start dispatch variables, and tests.
- Runner workspace preparation, repository clone/cache handling, action context construction, merge/rebase/conflict repair actions, checks, scripts, OpenSpec sync/archive, and ACP agent execution.
- HTTP and CLI/Web contracts for project creation, repository management, workflow workspace status, diff, commits, file content, and cleanup.
- Web project creation, repository settings, issue review surfaces, and terminology that currently says `worktree` or asks for `Local Path`.
- Existing path-only project/repository data is not migrated because this change intentionally does not preserve legacy compatibility.
