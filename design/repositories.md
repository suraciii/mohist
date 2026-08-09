---
status: wip
---

# Repository Execution

A Repository is a named execution resource owned by a Project Space. An Issue
stores only the target Repository resource name. A WorkflowRun does not copy
the Repository, and a Runner Workspace does not become a second source of truth
for Repository identity.

## Model

```text literal
Project.Repository
  Name
  GitUrl
  BaseBranch
  IsDefault

Issue
  ProjectId
  RepositoryName
  WorkspaceName

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
through the same Project-scoped coordination boundary. It evaluates unfinished
Issue bindings and commits or rejects the change as one serialized decision.
Issue start needs no new coordination protocol because a backlog Issue already
occupies its Repository.

Two Repository names in one Project may not point to equivalent Git remotes.
The alias check may normalize URLs temporarily during a write, but it does not
persist a hash. Integration locks remain keyed by
`(ProjectId, RepositoryName)`. Because a resource name identifies exactly one
remote, the lock cannot split one physical repository into two locks.

### Dispatch

Each task dispatch constructs its Runtime context in this order:

```text diagram
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

Repository resolution answers which source material a dispatch may use.
Workspace resolution answers where that work executes. Combining the two would
make a WorkflowRun, checkout, or Runner directory a second authority for both
resources.

An Issue therefore holds both stable references: `RepositoryName` selects the
Project Repository and `WorkspaceName` selects the Project Workspace. Dispatch
resolves the Repository live through the chain above and passes the Workspace
name independently. Workspace identity, Origin, materialization, affinity, and
loss behavior are defined only in
[`workspace.md`](workspace.md).

Repository preparation still protects one local invariant: the materialized
checkout must belong to the resolved `GitUrl`. If the Runner cannot confirm the
remote, it fails preparation before fetch, push, or rebase. This validation does
not make the remote URL, checkout path, or branch part of Workspace identity.

### Workspace Queries and Cleanup

Workspace operations are addressed by `(ProjectId, WorkspaceName)`. A
WorkflowRun ID may locate workflow history, but it never identifies a
Workspace. The Server resolves the Workspace Home to a Runner; Repository data
is supplied only to operations that need source control context.

The Workspace lifecycle and reclamation grant come from the Workspace view, not
Repository existence or WorkflowRun status. Runner-side registry and deletion
fences are defined in [`runner.md`](runner.md#local-workspace-lifecycle) and the
reclamation rules remain authoritative in
[`workspace.md`](workspace.md#runner-side-directory-reclamation).

## Failure Semantics

| Failure | Result |
|---|---|
| change Git URL / base branch while an unfinished Issue uses the Repository | reject the Project update and identify the blocking Issue |
| dispatch cannot resolve the Issue's Repository | fail the task; retry after repairing the Project |
| materialized checkout remote cannot be confirmed as the resolved Repository | fail preparation; do not fetch, push, or rebase |

## Status

Repository occupancy locking is implemented: `GitUrl` and `BaseBranch` updates
and deletion first query blockers among unfinished Issues. Issue dispatch also
passes the named `issue-N` Workspace, and the Runner materializes that Workspace
through the first-class Workspace path.

The remaining gaps point in one direction only. `WorkflowRun` still retains
legacy `WorkflowRepositoryContext` and `WorkspaceIdentity` copies, some
Workspace query wires still carry Project, Issue, Repository, path, and branch,
and the Runner retains a per-WorkflowRun Workspace manager and registry as a
fallback when a dispatch lacks the named Workspace fact. These fields and the
`workspaces.json` fallback are retired implementation paths. They do not define
Workspace identity and must be removed as callers converge on
`(ProjectId, WorkspaceName)`.
