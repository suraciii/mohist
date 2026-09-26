# Workspace

A Workspace is a named execution environment under a Project. It has a durable
identity, an Origin, Repository references, and a Runner-local Workspace Home.
Its lifecycle is independent of AgentSessions and WorkflowRuns.

The Server owns the logical Workspace. A Runner owns its Workspace Home and is
the only component that touches its filesystem. Directory contents and Git
layout are execution details, not Workspace fields. A Workspace gives
multi-repository work a home for plans, research, and other products that do
not belong to one Repository.

## Design Drivers

- Workspace identity must survive Runner loss while its directory may not.
- Project isolation requires one active Workspace for each Origin within a
  Project.
- Repository access must stay separate from checkout state and Git layout.
- Workflow execution needs a clean, repeatable layout. Interactive work needs
  a reusable directory without platform-imposed checkout rules.
- Provisioning must produce a usable Workspace Home after Runner loss. Git
  state comes from the remote branch. Declared non-repository artifacts come
  from the WorkflowRun's bound artifacts.

## Model

```text literal
Project.Workspace
  Name                 # Unique within the Project; derived from Origin by default
  Origin               # Source binding; see below
  RepositoryNames[]    # Project Repository references and access grants
  Status               # active | archived
  Home                 # Workspace Home route: runnerId + path; empty before provisioning
```

`Origin` is the source and unique resolution key:

```text literal
Origin = { kind: issue, issueNumber }
       | { kind: slack, teamId, channelId }
       | { kind: web,   conversationId }
       | { kind: cli }
       | { kind: manual }
```

- At most one active Workspace in a Project may have a given Origin.
- An Issue holds `WorkspaceName` for Workflow resolution. A Workspace does
  not duplicate Issue state.
- An AgentSession holds `WorkspaceName`. A Workspace does not hold a Session
  list; bound Sessions are a query over Sessions.
- `RepositoryNames` grant access and name default checkout targets. They do
  not prove that a checkout exists.
- Workspace creation and archival emit
  `com.mohist.workspace.created` and `com.mohist.workspace.archived`. Event
  lineage is defined in [`event-protocol.md`](../../platform/events/spec.md).

## Semantics

### Creation and Resolution

A Workspace is provisioned when its Origin first needs execution. There is no
separate global creation flow.

```text diagram
                   +--------+
                   | Origin |
                   +----+---+
                        |
                        v
              +------------------+
              | Active Workspace |
              +---------+--------+
            +-----------+-----------+
            v                       vIssue done or close
     +-------------+          +----------+
     | Runner Home |          | Archived |
     +------+------+          +-----+----+
            |                       |
            vlost or unavailable    v
 +---------------------+   +-----------------+
 | Provision Home      |   | No new bindings |
 | from durable inputs |   |                 |
 +---------------------+   +-----------------+
```

- Starting an Issue first creates `Origin = { issue, n }` and normally derives
  the Name `issue-<n>`. Retries and reruns reuse that Workspace.
- A Slack channel, Web conversation, or other ingress creates its corresponding
  Origin on the first trigger. The Name is derived from the context and is
  unique within the Project.
- `mo workspace create <name>` creates `Origin = { manual }`. The supplied
  Name must be unique within the Project.
- Creation establishes only the Workspace entity and Repository references. The
  Runner provisions its Home on first dispatch.
- A root Session resolves its ingress context to an active Workspace. For
  Slack, each Project resolves the channel independently, so Agents from
  different Projects use separate Workspaces.
- An invited Agent joins the enclosing Session or ingress Workspace.
- A child Session inherits its parent Workspace. Spawn cannot select another
  Workspace.
- `mo agent launch <agent> --workspace <name>` binds to an existing Workspace.
  Without the option, the command uses the current Project's default CLI
  Workspace, provisioning one when necessary, and returns the actual Name.

### Binding and Resolution

- Workflow task dispatch resolves the `WorkspaceName` held by the Issue.
- A delegated child cannot override its parent's Workspace.
- Workspace operations use `(ProjectId, WorkspaceName)`. A WorkflowRun ID may
  locate history, but never identifies a Workspace.
- The Server resolves `Home` to a Runner. Repository data is supplied only to
  operations that need source control context.

### Repository Membership

- `mo workspace repo add/remove <name> <repo>` changes `RepositoryNames`.
  Reject the change while an active Session is bound and tell the user to stop
  those Sessions or wait.
- A Workflow Workspace starts with the Issue's Repository. A composite Issue
  may attach more Repositories with `repo add`; attachment timing remains open
  under Status.
- During Workspace Home provisioning, the Runner injects credentials for
  declared Repositories through the same channel used by Workflow preparation.
  Agents do not need to know credential details.

### Scheduling Affinity and Provisioning

- After provisioning on Runner R, later dispatches for the Workspace route to
  R.
- If R is unreachable or its Home is unavailable, an available Runner
  provisions a new Home and replaces `Home`.
- Provisioning uses the remote Git branch for Repository contents and the
  current WorkflowRun's bound artifacts for declared non-repository files.
- Unpushed Git state and unuploaded files are not durable inputs.

### Layout

Workflow preparation owns this fixed root layout. Provisioning ensures the
Workspace Home and the target Repository checkout under `REPOS/`. A valid
existing checkout is preserved; a missing or unavailable checkout is created
from the required remote branch.

```text literal
issue-<number>/
├── .mohist/                  # Platform marker and identity files
├── REPOS/<repository-name>/  # Repository checkout; only this tree enters Git
├── PLANS/                    # Plans, designs, review reports, and task list
├── RESEARCH/                 # Research notes and exploration material
└── .scratch/                 # Temporary files
```

Only `REPOS/` participates in Git. `PLANS/`, `RESEARCH/`, and `.scratch/` are
Workspace-local material. Plans and reviews under `PLANS/` never enter a
commit, branch, or Pull Request. Their durable record is the uploaded run
artifact described in
[`workflow/plan-artifacts.md`](../../workflow/execution/design.md). Separate Issue
Workspaces never share checkouts or dependency caches.

Each Workflow dispatch has two directory boundaries:

- The execution directory is the Workspace root unless the Task selects a
  Workspace-relative `working-directory`.
- The Repository guard directory is always
  `REPOS/<repository-name>` for the target Repository. Branch checks, dirty
  worktree checks, residual Git checks, and cleanup use this directory.

An interactive Workspace starts empty except for Repository access. The Agent
organizes its own checkout layout.

### Archival

Archival is the only terminal Workspace operation:

- An Issue Workflow Workspace archives when the Issue becomes done or
  cancelled.
- `mo workspace close <name>` archives an interactive or manual Workspace.
  An Issue Workspace can end only through `mo issue done` or `mo issue close`.
- Loss of an ingress, such as Slack channel archival, also archives its
  Workspace. Archival releases the Origin, so the next trigger provisions a
  new one.
- Reject close while active Sessions remain bound and direct the user to stop
  them or wait.
- After archival, retain the entity for history, reject new bindings, and let
  the Runner reclaim its directory.

### Runner-Side Directory Reclamation

The Runner may reclaim an active Workspace with no active bound Sessions under
its disk policy. It must not reclaim an active Workspace with active bindings.
An archived Workspace may be deleted under the reclamation grant. The logical
Workspace remains after active-directory reclamation so a later binding can
provision a new Home.

Every directory deletion must acquire the directory's Runtime removal fence.

### Prompt Anchoring

For execution bound to a Workspace, the Runner injects a Workspace-root anchor
with the absolute path and the instruction: `all Workspace files are here; do
not search $HOME`. `working-directory` selects the Action execution directory;
it does not redefine the Workspace root or Repository guard. AGENTS.md and
prompt conventions define internal layout, not Workspace schema.

## Status

Workspace identity, explicit create and archive lifecycle, Issue and ingress
resolution, named Runner Home provisioning, cross-Session reuse, home affinity,
and Workspace-aware reclamation guards are implemented for current owners.
AgentJob scheduling can clear an offline Home and provision elsewhere.
WorkflowRun assignment remains pinned to its original Runner, so cross-Runner
Workflow Home provisioning is not implemented. Slack channel archive events do
not yet reach the Server archive boundary.

Issue dispatches resolve the named Issue Workspace. A WorkflowRun ID may locate
execution history and route assignment, but never selects a Workspace directory
or branch. Compound-Issue Repository attachment and Runtime Binding after
Workspace Home provisioning remain open questions.
