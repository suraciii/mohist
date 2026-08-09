# Workspace

A Workspace is a persistent execution environment under a Project. It contains
a set of working directories and access to one or more repositories. It
persists across Sessions and Agents. Multiple Sessions and Agents can continue
work in the same Workspace. A later participant sees the same directories,
installed dependencies, research material, and uncommitted changes.

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

All Stages, retries, Sessions, and invited Agents for the same Issue share this
Workspace. Mohist archives it when the Issue completes or is cancelled.

### Workspace for an Interactive Entry Point

A Session started directly from Slack, Web, or CLI also runs in a Workspace.
By default, each interaction location has one Workspace:

- **Slack**: Each channel has one Workspace. All Sessions and invited Agents in
  the channel use that Workspace.
- **Web**: Each conversation has one Workspace.
- **CLI**: Create one explicitly with `mo workspace create <name>` and bind a
  Session with `--workspace <name>`. Without `--workspace`,
  `mo agent launch` binds the current Project's default Workspace. When needed,
  Mohist creates `cli-current` with source `cli`. CLI output shows the actual
  binding so that the default scope is not hidden.

Mohist does not initialize an interactive Workspace from a clean state. It
accumulates work over time, which enables reuse across Sessions.

## Binding and Sharing

- A new Session uses the Workspace resolved from its interaction location by
  default. Another Session in the same channel uses the same Workspace.
- An Agent invited into a Session or channel enters the same Workspace and sees
  the same files.
- A delegated child Session inherits the same Workspace. To isolate a child,
  bind another Workspace during delegation or let the Agent create a Git
  worktree inside the directory. A Git worktree is a Git tool; Mohist does not
  provide an "isolated workspace" primitive.
- At any time, one interaction location maps to one active Workspace. There is
  always one answer to the question, "Which directory is the Agent using here?"

## Commands

```bash
# Create a shared environment and let two Agents join in sequence.
mo workspace create payment-refactor --repo server --repo web
mo agent launch coder --workspace payment-refactor
mo agent launch reviewer --workspace payment-refactor

# Inspect the source, repositories, and bound Sessions.
mo workspace list --status active
mo workspace view payment-refactor
mo session list --workspace payment-refactor

# Change repository membership, then archive the Workspace.
# Archive is rejected with a recovery instruction while Sessions are active.
mo workspace repo add payment-refactor infra
mo workspace close payment-refactor
```

See [CLI Reference](cli-reference.md#workspace) for the complete command
contract.

## Repositories

A Workspace holds references to repositories declared as Project resources. A
Workflow path starts with the Issue's target repository. An interactive path
mounts repositories as needed. A mount grants access and provides a default
checkout target. The Agent controls the actual clone, branch, and worktree
layout inside the directory. Mohist does not prescribe the internal layout.
Conventions such as placing checkouts under `repos/` and work products at the
Workspace root belong in the Prompt and are not enforced by the platform.

## Lifecycle Endpoints

- Completing or cancelling an Issue archives its Workflow Workspace.
- `mo workspace close <name>` archives any Workspace.
- Archiving a Slack channel archives its Workspace. The next message in that
  channel automatically starts a new Workspace. To discard a disordered
  environment and continue, close it and send another message.
- An archived Workspace remains available for history but accepts no new
  Sessions.

## Events

Workspace creation and archival produce the `workspace.created` and
`workspace.archived` platform events. Each event includes the Project,
Workspace name, and source: `issue`, `manual`, `slack`, `web`, or `cli`.
Subscribers can filter by source. For example, a channel Agent can perform
cleanup after an archive event, or a create event can trigger dependency
installation. See [Event Routing](event-routing.md) and
[Event Protocol](../design/event-protocol.md).

## Missing Directory

The Runner that executes a Workspace hosts its directories. The Workspace still
exists after a Runner failure or disk cleanup, but its directory contents are
not guaranteed to return. The next use starts from an empty directory, and
unpushed work is lost. A Workflow preserves completed work by pushing its
Workflow branch to the remote. Important work in an interactive Workspace must
be committed and pushed.

## Implementation Gaps

Workspace identity and the create and archive lifecycle are implemented.
Dynamic creation from Slack and Web entry points, and the connection from Slack
channel archival to Workspace archival, are not implemented. Interactive entry
points currently cover only the `manual` source. Directory materialization is
still organized by WorkflowRun on the Runner. Migration to a Workspace view for
cross-Session reuse and reclamation guards remains future work.
