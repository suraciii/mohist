# Workspace

A Workspace is a persistent execution environment under a Project. It holds
repository access, working directories, and work products across AgentSessions
and WorkflowRuns. Later participants see the same directories, installed
dependencies, research material, and uncommitted changes. Plans, research, and
other work products belong to the Workspace rather than a Repository.

## Product Commitments

- A Workspace remains available across AgentSessions, Agents, retries, and
  WorkflowRuns that share its scope.
- An Issue Workflow receives an isolated Workspace automatically.
- An interactive entry point resolves a Workspace from its interaction source
  or from an explicit CLI selection.
- One interaction location has one active Workspace at a time.
- A Workspace can hold several Project Repositories and work that spans them.
- A missing Runner directory may lose unpushed work, but it does not change
  Workspace identity.
- Important interactive work must be committed and pushed. Workflow work is
  preserved through the Workflow branch.

## Workspace for an Issue Workflow

When an Issue first starts, Mohist creates a Workspace named `issue-<number>`.
It initializes the Workspace from a clean checkout of the Issue's target
Repository. It does not share directories with other Issues, so Issues can run
in parallel without interfering.

All Stages, retries, AgentSessions, and invited Agents for the Issue share this
Workspace. Mohist archives it when the Issue completes or is cancelled.

## Workspace for an Interactive Entry Point

An AgentSession started from Slack, Web, or CLI also runs in a Workspace. By
default, each interaction location resolves one Workspace:

- **Slack:** each channel has one Workspace. AgentSessions and invited Agents in
  that channel use it.
- **Web:** each conversation has one Workspace.
- **CLI:** create one explicitly with `mo workspace create <name>` and bind an
  AgentSession with `--workspace <name>`. Without that option, `mo agent launch`
  uses the current Project's default Workspace. When needed, Mohist creates
  `cli-current` with source `cli`. CLI output shows the actual binding.

Mohist does not initialize an interactive Workspace from a clean state. It
accumulates work over time so later AgentSessions can reuse it.

## Binding and Resolution

- A new AgentSession uses the Workspace resolved from its interaction location
  unless the caller selects another permitted binding.
- An Agent invited into an AgentSession or channel enters the same Workspace
  and sees the same files.
- A delegated child AgentSession inherits the parent Workspace. To isolate a
  child, the Agent may create a Git worktree inside that Workspace. Spawn does
  not select another Workspace. A Git worktree is a Git tool, not a
  child-specific Workspace primitive.
- At any time, one interaction location maps to one active Workspace. The
  binding answers which directory the Agent uses there.

## Commands

```bash
# Create a shared environment and let two Agents join in sequence.
mo workspace create payment-refactor --repo server --repo web
mo agent launch coder --workspace payment-refactor
mo agent launch reviewer --workspace payment-refactor

# Inspect the source, repositories, and bound AgentSessions.
mo workspace list --status active
mo workspace view payment-refactor
mo session list --workspace payment-refactor

# Change repository membership, then archive the Workspace.
# Archive is rejected with a recovery instruction while AgentSessions are active.
mo workspace repo add payment-refactor infra
mo workspace close payment-refactor
```

See [CLI Reference](cli-reference.md#workspace) for the complete command
contract.

## Repositories

A Workspace holds references to Repositories declared by its Project. An Issue
Workflow starts with the Issue's target Repository. Runner owns that checkout's
branch, marker, and layout so every Stage, retry, and integration step observes
the same Workspace identity. Treat the recorded home as opaque and inspect it
through `mo workspace view`.

An interactive Workspace mounts Repositories as needed. A mount grants access
and provides a default checkout target, while the Agent controls its clone,
branch, and worktree layout. Layout conventions in an interactive Workspace
belong in the Prompt and are not enforced by the platform.

## Layout

A Workflow Workspace has this fixed root layout:

```text literal
issue-<number>/
├── .mohist/                  # Platform marker and identity files
├── REPOS/<repository-name>/  # Repository checkout; only this tree enters Git
├── PLANS/                    # Plans, designs, review reports, and the task list
├── RESEARCH/                 # Research notes and exploration material
└── .scratch/                 # Temporary files
```

Only `REPOS/` participates in Git. Everything else is Workspace-local work
material and never appears in a commit, branch, or Pull Request. The Workflow
branch is the recovery point for Repository work. Plan and review material under
`PLANS/` is uploaded as run artifacts for evidence and audit. It has no
per-file recovery point. If the Workspace directory is lost, rerun from the
Plan Stage to regenerate it.

Each Workflow dispatch has two directory boundaries. Its execution directory is
the Workspace root unless the Task selects another Workspace-relative path with
`working-directory`. Its Repository guard directory is always
`REPOS/<repository-name>` for the Issue's target Repository. Branch stability,
dirty-worktree detection, residual Git state, and Git cleanup inspect that
Repository guard even when the Task executes from the Workspace root.

Repository-only Tasks, such as Git Actions and verification scripts, use
`working-directory: REPOS/<repository-name>`. Agents anchor at the Workspace
root and see `REPOS/` and `PLANS/` side by side. The Workspace root is not a Git
checkout, and Git guards remain in the Repository.

## Lifecycle Endpoints

- Completing or cancelling an Issue archives its Workflow Workspace.
- `mo workspace close <name>` archives an interactive or manually created
  Workspace. An Issue Workspace can end only through `mo issue done` or
  `mo issue close` because the Issue lifecycle owns it.
- Archiving a Slack channel archives its Workspace. The next message in that
  channel starts a new Workspace. To discard a disordered environment, close it
  and send another message.
- An archived Workspace remains available for history but accepts no new
  AgentSessions.

## Events

Workspace creation and archival produce platform events. Subscribers can filter
those events by source. For example, a channel Agent can clean up after an
archive event, or a create event can trigger dependency installation. See
[Event Routing](event-routing.md) for the event contract.

## Runner-side Directory Reclamation

The Runner that executes a Workspace hosts its directories. The Workspace still
exists after Runner failure or disk cleanup, but its directory contents are not
guaranteed to return. Mohist may rematerialize a missing directory empty on the
same home Runner, and unpushed work is lost.

A Workflow preserves completed Repository work by pushing its Workflow branch to
the remote. Important work in an interactive Workspace must be committed and
pushed.

## Implementation Gaps

Workspace identity, creation and archival, Issue and interactive source
resolution, named Runner materialization, and cross-AgentSession reuse are
implemented. AgentJobs can replace an offline home, but WorkflowRuns cannot yet
move their assignment to another Runner. Slack channel archival does not yet
archive its Workspace; close the interactive Workspace explicitly when a
channel should no longer retain an active environment.
