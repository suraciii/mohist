---
status: wip
---

# Repository Execution

A Repository is a named execution resource owned by a Project Space. An Issue
stores only the target Repository resource name. A WorkflowRun does not copy
the Repository, and a Runner Workspace does not become a second source of truth
for Repository identity.

## Model

```text
Project.Repository
  Name
  GitUrl
  BaseBranch
  IsDefault

Issue
  ProjectId
  RepositoryName
  WorkflowRunId?

WorkflowRun
  Id
  ProjectId
  IssueNumber
```

- Project Repository is the only write authority for `GitUrl` and
  `BaseBranch`.
- An Issue's `RepositoryName` is a stable reference to a Project Repository.
  The Issue cannot be rebound after its first start.
- A WorkflowRun stores only the scalars needed to locate its Issue. It stores
  no Repository snapshot, Workspace path, or branch.
- A Workspace is a first-class execution-environment resource under a Project,
  with its own identity, origin, and lifecycle. Its Repository references are
  access grants, not copies of Repository definitions. See
  [`workspace.md`](workspace.md).
- A normalized Git remote is a temporary validation value, not a domain field.
  The system does not persist `RemoteFingerprint` or
  `RemoteIdentityVersion`.

## Semantics

### Repository Changes

A Project may add a Repository, change the default, and modify `GitUrl` or
`BaseBranch` or delete a Repository when no unfinished Issue uses it.

Both backlog and `in_progress` Issues occupy their target Repository:

- changing that Repository's `GitUrl` or `BaseBranch` is rejected;
- deleting that Repository is rejected;
- changing the Project default is unaffected because the Issue stores an
  explicit `RepositoryName`;
- done and cancelled Issues retain only the historical resource name and do not
  block modification or deletion.

Repository update and Issue create, reassign, reopen, and remove must pass
through the same Project-scoped coordination boundary. It queries blockers
among unfinished Issues before committing a Project change. The existing
`IssueRepositoryCoordinator` already serializes these binding changes; adding
metadata update to the same boundary is sufficient. Issue start needs no new
coordination protocol because a backlog Issue is already a blocker.

Two Repository names in one Project may not point to equivalent Git remotes.
The alias check may normalize URLs temporarily during a write, but it does not
persist a hash. Integration locks remain keyed by
`(ProjectId, RepositoryName)`. Because a resource name identifies exactly one
remote, the lock cannot split one physical repository into two locks.

### Dispatch

Each task dispatch constructs its Runtime context in this order:

```text
WorkflowRun.(ProjectId, IssueNumber)
  -> Issue.RepositoryName
  -> Project.Repository
  -> repository.{name, gitUrl, baseBranch}
```

The resolved value belongs only to that dispatch. It is not a Run Variable and
is not written back to WorkflowRun. There is no fallback to the Project
default, `main`, or legacy variables. If the target resource is missing, the
task fails with an actionable Repository error; it may be retried after the
Project Repository is repaired.

An unfinished Issue locks the Repository's execution properties, so every
dispatch in one WorkflowRun sees stable `GitUrl` and `BaseBranch` values without
a snapshot.

### Workspace

The system derives an Issue-backed Workspace directly from its generated
`WorkflowRunId`:

```text
path   = <runnerRoot>/workspaces/<workflowRunId>
branch = mohist/run-<workflowRunId>
marker = { "workflowRunId": "<workflowRunId>" }
```

The Runner accepts only a `WorkflowRunId` that matches the system ID syntax,
then verifies that the target path is under `runnerRoot` and is not a symbolic
link. Repository names, Issue titles, and other user input never enter the path
or branch.

When preparing or reusing a Workspace, the Runner verifies:

1. both path and branch are derived from the dispatch `WorkflowRunId`;
2. the marker `WorkflowRunId` matches the dispatch;
3. the current checkout is on the expected branch;
4. `git remote get-url origin` equals this dispatch's `repository.gitUrl`.

Git URLs require only trimmed exact equality. The Workspace was cloned from
that value, and the value cannot change while the Issue is unfinished, so
Server and Runner do not need another URL-equivalence algorithm. A user who
manually changes the Workspace `origin` has corrupted it; the system fails
explicitly rather than guessing whether two spellings refer to the same
repository.

The Workspace marker does not store Project, Issue, Repository, base branch,
run branch, remote hash, or algorithm version. Each is either readable from its
authority or derivable from `WorkflowRunId`.

When first creating a Workspace, or rebuilding one after it is lost, the Runner
first checks for the remote run branch:

```text
origin/mohist/run-<workflowRunId> exists -> check out the remote branch
otherwise                                 -> create from Repository.BaseBranch
```

The pushed run branch is therefore the Workspace rebuild source. Unpushed local
commits are not durable state. If the Workspace is corrupted or the Runner root
is lost, the Workflow executes the corresponding task again.

### Workspace Queries and Cleanup

Diffs, commits, file reads, rebase, and manual cleanup are addressed by
`WorkflowRunId`. The Server uses ProjectId to select a Runner, but ProjectId is
not part of Workspace identity. The Runner derives path and run branch itself;
operations that need a base branch use the Project Repository resolved for the
dispatch.

The Runner registry stores only lifecycle facts that cleanup cannot derive
elsewhere:

```text
WorkspaceRegistryEntry
  WorkflowRunId
  Phase: active | eligible | stuck
  MaterializedAt
  TerminalAt?
```

Cleanup deletes only a directory derived from `WorkflowRunId`, located under
the Runner root, and carrying a matching marker. Cleanup does not require the
Repository to still exist and does not validate the remote; Repository content
does not decide whether the directory is safe to delete.

## Failure Semantics

| Failure | Result |
|---|---|
| change Git URL / base branch while an unfinished Issue uses the Repository | reject the Project update and identify the blocking Issue |
| dispatch cannot resolve the Issue's Repository | fail the task; retry after repairing the Project |
| Workspace marker missing or run ID differs | `workspace_corrupt`; do not modify the directory |
| Workspace branch differs | `workspace_branch_mismatch`; do not switch an unknown Workspace automatically |
| Workspace origin differs from the Project Repository | `workspace_repository_mismatch`; do not fetch, push, or rebase |
| cleanup target is outside the Runner root or marker differs | reject deletion and mark the registry entry stuck |

## Rollout

The project keeps no compatibility layer for the old Workspace protocol. Server
and Runner must be deployed as one version:

1. Stop Server and Runner, then back up the database and Runner root.
2. Clear the Runner Workspace registry.
3. Delete old Workspaces that have no commits to preserve.
4. For an old run with unmerged commits, first confirm that its remote branch
   contains those commits.
5. Start Server and Runner at the same version.
6. Retry the original run and confirm that the Runner rebuilds the Workspace
   from the same-named remote branch and that the new marker contains only
   `workflowRunId`.

Do not add legacy snapshot backfill, old-marker upgrade, or fingerprint
fallback. Rebuild reconstructible state. Preserve required Git work through a
remote branch first.

## Status

The main gaps between the current implementation and the target design are:

- `WorkflowRun` holds `WorkflowRepositoryContext` and `WorkspaceIdentity`;
- `IssueWorkStarted`, the dispatch overlay, and Workspace APIs copy a Repository
  snapshot across multiple layers;
- Workspace query wire types still carry full identity (Project, Issue,
  Repository, path, and branch) instead of addressing by `WorkflowRunId` and
  letting the Runner derive the rest;
- Runner registry entries still store `issueNumber`, `workspacePath`, and
  `runBranch`, all derivable from `WorkflowRunId`; the marker also stores an
  extra `runBranch`.

Implemented: Repository occupancy locking (`GitUrl` / `BaseBranch` updates and
deletion first query blockers among unfinished Issues); Workspace path and run
branch derive directly from `WorkflowRunId`; the marker no longer stores full
identity; fingerprints and algorithm versions are no longer persisted. The
Runner-side `git-remote-identity` module is now dead code and will be deleted as
the remaining gap closes.

Remaining implementation order: first remove the Repository snapshot from
`WorkflowRun` and the multi-layer copies, then address Workspace queries by
`WorkflowRunId`, and finally reduce the registry and marker. The Server/Runner
protocol switch still requires a single-version deployment and cannot be split
into two independent deployments.
