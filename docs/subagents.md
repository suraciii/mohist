# Subagents and Session Trees

A Mohist Agent can delegate from its Session by starting a Session for another
Agent. The Sessions form a tree. Agent resources stay flat: **Subagent** is a
role in Session context, not an Agent property.

## Product Commitments

- A parent Session delegates work. A child Session performs it and returns a
  result to the parent.
- The same Agent is top-level when started by a user and a Subagent when
  started by another Session. The parent-child relation belongs to Sessions.
- A child inherits the parent's Workspace and working directory.
- A Workflow defines its shape before execution. A Session Tree decides its
  shape during execution. A Session Tree does not replace a Workflow.
- Mohist provides capabilities. The Agent owns decomposition, assignment,
  waiting, verification, and correction. Mohist does not provide automatic
  fork-join, restart, or inspection.
- An Agent cannot spawn another Agent unless its available Agent list declares
  that target. The list is fixed when the AgentJob starts.
- An archived Agent rejects new delegations. An unauthorized spawn is rejected
  without creating a child.
- A child starts with empty context. The spawn brief and its references are
  the child's only initial input.
- The caller and an idempotency key identify one delegation intent. Retrying
  with that key returns the same child or rejection; a new key means new work.
- A child failure does not retry automatically. The parent chooses retry,
  another Agent, or escalation.
- Stop, detach, and spawn races are resolved by the Session owner. See
  [Subagent Design](../design/subagents.md) for the design contract.

## Session Tree

Agent resources have no parent-child relation. Session records do:

```text diagram
         +--------------------------+
         | Agent resources are flat |
         +-------------+------------+
                       :
                       v
                 +----------+
                 | S1: lead |
                 +----++----+
       +--------------++---------------+
       vspawn         vspawn           vspawn
 +-----------+  +----------+   +--------------+
 | S2: terra |  | S3: luna |   | S4: reviewer |
 +-----------+  +-----+----+   +--------------+
                      |
                      vspawn
                 +---------+
                 | S5: e2e |
                 +---------+
```

The user starts the root Session. A parent creates child Sessions through
`mo agent spawn`. Each child keeps the parent Workspace and working directory.

## Workflow or Session Tree

Use a Workflow when its stages, tasks, checks, and Approval Points are known
before execution. Use a Session Tree when the lead Agent must choose the
number of parts, assignments, and collection order after reading the task.

## Capability Declaration

The available Agent list is part of the Agent execution definition. It names
which defined Agents the Agent may spawn. The declaration is fixed when the
AgentJob starts. Renaming an Agent does not change permission. An archived
Agent does not accept new delegations.

A lead Agent that can decompose work needs all three settings in its existing
Agent configuration:

1. Add the Subagent collaboration discipline through **Skills**.
2. **Declare** the available Subagents.
3. Define the division-of-work policy in **Instructions**, including assignment
   and stopping rules.

## Known at Startup

The available Agent list loads with the AgentJob through the same mechanism as
Skills. From its first Turn, the Agent knows its Session identity, whether it
can decompose work, which Agents it may invoke, and how to invoke them. Each
entry has a name and description. The description states when to select it.

Mohist does not recommend or route automatically. Agent selection is the lead
Agent's judgment. The Agent may query `mo agent list` and `mo agent view`.

## Spawn and Context

`mo agent spawn` starts a child Session:

```bash
mo agent spawn reviewer --project <project-id> --parent-session <session-id> --prompt "Review changes in the current working directory, focusing on the token storage path" --idempotency-key <key>
```

Spawn is a normal Agent launch with an explicit parent-child relation.
`--parent-session` names the delegating Session because a working directory
does not prove delegation. The caller and `--idempotency-key` identify one
intent. A retry with the same key returns the same child or rejection.

The spawn boundary rejects a parent without an inheritable working directory,
a target outside the declared scope, a target that Needs setup, or an archived
Agent. A failed check leaves no executable child. Temporary loss of the
parent execution environment may be retried with the same key.

The child always inherits the parent's current Workspace and working
directory. Spawn accepts no alternate Workspace or path. For file isolation,
the Agent may create a git worktree in the inherited directory. The platform
has no child-specific Workspace primitive.

A child starts with empty context. The parent must put all required information
in the brief. The child cannot read the parent's conversation history. Later
parent changes do not rewrite the child's history. After acceptance, the child
is ordinary work and follows the normal child lifecycle.

## Messages Between Parent and Child

A terminal report goes from child to parent automatically when the child's
first delegation ends. It identifies the child Session, its result, and the
result location. The parent retrieves details from the transcript, working
directory, and git state. The report wakes an idle parent and waits in order
while the parent is busy. Only terminal state is reported.

A steer goes from parent to child. A request for help goes from child to parent.
Both use `mo session followup`. The child learns its parent Session during
spawn.

Inspection and waiting belong to the parent Agent. `mo session tree` shows the
complete tree and each node's state:

```bash
mo session tree <session-id>
```

Results are paginated for a large tree. Pagination fixes the tree shape seen by
the first request. Sessions added or detached during pagination appear only in
a new read.

## Lifecycle

### Cascade stop

Stopping a Session stops active work throughout the attached subtree at that
time. A queued Turn ends locally. A running Turn ends after Runtime
confirmation. Both are recorded cancelled. Sessions remain and can be
continued explicitly. An unconfirmed stop result is shown accurately and can
be retried.

```bash
mo session stop <session-id> --idempotency-key <key>
```

### Detach

Detach a child when its independent value must remain after a parent stop. A
detached Session is no longer affected by later parent stops.

```bash
mo session detach <session-id>
```

### Restart and failure

Mohist does not restart a child automatically. After a failure report, the
parent chooses whether to retry, select another Agent, or escalate. Mohist does
not impose a depth or breadth limit. Normal Agent concurrency and queue limits
still apply.

## Scheduled Input

A schedule delivers one Input to a Session at an explicitly specified time. It
stores the text and due time immediately. At the due time, Mohist appends the
Input through the ordinary acceptance and execution path. A user or Agent may
create the schedule. This is not automatic inspection.

```bash
mo session schedule create <session-id> --at 2026-08-06T14:00:00+08:00 --text "Report current progress" --idempotency-key <key>
mo session schedule list <session-id>
mo session schedule cancel <session-id> <schedule-id>
```

- `--at` accepts only RFC 3339 with a time-zone offset, such as
  `2026-08-06T14:00:00+08:00` or `...Z`. A time without an offset or in the
  past is rejected.
- A schedule is one-time. There is no repeated schedule; create another one.
- An idle Session wakes and starts a Turn. A busy Session receives the Input in
  ordinary order.
- If Session state is unknown, the Input remains pending delivery and delivery
  continues after confirmation recovers. Mohist never pretends delivery or
  silently discards the Input.
- A schedule not yet delivered can be cancelled. Cancelling a delivered
  schedule does not change its Input. A schedule does not expire automatically.
- Stop, cascade stop, detach, Reset, and Compact do not delete schedules.
  Delivery that meets a stop waits until stop finishes. A detached Session
  still receives its scheduled Input.

The first version excludes repeated or periodic schedules, relative times,
attachments, automatic launch of a new Session at the due time, and schedule
display in `mo session view` or `mo session tree`.

## Implementation Gaps

Capability declaration, startup awareness, `mo agent spawn`, terminal reports,
`mo session tree`, cascade stop, detach, and scheduled Input are implemented.
A child inherits the parent Workspace and working directory. Git isolation
remains an Agent and Git concern, not a second Session Tree Workspace
lifecycle.

Temporary anonymous child Sessions are excluded. A child Session identity must
come from a defined Agent so configuration has one owner and remains visible,
adjustable, and reusable. The spawn brief provides runtime flexibility without
a temporary role.
