# Workspace

A Workspace is a persistent execution environment under a Project. It contains
a set of working directories and access to one or more repositories. It
persists across AgentSessions and Agents. Multiple AgentSessions and Agents
can continue work in the same Workspace. A later participant sees the same
directories, installed dependencies, research material, and uncommitted
changes.

Work happens in a Workspace; repositories are its inputs. Repository checkouts
live under the Workspace. Plans, research, notes, and other work products belong
directly to the Workspace rather than to one repository. Work that spans
several repositories therefore always has a place at the Workspace level.

## Two Sources

### Workspace for an Issue Workflow

When an Issue first starts, it automatically receives a Workspace named
`issue-<number>`. Mohist initializes it from a clean checkout of the target
repository. It does not share directories with other Issues, so many Issues can
run in parallel without interfering with each other.

All Stages, retries, AgentSessions, and invited Agents for the same Issue
share this Workspace. Mohist archives it when the Issue completes or is
cancelled.

### Workspace for an Interactive Entry Point

An AgentSession started directly from Slack, Web, or CLI also runs in a
Workspace. By default, each interaction location has one Workspace:

- **Slack**: Each channel has one Workspace. All AgentSessions and invited
  Agents in the channel use that Workspace.
- **Web**: Each conversation has one Workspace.
- **CLI**: Create one explicitly with `mo workspace create <name>` and bind an
  AgentSession with `--workspace <name>`. Without `--workspace`,
  `mo agent launch` binds the current Project's default Workspace. When needed,
  Mohist creates `cli-current` with source `cli`. CLI output shows the actual
  binding so that the default scope is not hidden.

Mohist does not initialize an interactive Workspace from a clean state. It
accumulates work over time, which enables reuse across AgentSessions.

## Binding and Sharing

- A new AgentSession uses the Workspace resolved from its interaction location
  by default. Another AgentSession in the same channel uses the same Workspace.
- An Agent invited into an AgentSession or channel enters the same Workspace
  and sees the same files.
- A delegated child AgentSession inherits the same Workspace. To isolate a
  child, let the Agent create a Git worktree inside the inherited directory.
  Spawn cannot select another Workspace. A Git worktree is a Git tool; Mohist
  does not provide a child-specific Workspace primitive.
- At any time, one interaction location maps to one active Workspace. There is
  always one answer to the question, "Which directory is the Agent using here?"

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

A Workspace holds references to repositories declared as Project resources. An
Issue Workflow starts with the Issue's target repository. Runner owns that
checkout's branch, marker, and layout so every Stage, retry, and integration
step observes the same Workspace identity. Treat its recorded home as opaque
and inspect it through `mo workspace view`.

An interactive Workspace mounts repositories as needed. There, a mount grants
access and provides a default checkout target, while the Agent controls the
actual clone, branch, and worktree layout. Conventions such as placing checkouts
under `repos/` and work products at the Workspace root belong in the Prompt and
are not enforced by the platform.

## Lifecycle Endpoints

- Completing or cancelling an Issue archives its Workflow Workspace.
- `mo workspace close <name>` archives an interactive or manually created
  Workspace. An Issue Workspace can end only through `mo issue done` or
  `mo issue close` because the Issue lifecycle owns it.
- Archiving a Slack channel archives its Workspace. The next message in that
  channel automatically starts a new Workspace. To discard a disordered
  environment and continue, close it and send another message.
- An archived Workspace remains available for history but accepts no new
  AgentSessions.

## Events

Workspace creation and archival produce platform events that subscribers can
filter by source and answer. For example, a channel Agent can perform cleanup
after an archive event, or a create event can trigger dependency installation.
See [Event Routing](event-routing.md) for the event contract.

## Missing Directory

The Runner that executes a Workspace hosts its directories. The Workspace still
exists after Runner failure or disk cleanup, but its directory contents are not
guaranteed to return. A missing directory can be rematerialized empty on the
same home Runner, and unpushed work is lost. A Workflow preserves completed work by
pushing its Workflow branch to the remote. Important work in an interactive
Workspace must be committed and pushed.

## Implementation Gaps

Workspace identity, create and archive lifecycle, Issue and interactive source
resolution, named Runner materialization, and cross-AgentSession reuse are
implemented. AgentJobs can replace an offline home; WorkflowRuns cannot yet move
their assignment to another Runner. Slack channel archival also does not yet
archive its Workspace; until that linkage exists, close the interactive
Workspace explicitly when the channel should no longer retain an active
environment.
