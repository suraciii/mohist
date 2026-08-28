# Repository Execution

A Repository is a named execution resource owned by a Project Space. An Issue
stores the target Repository resource name. A WorkflowRun captures the bound
Repository snapshot and may record one write-once Pull Request identity. A Runner
Workspace does not become a second source of truth for Repository identity.

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
  Repository
  PullRequestIdentity
```

- Project Repository is the only write authority for `GitUrl` and
  `BaseBranch`.
- An Issue's `RepositoryName` is a stable reference to a Project Repository.
  The Issue cannot be rebound after its first start.
- A WorkflowRun stores the bound Repository snapshot (name, Git URL, and base
  branch) captured at start. It may also store one write-once Pull Request
  identity for that Repository.
- A Workspace is a first-class execution-environment resource under a Project,
  with its own identity, origin, and lifecycle. Its Repository references are
  access grants, not copies of Repository definitions. See
  [`workspaces.md`](workspaces.md).
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

Each task dispatch uses the WorkflowRun's bound Repository snapshot. The
snapshot is not a Run Variable and is not resolved again from the Project
Repository collection. There is no fallback to the Project default, `main`, or
legacy variables.

An unfinished Issue locks the Repository's execution properties before the run
starts, so every dispatch in that WorkflowRun uses stable `GitUrl` and
`BaseBranch` values. The first `github.pr.number` carrier through the Workflow
grain records the run's write-once Pull Request identity; a conflicting number
is rejected.

### Workspace

Repository resolution answers which source material a dispatch may use.
Workspace resolution answers where that work executes. Combining the two would
make a WorkflowRun, checkout, or Runner directory a second authority for both
resources.

An Issue therefore holds both stable references: `RepositoryName` selects the
Project Repository and `WorkspaceName` selects the Project Workspace. WorkflowRun
carries the bound Repository snapshot, while dispatch passes the Workspace name
independently. Workspace identity, Origin, materialization, affinity, and loss
behavior are defined only in [`workspaces.md`](workspaces.md).

Repository preparation still protects one local invariant: the materialized
checkout must belong to the bound `GitUrl`. If the Runner cannot confirm the
remote, it fails preparation before fetch, push, or rebase. This validation does
not make the remote URL, checkout path, or branch part of Workspace identity.

### Workspace Queries and Cleanup

Workspace operations are addressed by `(ProjectId, WorkspaceName)`. A
WorkflowRun ID may locate workflow history, but it never identifies a
Workspace. The Server resolves the Workspace Home to a Runner; Repository data
is supplied only to operations that need source control context.

The Workspace lifecycle and reclamation grant come from the Workspace view, not
Repository existence or WorkflowRun status. Runner-side registry and deletion
fence rules are authoritative in
[`workspaces.md`](workspaces.md#runner-side-directory-reclamation).

## Failure Semantics

- Changing the Git URL or base branch while an unfinished Issue uses the
  Repository is rejected and identifies the blocking Issue.
- When a run lacks the required bound Repository context, the task fails with
  an actionable Repository error.
- When preparation cannot confirm that the materialized checkout belongs to the
  bound Repository, it fails before fetch, push, or rebase.

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
