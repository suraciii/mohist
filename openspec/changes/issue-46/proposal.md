## Why

Mohist currently copies full repository configuration onto each issue, which lets issue read models and workflow actions drift away from the project's current repository settings. This needs to change now because repository path, remote, base branch, and default selection are project-owned configuration, and stale issue snapshots make issue pages, workspace actions, and integrate behavior unreliable.

## What Changes

- Store only a stable project repository reference on an issue instead of persisting a full mutable repository snapshot as issue-owned authority.
- Resolve an issue's repository details from the current project repository configuration for issue reads, workflow startup, workflow variables, workspace status, diff/file-content reads, rebase, merge-ready checks, and integrate.
- Make issue creation bind to the selected project repository reference, or to the current default project repository reference when no repository is specified.
- Treat missing or ambiguous repository references as actionable project configuration errors instead of silently falling back to stale issue data or implicit branch defaults.
- Interpret or migrate existing issues with embedded repository snapshots so old `isDefault`, `path`, `remote`, and `baseBranch` values no longer override current project configuration.

## Capabilities

### New Capabilities
- `issue-repository-resolution`: Resolves an issue's repository reference against current project repository configuration and surfaces configuration errors when resolution fails.

### Modified Capabilities
- `project-management`: Project repository configuration remains the source of truth for repository identity, path, remote, base branch, and default selection.
- `local-issue-store`: Issue persistence stores repository references rather than issue-owned repository snapshots, including compatibility for older stored issues.
- `http-api`: Issue create/read and workspace-related API surfaces expose repository details from resolved project configuration and report repository configuration problems clearly.
- `workflow-run`: Workflow startup and runtime variables use resolved repository context instead of persisted issue snapshot data.
- `worktree-manager`: Worktree creation, branch targeting, and related repository operations use repository context resolved from the issue's project repository reference.
- `base-drift-awareness`: Rebase and drift decisions use the resolved current project repository/base branch for the issue rather than stale issue-owned repository data.

## Impact

Affected areas include issue storage and migrations, project repository resolution logic, issue read-model assembly, workflow variable construction, workspace and file-content routes, merge-ready and rebase flows, integrate targeting, and UI/API error reporting for repository configuration problems.
