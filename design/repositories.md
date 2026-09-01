# Repository Execution

A Repository is a named execution resource declared by a Project. An Issue
binds one target Repository, and a WorkflowRun captures that Repository's
values when it starts. Workspace materialization is not a second authority for
Repository identity.

## Design Drivers

- A Project is a product boundary and may contain several isolated
  Repositories.
- The Repository resource must remain the write authority for its Git URL and
  base branch.
- An unfinished Issue needs stable source-control properties for its entire
  WorkflowRun.
- Repository selection and Workspace location answer different questions and
  must not be merged.
- A temporary remote check can protect a checkout without creating another
  persisted identity or lock key.

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

- The Project Repository is the only write authority for `GitUrl` and
  `BaseBranch`.
- `Issue.RepositoryName` references a Project Repository. It is immutable after
  the Issue's first start.
- WorkflowRun stores the bound Repository snapshot: name, Git URL, and base
  branch. It may store one write-once Pull Request identity for that
  Repository.
- A Workspace stores Repository references as access grants, not Repository
  definitions. Its identity, materialization, affinity, and loss behavior are
  defined in [`workspaces.md`](workspaces.md).
- A normalized Git remote is a temporary validation value. Mohist does not
  persist `RemoteFingerprint` or `RemoteIdentityVersion`.

## Semantics

### Resource Changes

A Project may add Repositories, change its default, edit a Repository, or
delete one when no unfinished Issue uses it.

```text diagram
 +--------------+             +-------------+
 | Project Repo |             | Repo change |
 +-------+------+             +------+------+
         |                           |
         v                           v
 +---------------+         +-------------------+
 | Issue binding |         | Issue unfinished? |
 +-------+-------+         +---------+---------+
         |                     +-----+------+
         v                     vyes         vno
 +--------------+         +--------+   +--------+
 | Run snapshot |         | Reject |   | Commit |
 +-------+------+         +--------+   +--------+
         |
         v
 +---------------+
 | Task dispatch |
 +---------------+
```

- Backlog and `in_progress` Issues occupy their target Repository. Editing its
  Git URL or base branch, or deleting it, is rejected.
- Done and cancelled Issues retain their historical Repository name but do not
  block editing or deletion.
- Changing the Project default does not rewrite existing Issue bindings.
- The default Repository cannot be deleted. Select another default first.
- Two Repository names in one Project must not point to equivalent Git remotes.
  The alias check may normalize URLs during the write, but it must not persist
  a hash. A Repository name identifies exactly one remote, so an integration
  lock cannot split one physical Repository into two locks.

Repository update and Issue create, reassign, reopen, and remove use one
Project-scoped coordination boundary. It serializes the check of unfinished
Issue bindings with the commit or rejection. Issue start needs no new
coordination protocol because a backlog Issue already occupies its Repository.
Integration locks use `(ProjectId, RepositoryName)`.

### Issue Binding and Dispatch

An Issue selects a Repository at creation. Without an explicit repository it
uses the current Project default. Before first start, the Issue may be
reassigned. After first start, reassignment is rejected.

Every task dispatch uses the WorkflowRun snapshot. It does not resolve the
Project Repository again and has no fallback to the Project default, `main`, or
legacy variables. Source properties therefore remain stable for the run.

The first `github.pr.number` carrier through the Workflow grain records the
run's Pull Request identity. A conflicting number is rejected. A missing bound
Repository context fails the task with an actionable Repository error.

### Workspace Separation

Repository resolution answers which source material a dispatch may use.
Workspace resolution answers where it executes. An Issue holds both
`RepositoryName` and `WorkspaceName`; WorkflowRun carries the Repository
snapshot while dispatch passes Workspace independently.

The materialized checkout must belong to the bound Git URL. If the Runner
cannot confirm the remote, preparation fails before fetch, push, or rebase.
This check does not make the remote URL, checkout path, or branch part of
Workspace identity.

Workspace operations use `(ProjectId, WorkspaceName)`. A WorkflowRun ID may
locate workflow history but never identifies a Workspace. Workspace lifecycle
and reclamation come from the Workspace view, not Repository existence or
WorkflowRun status. See
[`workspaces.md#runner-side-directory-reclamation`](
workspaces.md#runner-side-directory-reclamation).

## Failure Semantics

- An edit or deletion blocked by an unfinished Issue identifies that Issue.
- A task without required bound Repository context fails with an actionable
  Repository error.
- Preparation fails before source-control mutation when the checkout remote
  cannot be confirmed against the bound Git URL.

## Status

Repository occupancy locking is implemented. Git URL and base branch updates
and deletion query blockers among unfinished Issues. Issue dispatch passes the
named `issue-N` Workspace, and the Runner materializes it through the first-class
Workspace path.

Legacy `WorkflowRepositoryContext` and `WorkspaceIdentity` copies remain in
WorkflowRun. Some Workspace query wires still carry Project, Issue, Repository,
path, and branch, and the Runner retains a per-WorkflowRun Workspace manager
and registry for dispatches without a named Workspace. The `workspaces.json`
fallback is also a retired implementation path. These paths do not define
Workspace identity. Callers are converging on `(ProjectId, WorkspaceName)`.
