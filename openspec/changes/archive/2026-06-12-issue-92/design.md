## Context

Mohist currently treats a Project and Repository as local checkout paths. `project.path`, `project.effectivePath`, `repository.path`, `repository.remote`, and `repository.resolvedPath` leak through domain models, API contracts, dispatch variables, Web UI settings, and runner workspace preparation. Runner setup prefers local paths, creates issue work areas with `git worktree add`, and some integration work can switch back to the user's main checkout.

This breaks the intended workflow execution boundary. A Project should be a Mohist scope for issues, workflow configuration, and repository references. A Repository should be remote source metadata. The only local directory that workflow work may use is the runner-created workflow workspace. Repository caches and clones are runner implementation details under `MOHIST_RUNNER_ROOT`.

The proposal explains the motivation and acceptance criteria. The specs define required contract changes across `project-management`, `http-api`, `web-ui`, `workflow-run`, `workflow-agent`, and `worktree-manager`. This design intentionally assumes a breaking change: Mohist is still in active development, so no path-only compatibility layer or legacy data migration is required.

Stakeholders are CLI users, Web UI users, workflow authors, backend API consumers, and the runner/action runtime. The most important invariant is that tasks, checks, repairs, merge, conflict resolution, OpenSpec operations, scripts, and ACP agent sessions execute only inside the workflow workspace.

## Goals / Non-Goals

**Goals:**

- Remove project local path fields from domain, persistence, API/read models, workflow variables, CLI/Web requests, and UI labels.
- Replace repository `path`/`remote` configuration with `name`, `gitUrl`, `baseBranch`, and `isDefault`.
- Prepare repositories from `gitUrl` into runner-owned cache state under `MOHIST_RUNNER_ROOT`.
- Create or reuse an isolated workflow workspace per active workflow run or issue without using git worktrees attached to a user checkout.
- Expose `workspace.path` as the only local execution directory in workflow dispatch.
- Enforce `context.workDir == workspace.path` for agent tasks, scripts, checks, OpenSpec actions, merge, rebase, repair, and conflict resolution.
- Reshape status, diff, commits, file content, cleanup, and Web labels around workspace terminology instead of worktree terminology.
- Add tests covering API contracts, dispatch variables, runner workspace preparation from Git URL, cwd isolation, merge/conflict isolation, and Web repository settings.

**Non-Goals:**

- Preserve compatibility with old path-only projects or repositories.
- Migrate existing path-only project/repository data into new repository metadata.
- Add credential management for private Git URLs.
- Add remote hosting integration, PR creation, or push workflows.
- Allow actions or workflow templates to opt out of workspace isolation.
- Expose repository cache paths as public API fields, workflow variables, or action cwd choices.

## Decisions

### Decision 1: Make Project Pathless

Project domain and read models will represent only Mohist scope metadata such as id, name, current/default flags, workflow settings, and repository references. Project creation accepts `{ name }` and does not inspect the filesystem or infer Git branch information.

Rationale: project scope is a product concept, not a checkout. Removing path at the source prevents downstream APIs, workflow variables, and UI from depending on a user checkout.

Alternatives considered:

- Keep `project.path` nullable for legacy users. Rejected because the issue explicitly disallows backwards compatibility and nullable path fields would keep the wrong abstraction alive.
- Keep project-level `baseBranch`. Rejected because base branch belongs to a repository reference, not the Mohist project scope.
- Store a runner workspace path on Project. Rejected because workspaces are per run/issue runtime facts, not project configuration.

### Decision 2: Model Repository As Remote Metadata Only

Repository configuration will contain `name`, `gitUrl`, `baseBranch`, and `isDefault`. API create/update validation requires `gitUrl` and rejects path-only input. Existing `remote` naming is replaced by `gitUrl` everywhere public because it identifies the remote Git source, not a local remote alias.

Rationale: a Repository is the configured source of truth for workflow checkout. `gitUrl` plus `baseBranch` is sufficient for the runner to prepare a workspace and avoids treating local paths and remote URLs as interchangeable.

Alternatives considered:

- Rename `remote` to `gitUrl` only at the API edge while keeping internal `Remote`. Rejected because duplicate terminology increases mapping bugs and would continue exposing old semantics in dispatch and persistence.
- Support either `path` or `gitUrl` during transition. Rejected because path-only repositories are a non-goal and would keep workflow execution tied to user checkouts.
- Resolve `gitUrl` from an existing local checkout. Rejected because project creation must not inspect local Git state.

### Decision 3: Runner Owns Cache And Workspace State

Runner workspace preparation will use `repository.gitUrl` and `repository.baseBranch` to clone or fetch a repository cache under `MOHIST_RUNNER_ROOT`. It will then materialize a separate workflow workspace directory for the active run or issue and checkout the configured base branch content there. The cache path is internal and never becomes the action cwd.

Rationale: this follows the GitHub Actions/Azure Pipelines model: repository identity is remote/ref metadata, while local checkout locations are runner-managed runtime state. Separating cache and workspace lets Mohist reuse fetch state without giving actions access to cache internals.

Alternatives considered:

- Continue using `git worktree add` from the user's main checkout. Rejected because it depends on project path and allows work to escape into the main checkout.
- Clone directly into every workspace with no cache. Acceptable but slower; keeping a runner-owned cache improves performance while preserving isolation if the cache is never used as cwd.
- Use repository cache as the workspace. Rejected because actions could mutate shared cache state and contaminate later runs.

### Decision 4: Persist Workspace Identity On WorkflowRun

WorkflowRun state will retain the runner-created workspace identity needed to resume work, serve review data, and clean up runtime state. Project and Repository persisted models remain free of local execution paths.

Rationale: workspace path is a runtime fact scoped to a run. Persisting it on the run preserves idempotent start/resume behavior without reintroducing project/repository path configuration.

Alternatives considered:

- Recompute workspace paths from project path and issue number. Rejected because project path is removed and `mo/issue-N` worktree assumptions are no longer valid.
- Keep workspace paths only in runner memory. Rejected because workflow resume, inspection APIs, and cleanup need durable identity across server/runner restarts.
- Persist workspace identity on Repository. Rejected because multiple runs can use the same repository and must have isolated workspaces.

### Decision 5: Dispatch Exposes Workspace Path Only

Workflow dispatch variables will include repository metadata such as `repository.gitUrl` and `repository.baseBranch`, plus `workspace.path`. They will not include `project.path`, `project.effectivePath`, `repository.path`, `repository.remote`, `repository.resolvedPath`, repository cache paths, or user checkout paths.

Rationale: the dispatch contract is the main enforcement point available to workflow templates and actions. If external paths are not present, templates cannot select them as work directories.

Alternatives considered:

- Include cache path as read-only metadata. Rejected because any exposed local path is likely to become an accidental cwd or file source.
- Keep old path variables for prompt compatibility. Rejected because no backwards compatibility is required and stale prompt variables would undermine the boundary.
- Rely only on action implementation discipline. Rejected because templates and prompts need a constrained data model, not just implementation conventions.

### Decision 6: Enforce Cwd Isolation In Action Execution

All runner actions will receive `context.workDir` set to the workflow workspace path. ACP sessions, named session reuse, script/check actions, OpenSpec sync/archive, merge, rebase, repair, and conflict resolver code must run with that cwd and must not override cwd to project paths, repository cache paths, or external checkouts. `mohist/merge` performs the authoritative squash merge inside the workspace.

Rationale: removing path fields is necessary but not sufficient. The runner/action layer must enforce the workspace boundary at every process and Git command entry point.

Alternatives considered:

- Audit only `mohist/merge`. Rejected because the same escape risk exists in checks, scripts, OpenSpec actions, ACP sessions, and repair flows.
- Allow actions to configure custom cwd under trusted templates. Rejected because the acceptance criteria explicitly disallow opt-out from workspace isolation.
- Use process-level chroot/container isolation. Deferred as beyond the current issue; this design enforces Mohist's cwd/data boundary but does not add OS sandboxing.

### Decision 7: Rename Worktree Surfaces To Workspace Semantics

User-facing API and Web UI language will use workspace terminology for execution status, cleanup, review data, changed files, file content, and unavailable data states. Review APIs operate from the workflow workspace and compute issue-level diff/commits using merge-base semantics rather than assuming `mo/issue-N` worktrees under a project checkout.

Rationale: terminology should match the product model. Leaving worktree language would imply implementation details and user checkout coupling that this change removes.

Alternatives considered:

- Keep existing endpoint names and only change response bodies. Partially acceptable if route compatibility is needed, but user-facing labels and response fields should still move to workspace semantics.
- Keep `worktree` internally as a class name. Rejected where practical because stale names make future regressions more likely; if a large rename is staged, public contracts and behavior should change first.
- Preserve `mo/issue-N` branch assumptions for review data. Rejected because workflow workspace identity is now run-managed, not derived from a project worktree convention.

## Risks / Trade-offs

- `[Breaking existing local projects] -> No compatibility migration is planned; document that path-only development data must be recreated with Git URL repositories.`
- `[Private repository clone failures] -> Return clear validation or workspace preparation errors; credential management remains a non-goal for this issue.`
- `[Cache/workspace contamination] -> Never use cache as cwd, create separate workspace directories, and test that actions mutate only workspace files.`
- `[Hidden path references in prompts or actions] -> Remove path variables from dispatch, update built-in prompts/actions, and add tests that serialized variables omit old fields.`
- `[Partial terminology rename causing UI/API confusion] -> Prioritize public request/response fields and visible Web labels; leave internal type renames only where needed for correctness.`
- `[Merge conflict resolver escapes workspace] -> Pass the same workspace cwd through merge/rebase/repair invocation chains and cover conflict scenarios with tests.`
- `[Workspace cleanup deletes wrong data] -> Restrict cleanup to runner-managed workspace roots under `MOHIST_RUNNER_ROOT` and avoid `git worktree remove`, `git branch -d`, or project checkout operations.`
- `[Review data unavailable after cleanup] -> Return availability-aware payloads with structured reasons such as `workspace_removed`, `not_started`, `branch_missing`, or `git_error`.`
- `[Runtime persistence drift] -> Persist workspace identity on WorkflowRun and keep repository configuration immutable with respect to cleanup.`

## Migration Plan

1. Update backend domain and persistence models: remove Project path/effectivePath/baseBranch checkout concepts and replace Repository path/remote/resolvedPath with gitUrl/baseBranch metadata.
2. Update API DTOs and validation: project create/update no longer accepts path; repository create/update requires gitUrl and rejects path-only bodies; responses omit removed fields.
3. Update ProjectService/repository service behavior: project creation no longer creates a default repository from a path and no longer runs Git commands to infer base branch.
4. Update workflow start and dispatch: select explicit default repository metadata, prepare or reuse a WorkflowRun workspace, persist workspace identity on the run, and serialize only repository metadata plus workspace.path.
5. Replace worktree manager behavior with runner workspace management: clone/fetch repository cache from gitUrl, materialize isolated workspaces, list/prune runner-managed workspaces, and remove git worktree commands.
6. Update runner actions: ensure every process and Git command runs with `context.workDir`, especially ACP sessions, named session reuse, scripts/checks, OpenSpec sync/archive, merge, rebase, repair, and conflict resolver flows.
7. Update review/status/file/cleanup APIs and Web UI: use workspace terminology, remove Local Path fields, show Git URL/base branch, and compute review data from the workflow workspace.
8. Update tests across backend, runner, and Web UI for the acceptance criteria, including negative assertions that old path fields and git worktree commands are not used.

Rollback strategy is limited because this is a deliberate breaking model change. Before release, rollback is a normal code revert. After release, rollback would require restoring old path fields and old worktree behavior, which would also reintroduce the issue's execution-boundary bug. The safer operational fallback is to recreate development projects with explicit Git URL repositories and rerun affected workflows in fresh runner workspaces.

## Open Questions

- Should workflow workspace identity be keyed by workflow run id, issue number, or both for directory naming and list/prune display?
- Should repository cache layout be one cache per normalized Git URL, per project repository id, or per project/repository pair to avoid cross-project cache sharing surprises?
- What exact route names should replace or supplement existing worktree-oriented status/cleanup APIs if route compatibility is not required?
- Should workspace preparation use full clone, shallow clone, or fetch into cache plus checkout copy by default?
- How should the UI guide users when `gitUrl` points to a private repository and clone fails due to missing credentials?
- Should cleanup be automatic after successful Integrate, user-triggered, or profile-configurable in the first implementation?
