# Repositories

A Project is the scope and execution boundary for one product in Mohist. Its
code can span multiple codebases, such as separate server and web codebases. A
Project references them by declaring **Repositories**. A Repository is an
execution resource declared by a Project, and each Issue binds to one target
Repository.

## Mental Model

- **Project = product; Repository = resource.** A Project is not the same as
  one codebase. It declares one or more Repositories as a pipeline declares its
  resources.
- Each Repository has a resource name that is unique within the Project, such
  as `server` or `web`, plus a Git URL and base branch. The resource name is its
  stable management reference and its directory name under a Workspace's
  `REPOS/` directory. It must be a lowercase portable path segment beginning
  with a letter or number and containing only letters, numbers, `_`, or `-`.
  The exact reserved names `con`, `prn`, `aux`, `nul`, `com1` through `com9`,
  and `lpt1` through `lpt9` are invalid.
- Each Project has exactly one **default Repository**. When there is only one,
  it is naturally the default.
- Data remains isolated between Projects. Repository declarations are not
  shared across Projects.

## Managing Repositories

Repositories are members of the Project's collection:

```bash
mo repo list
mo repo create server --git-url /path/to/my-server --base-branch main
mo repo create web    --git-url /path/to/my-web    --base-branch main
mo project repo set-default server
mo repo edit web --base-branch develop
mo repo delete web
```

- A codebase supplied with `--path` when a Project is created is registered as
  that Project's default Repository. A single-Repository Project still starts
  with one command.
- The default Repository cannot be deleted. Select another default first.
- A non-default Repository can be deleted only when no unfinished Issue is
  bound to it. Backlog and in-progress Issues prevent deletion. Done and
  cancelled Issues retain the historical target Repository name but do not
  prevent deletion.
- The Git URL or base branch cannot change while a backlog or in-progress Issue
  uses the Repository. Changing the default does not affect existing Issue
  bindings.

## Issues and Repositories

Each Issue binds to a target Repository when it is created. Use
`mo issue create "Web change" --repo web` to select one explicitly. Without
`--repo`, the Issue binds to the current default Repository. Later default
changes do not rewrite existing Issues. Before its first start, an Issue can be
reassigned with `mo issue edit <number> --repo <resource-name>`. The binding is
permanently locked after the first start. `mo issue list --repo <resource-name>`
filters by the stored binding, and `mo issue view` displays the target
Repository.

The Workflow workspace, branch, diff, rebase, local integration, and GitHub
Pull Request all use the Issue's target Repository. Its Git URL and base branch
remain unchanged while the Issue runs. Runner must be able to access every
Repository declared by the Project.

For execution, Runner clones the target Repository at
`${{ workspace.path }}/REPOS/<repository-name>`. Profiles use the resolved
`${{ repository.path }}` and `${{ repository.branch }}` facts instead of
constructing that path or treating the branch as a Workspace property. Plans,
research, and review artifacts remain outside the checkout at the Workspace
root.

## Runner Constraint

Runner must be able to access **every** Repository declared by a Project.
Before adding a Repository, confirm that its Git URL is available on the Runner
host. A local path or `file://` URL must be visible from that host.

## Single-Repository Promise

A Project with one Repository behaves like the original single-codebase model.
Every Issue uses that Repository automatically, and the user does not need to
understand the concepts in this document. The additional complexity appears
only when a second Repository is added.

## Non-goals

- **Release coordination:** Mohist does not coordinate simultaneous deployment
  of changes from several Repositories.
- **Several target Repositories for one Issue:** An Issue has exactly one target
  Repository and one integration branch. Additional Workspace Repository access
  does not create another PR or integration target.

## Implementation Gap

Repository names are currently validated only as nonempty text, and the Runner
still places the target checkout at the Workspace root. Portable path-segment
validation, the `REPOS/<repository-name>/` checkout, and the runtime
`repository.path` and `repository.branch` facts remain to be implemented.
