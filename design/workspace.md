---
status: wip
---

# Workspace

A Workspace is a first-class named execution-environment resource under a
Project: a persistent directory plus access to a set of repositories. Its
lifecycle is independent of any AgentSession or WorkflowRun.

Boundary: a Workspace holds only identity, origin, Repository references,
status, and materialization routing facts. Directory contents and Git layout
(clone / branch / worktree) belong to Workflow preparation or Agent behavior,
not Workspace entity schema.

A Workspace is the home of the work, while repositories are its materials.
Repository checkouts live under the Workspace (by convention, in `repos/`), and
work products such as plans and research belong at the Workspace level. This
meaning is carried by prompt conventions rather than platform schema. It gives
cross-repository work a place for artifacts that do not belong to any one
repository.

Reference analogy, for explanation only and not as a terminology source:
Runner ~= Node, WorkflowRun ~= Pod, AgentSession ~= Container, and Workspace ~=
local PersistentVolume. Its lifecycle is independent of its consumers, its
materialization location determines scheduling affinity, it is lost with its
node, and it is shared within one consumer group.

## Model

```text literal
Project.Workspace
  Name                 # Unique within the Project; derived from Origin by default
  Origin               # Source binding; see below
  RepositoryNames[]    # References to Project Repository resources (access grants)
  Status               # active | archived
  Home                 # Materialization route: runnerId + path; empty before materialization
```

Origin is both the source from which the Workspace was created and its unique
resolution key:

```text literal
Origin = { kind: issue, issueNumber }
       | { kind: slack, teamId, channelId }
       | { kind: web,   conversationId }
       | { kind: cli }
       | { kind: manual }
```

- At most one active Workspace may have a given Origin within a Project.
- Workspace creation and archival emit `com.mohist.workspace.created` and
  `com.mohist.workspace.archived`. See the Workspace family in
  [`event-protocol.md`](event-protocol.md) for event lineage.
- Reverse resolution for the Workflow path goes through the Issue: the Issue
  holds WorkspaceName, while the Workspace does not duplicate Issue state.
- An AgentSession holds WorkspaceName. A Workspace does not hold a Session
  list; "which Sessions are currently bound" is a query over Sessions.
- RepositoryNames are access grants and default checkout targets, not evidence
  of materialized checkouts. Workflow preparation owns the clean Issue checkout;
  an interactive Agent organizes its own checkout layout.

## Semantics

### Creation (Dynamic Provisioning)

A Workspace is provisioned dynamically when its Origin first needs to execute;
there is no separate global creation flow:

- Workflow path: starting an Issue's first run creates
  `Origin = { issue, n }`, with Name derived as `issue-<n>`. Retry and rerun
  reuse the same Workspace.
- Interactive path: the first trigger from an ingress context, such as a Slack
  channel, creates the corresponding Origin. Its Name is
  derived from the context and made unique within the Project.
- Manual path: `mo workspace create <name>` creates a Workspace explicitly with
  `Origin = { manual }`.
- Generic Web Composer does not use `Origin.Web` implicit conversation
  provisioning. It binds only to an active named Workspace explicitly selected
  by the user. If none is available, the user must explicitly create an
  `Origin = { manual }` Workspace and select it in the Composer.

Creation establishes only the entity and Repository references. The Runner
materializes the directory on first dispatch. A manual Name is supplied by the
user and must be unique within the Project.

### Repository Membership

- `mo workspace repo add/remove <name> <repo>` changes RepositoryNames. Reject
  the change while the Workspace has active bound Sessions, and tell the user
  to stop those Sessions or wait.
- The Workflow path initially contains the Issue's RepositoryName. A compound
  Issue can attach additional repositories with `repo add`; the attachment
  timing remains an open question under Status.
- During materialization, the Runner injects Repository access credentials for
  RepositoryNames through the same channel used by Workflow prepare
  (`GH_TOKEN` / Git credentials; see
  [`github-integration.md`](github-integration.md)). Agent-managed clones do not
  need to know credential details.

### Binding and Resolution

- Workflow task dispatch binds through the WorkspaceName held by the Issue.
- Starting a root Session resolves the ingress context to an Origin and then to
  an active Workspace, provisioning one if none exists. For Slack, the
  Workspace belongs to the triggered Agent's Project. If Agents from different
  Projects use the same channel, each Project owns a separate Workspace.
- An invited Agent joins the Workspace of the enclosing Session or ingress
  context.
- A delegated child Session always inherits its parent Session's Workspace. A
  spawn request cannot select another Workspace.
- Explicit override: `mo agent launch <agent> --workspace <name>` binds a new
  Session to an existing Workspace. Without `--workspace`, it binds to the
  current Project's default CLI Workspace, provisioning one when necessary;
  the launch response returns its actual Name.
- Generic Web Composer accepts only an explicitly selected active named
  Workspace. A launch without that binding is rejected; it never creates a
  hidden Workspace or binds a Session to an identity returned only in the
  response.

### Scheduling Affinity and Rematerialization

- Once a Workspace is materialized on Runner R, all subsequent dispatches bound
  to it route to R.
- If R is unreachable or the directory has been reclaimed, rematerialize the
  Workspace on an available Runner: replace Home and start from an empty
  directory. Unpushed Git state and unpersisted artifacts in the old directory
  are lost; the platform does not guarantee directory continuity.
- Workflow recovery semantics stay unchanged: the Profile's push discipline is
  the recovery point, and prepare clones and checks out again in the new
  directory.

### Initialization

- Workflow path: clean initialization. Prepare performs a fresh clone from the
  Repository resource. Workspaces for parallel Issues use separate directories
  with no shared checkout or dependency cache.
- Interactive path: an empty directory plus Repository access. The Agent
  organizes it according to convention; the platform creates no internal
  layout in advance.

### Archival

Archival is the Workspace's only terminal operation:

- Workflow path: archive automatically when the Issue becomes done or
  cancelled.
- Interactive path: `mo workspace close <name>` archives explicitly. Loss of an
  ingress, such as Slack channel archival, also triggers archival. Archival
  releases the Origin, so the next trigger in that channel provisions a new
  Workspace.
- Reject `mo workspace close` while active Sessions remain bound and tell the
  user to stop them or wait. A Workspace whose Origin is an Issue does not
  accept manual close; direct the user to `issue done / close`, because only an
  Issue terminal transition may archive it.
- After archival, retain the entity for queries, reject new bindings, and grant
  the Runner permission to reclaim the directory.

### Runner-Side Directory Reclamation

The Runner keeps its existing periodic retention and storage-budget
maintenance. The reclamation guard changes from "WorkflowRun is terminal" to
the Workspace view:

| Workspace status | Directory handling |
|---|---|
| active with active bound Sessions | reclamation forbidden |
| active without active bindings | may be reclaimed by disk policy; the entity remains and the next binding rematerializes it |
| archived | delete under the reclamation grant |

Every deletion must still acquire the directory's Runtime removal fence; that
invariant does not change.

### Prompt Anchoring

For execution bound to a Workspace, the Runner injects a working-directory
anchor containing the absolute path and the instruction "all Workspace files
are here; do not search `$HOME`". AGENTS.md and prompt conventions define the
internal layout; it is not platform schema.

## Status

Workspace identity and explicit create/archive lifecycle are implemented.
Issue, Slack, Web, and CLI origins resolve or provision Workspaces; named Runner
materialization, cross-Session reuse, home affinity, and Workspace-aware
reclamation guards are also implemented for their current owners. AgentJob
scheduling can clear an offline Home and rematerialize elsewhere. WorkflowRun
assignment remains pinned to its original Runner, so the cross-Runner Workflow
rematerialization target above is not implemented. The Slack adapter also does
not yet propagate a provider channel-archive event to the Server's archive
boundary.

Open questions concern compound-Issue repository attachment, Runtime Binding
after rematerialization, and whether Workflow OpenSpec artifacts belong at the
Workspace root. Git worktrees remain a Git implementation detail; a spawned
Session always inherits its parent Workspace.
