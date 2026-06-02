## Context

Mohist currently stores `RepositoryInfo? Repository` directly on `Issue` and persists that snapshot through `IssueSnapshot`. Issue creation copies `project.GetRepository(req.RepositoryName)` into the issue, and later code treats that copy as authoritative for repository path and base branch.

The current coupling shows up in multiple places:

- `IssueQueryService` returns `issue.Repository` directly in issue read models.
- `IssueGrain.StartWorkAsync` and `BuildVariables` derive workflow project/repository context from `issue.Repository`.
- `WorkspaceRoutes` resolves diff, commits, file-content, worktree status, and cleanup paths from `issue.Repository`.
- `IssueRoutes` archive and rebase paths read `issue.Repository?.Path` and `issue.Repository?.BaseBranch`.

This violates the intended domain boundary. Repository configuration belongs to the project and can change over time. Once Mohist copies path, remote, base branch, and `isDefault` onto an issue, old issues can continue to expose stale repository metadata and workflow actions can keep targeting the wrong repository context.

This change must preserve multi-repository projects, maintain API compatibility where practical, and keep existing stored issues readable even if they still contain embedded repository snapshots.

## Goals / Non-Goals

**Goals:**
- Make project repository configuration the only authority for repository identity, path, remote, base branch, and default marker.
- Store only a stable repository reference on new issues.
- Resolve repository details from the current project configuration for issue read models, workflow startup/variables, workspace APIs, rebase, merge-ready, and integrate.
- Prevent stale issue snapshots from overriding changed project repository configuration.
- Surface missing or ambiguous repository references as actionable configuration problems.
- Preserve compatibility for existing issue rows that still contain embedded repository snapshots.

**Non-Goals:**
- Removing multi-repository support.
- Changing branch naming or worktree layout.
- Making already-materialized workflow definitions live-update beyond resolving repository context at runtime/read boundaries.
- Redesigning general project identity or repository naming beyond issue-to-project repository reference semantics.

## Decisions

### 1. Add an issue-level repository reference and stop treating `RepositoryInfo` as issue-owned state

New issue state will store a stable repository reference, expected to be repository name for now because project repositories are currently keyed by `Name` and the API already accepts `repositoryName`. The issue domain and snapshot format will stop using embedded `RepositoryInfo` as authoritative configuration.

Rationale:
- This is the smallest change aligned with current project structure.
- It matches current external API shape and avoids inventing a new repository id system inside this issue.
- It cleanly separates issue identity from mutable repository configuration.

Alternatives considered:
- Add a new opaque repository id first. Rejected for now because current project repositories do not expose a separate stable id in the server model, so this would expand scope beyond issue #46.
- Keep `RepositoryInfo` on the issue but mark some fields informational. Rejected because it preserves dual authority and makes stale-field regressions likely.

### 2. Introduce one shared repository resolution path for all issue repository lookups

Add a small server-side resolver component that takes `(ProjectInfo project, Issue issue-or-read-model)` and returns either:

- a resolved repository context containing repository identity, path, remote, base branch, and default metadata, or
- a structured repository configuration problem describing missing or ambiguous resolution.

All repository-dependent code paths should use this resolver instead of reading `issue.Repository` directly.

Expected consumers:
- issue read-model assembly in `IssueQueryService`
- workflow start and variable construction in `IssueGrain`
- workspace/review routes in `WorkspaceRoutes`
- rebase/archive/integrate paths in route or workflow services

Rationale:
- This removes copy-pasted fallback logic like `repo?.BaseBranch ?? "main"`.
- It guarantees one interpretation of missing references and one source of runtime repository facts.
- It creates a narrow seam for compatibility logic for old issues.

Alternatives considered:
- Resolve repositories ad hoc in each route/service. Rejected because the bug exists precisely due to distributed fallback behavior.
- Put all logic into `ProjectInfo.GetRepository`. Rejected because issue #46 also needs compatibility interpretation for legacy issue snapshots and error reporting, which is broader than a simple name lookup.

### 3. Keep legacy snapshots readable by interpreting them as references, not authority

Existing stored issues may only have embedded `RepositoryInfo`. During deserialization or repository resolution, Mohist should derive the repository reference from legacy data, preferring the embedded repository name as the reference key. The embedded path, remote, base branch, and `isDefault` fields must not override current project configuration once a project repository match is found.

Behavior:
- If a legacy issue has `Repository.Name = "main"`, resolve against the current project repository named `main` and ignore stale snapshot metadata.
- If no repository reference exists but the legacy snapshot was effectively using the default repository, resolve to the current project default repository.
- If the legacy reference no longer matches any configured repository, surface a repository configuration problem rather than silently using the stale snapshot or defaulting to `main`.

Rationale:
- This avoids a mandatory database migration before the feature is safe.
- It immediately fixes stale path/baseBranch/default drift for old issues.

Alternatives considered:
- Hard migration of all issue rows on deploy. Rejected as the primary mechanism because persisted issue state is spread through Orleans/SQLite state rows and compatibility is still needed during rollout and rollback.
- Continue using legacy snapshot fields when resolution fails. Rejected because it would preserve the untrustworthy behavior this issue is fixing.

### 4. Expose resolved repository details in read models, plus configuration error state

Issue APIs should continue returning repository details, but those details should be built from resolved project configuration. The read model should also carry a repository configuration problem when resolution fails so the issue page can explain why workspace/review/workflow actions are blocked.

The design expects an additive field such as `repositoryProblem` or equivalent issue attention/error payload rather than removing `repository` from responses.

Rationale:
- The UI still needs resolved repository facts for display.
- The acceptance criteria require visible, actionable configuration errors.
- An additive error field preserves backward compatibility better than replacing `repository` with null and a generic 500.

Alternatives considered:
- Only fail repository-dependent endpoints and keep issue reads silent. Rejected because the issue page itself must be trustworthy and show configuration problems.
- Return raw resolver exceptions. Rejected because the UI needs stable, product-level error semantics.

### 5. Make workflow runtime variables consume resolved repository context

`IssueGrain.StartWorkAsync` should load the current project configuration, resolve the issue repository reference, and pass that resolved context into `WorkflowProjectContext` and workflow variables. The `repository` variable should contain resolved runtime facts such as repository name, path, remote, and base branch, and should ideally include the repository reference identity explicitly.

If resolution fails, workflow start should fail with a repository configuration problem before worktree creation or task dispatch proceeds.

Rationale:
- Workflow start is a critical trust boundary: once the wrong repo/base branch enters variables, downstream tasks inherit the bug.
- This matches the specs that say runtime variables are resolved context, not issue-owned config.

Alternatives considered:
- Resolve once at issue creation and copy the result into workflow variables forever. Rejected because project repository configuration can change after issue creation.
- Re-resolve inside every workflow task definition. Rejected because start-time runtime context is already the place where repository variables are assembled.

### 6. Normalize repository-dependent HTTP/workspace operations onto the same resolved context

Routes for diff, commits, commit diff, file-content, worktree status, cleanup, archive, rebase, merge-ready, and integrate should resolve repository context from the project and issue reference before computing repo path/base branch.

Expected behavior:
- review/workspace APIs use resolved current repo path and base branch on every request
- rebase uses the resolved base branch when the request does not override it
- cleanup/archive remove the worktree from the resolved current repository path
- integrate/merge-ready use the same resolution path so branch targeting remains consistent

Rationale:
- This is necessary to eliminate stale base branch and path usage across product surfaces.
- It keeps read APIs and workflow actions consistent with each other.

Alternatives considered:
- Fix only workflow start and rebase. Rejected because stale repository data is also user-visible in read APIs and cleanup/archive behavior.

## Risks / Trade-offs

- [Repository name is the reference key today, not an immutable id] -> Mitigation: keep the resolver abstraction narrow so Mohist can switch to a dedicated repository id later without rewriting every call site.
- [Existing issues with missing legacy names may not resolve cleanly] -> Mitigation: define explicit compatibility rules and surface a repository configuration problem instead of guessing.
- [More endpoints may now return configuration errors where they previously limped along] -> Mitigation: use stable error codes/messages and expose the same problem on the issue read model so users know how to repair project config.
- [Route handlers currently assume `issue.Repository` is always available] -> Mitigation: centralize resolution and refactor handlers to consume a resolved context object rather than inline tuple fallbacks.
- [Project config can change while a workflow is already running] -> Mitigation: scope this issue to runtime/read boundaries. Start-time variables and per-request read operations re-resolve; already-materialized task definitions are not retroactively rewritten.

## Migration Plan

1. Update issue domain/storage shape to add repository reference storage and compatibility reading for legacy snapshots.
2. Add a repository resolver abstraction plus a structured repository configuration problem model.
3. Refactor issue creation to persist only the selected/default repository reference.
4. Refactor `IssueQueryService` to resolve repository details for read models and attach repository problems.
5. Refactor `IssueGrain.StartWorkAsync` and workflow variable construction to require resolved repository context.
6. Refactor workspace/review/rebase/archive/integrate call sites to use the shared resolver.
7. Add tests covering project repository changes after issue creation, legacy embedded snapshots, missing references, and ambiguous resolution.

Deployment strategy:
- Deploy compatibility-first code that can read both old snapshot-based issues and new reference-based issues.
- Optionally add a background or on-read rewrite later to persist the new format, but correctness must not depend on that migration.

Rollback strategy:
- Safe rollback requires retaining legacy snapshot fields or deserialization compatibility during the rollout window.
- Because new issues will stop relying on embedded repository configuration, rollback should preserve read compatibility rather than reintroduce snapshot authority.

## Open Questions

- Should the public API continue using `repositoryName`, or should this issue also introduce a more explicit `repositoryId`/`repositoryRef` field while keeping `repositoryName` as compatibility input?
- Where should repository configuration problems live in the read model: a dedicated `repositoryProblem` field, the existing attention model, or both?
- Do we want repository-dependent endpoints to return `409` configuration errors consistently, or should some read endpoints continue returning `200` with `available: false` plus a repository-problem reason for UI compatibility?
- Is there any current integrate or merge-ready path outside the reviewed routes/services that still reads issue-owned repository data and needs to be included explicitly in the refactor checklist?
