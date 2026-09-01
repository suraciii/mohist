# Repositories

A Project is the scope and execution boundary for one product. Its code may
span several codebases. The Project declares those codebases as Repositories,
and each Issue binds to one target Repository.

## Product Commitments

- A Repository is a Project resource with a stable name, Git URL, and base
  branch.
- Each Project has exactly one default Repository.
- Repository declarations remain isolated between Projects.
- Each Issue records one target Repository. The binding locks after the Issue
  first starts.
- Changing a Project default never changes an existing Issue binding.
- Runner must be able to access every Repository declared by the Project.
- A Project with one Repository keeps the simple single-codebase workflow.

## Mental Model

- **Project means product. Repository means resource.** A Project is not the
  same as one codebase.
- A Repository name is unique within its Project. Use names such as `server` or
  `web` as stable management references.
- A Project with one Repository uses that Repository as its default. A Project
  with several Repositories still has only one default.
- Repository declarations are not shared across Projects.

## Managing Repositories

Repositories are members of the Project collection:

```bash
mo repo list
mo repo create server --git-url /path/to/my-server --base-branch main
mo repo create web    --git-url /path/to/my-web    --base-branch main
mo project repo set-default server
mo repo edit web --base-branch develop
mo repo delete web
```

A codebase supplied with `--path` when a Project is created becomes that
Project's default Repository. A single-Repository Project therefore needs no
extra selection.

The default Repository cannot be deleted. Select another default first. A
non-default Repository can be deleted only when no unfinished Issue is bound to
it. Backlog and In Progress Issues prevent deletion. Done and Cancelled Issues
retain the historical target Repository name but do not prevent deletion.

The Git URL or base branch cannot change while a Backlog or In Progress Issue
uses the Repository. Changing the default affects only future Issue bindings.

## Issues and Repositories

Create an Issue with an explicit target:

```text literal
mo issue create "Web change" --repo web
```

Without `--repo`, the Issue uses the current default Repository. Before its
first start, an Issue can be reassigned:

```text literal
mo issue edit <number> --repo <resource-name>
```

The binding becomes permanent after the first start. `mo issue list --repo
<resource-name>` filters by the stored binding, and `mo issue view` displays the
target Repository.

The Workflow Workspace, branch, diff, rebase, local integration, and GitHub
Pull Request all use the Issue's target Repository. Its Git URL and base branch
remain unchanged while the Issue runs.

## Runner Constraint

Runner must access every Repository declared by the Project. Before adding a
Repository, confirm that its Git URL is available on the Runner host. A local
path or `file://` URL must be visible from that host.

## Single-Repository Promise

A Project with one Repository behaves like the original single-codebase model.
Every Issue uses that Repository automatically. The additional Repository
concepts matter only after a second Repository is added.

## Non-goals

- Mohist does not coordinate simultaneous deployment of changes from several
  Repositories.
- One Issue cannot check out several Repositories at once. Evaluate a
  multi-codebase requirement separately when it becomes real.

## Implementation Gaps

The repository model, default binding, pre-start reassignment, and deletion
safeguards are implemented. No other implementation gap is currently recorded
for this document.
