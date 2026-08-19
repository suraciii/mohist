# Subagents and Session Trees

A Mohist Agent can decompose a task from its own session by starting a new
session for another Agent. Decomposition can continue through multiple levels
to form a **session tree**. A parent session delegates work, a child session
performs it, and the result returns to the parent.

Agent resources are always flat and have no hierarchy: **Subagent is a role in
session context, not an Agent property**. The same Agent is a top-level Agent
when a user starts it directly and a Subagent when another session spawns it.
The parent-child relationship belongs to AgentSessions. This is like a process
tree: programs have no parent-child relation, but processes do.

```text diagram
Control plane: flat Agent resources, with no parent-child relation

  [lead]   [terra]   [luna]   [e2e]   [reviewer]
     |
     | mo agent launch (the user starts the tree root)
     v
Execution plane: a recorded session tree that grows during work

  S1: lead
     +-- spawn --> S2: terra
     +-- spawn --> S3: luna
     |                 +-- spawn --> S5: e2e
     +-- spawn --> S4: reviewer

  Child sessions inherit the parent working directory.
  For file isolation, the Agent can use git inside that directory.
```

## Division of Work with a Workflow

- A Workflow orchestrates work with a **known shape**. Its Definition specifies
  stages, tasks, checks, and approval points in advance.
- A session tree orchestrates work whose **shape becomes known only at runtime**.
  After reading a task, the lead Agent decides how many parts to create, who
  receives each part, and when to collect the results.

A process that can be defined in advance belongs in a Workflow. Decomposition
that depends on information found at runtime and requires Agent judgment belongs
in a session tree. A session tree neither passes through nor replaces a
Workflow.

## Mental Model

- **Mohist provides capabilities; the Agent plans the work**. Mohist provides
  the CLI, Skills, and messages. An Agent combines them to inspect, wait,
  accept, and correct work. Mohist does not include built-in fork-join,
  automatic restart, or automatic inspection processes. An Agent explicitly
  schedules a timed input when it needs to wake at a specific time.
- **The Agent owns work discipline**. Nonoverlapping assignments, required
  delivery reports, result verification, and git as the final arbiter are lead
  Agent disciplines carried by Skills and Instructions, not product mechanisms.

## Capability Declaration

By default, an Agent cannot use any Subagent. When managing an Agent, use its
**available Agent list** to declare which defined Mohist Agents it can spawn.

The declaration is part of the execution definition. It is fixed when the
AgentJob starts, and edits affect only work started later. Renaming an Agent in
the list does not change permission. An archived Agent no longer accepts new
delegations. An unauthorized spawn is rejected and reports the declaring
Agent's permitted scope.

Creating a lead Agent that can decompose work requires three control-plane
settings, all in the existing Agent configuration:

1. Add the Subagent collaboration discipline through **Skills**.
2. **Declare** the available Subagents.
3. Define the division-of-work policy in **Instructions**, including which work
   goes to which Agent and when to stop.

## Known at Startup

The available Subagent list loads into the session with the AgentJob through the
same mechanism as a Skill. From its first Turn, the Agent knows its session
identity, whether it can decompose work, which Agents it can invoke, and how to
invoke them. Each list entry contains a name and description. The description
answers when that Agent should be selected.

Mohist does not recommend or route automatically. Agent selection is the lead
Agent's judgment. When it needs more information, the Agent can query
`mo agent list` and `mo agent view`.

## Spawn and Context

`mo agent spawn` starts a child session:

```bash
mo agent spawn reviewer --project <project-id> --parent-session <session-id> --prompt "Review changes in the current working directory, focusing on the token storage path" --idempotency-key <key>
```

A child session always inherits the parent session's working directory and uses
the same Workspace. Spawn does not accept an alternate Workspace or path. When
an assignment needs file isolation, the Agent can create a git worktree in the
shared directory. A git worktree is a git tool whose use the Agent decides; the
platform has no child-specific Workspace primitive.

Spawn is a normal Agent launch plus an explicit parent-child relation.
`--parent-session` names the delegating session because a working directory does
not prove delegation. The caller and `--idempotency-key` identify one delegation
intent, so a retry after interruption returns the same child or the same
rejection instead of starting duplicate work. Only a new key means new work.

Before Mohist accepts the relation, a failed check leaves no executable child.
Temporary loss of the parent's execution environment may be retried with the
same key. Mohist rejects the delegation when the parent has no inheritable
working directory, the target is outside the declared scope, the target Needs
setup, or the target is archived. After acceptance, the child is ordinary work:
later parent changes do not rewrite its history, and stop follows the
normal child lifecycle. See [Subagent Design](../design/subagents.md) for the
concurrency and recovery protocol behind this guarantee.

Context rules:

- **Clean context and explicit brief**: A child session starts with empty
  context. Its only input is the spawn prompt and context references. The parent
  must put all necessary information in the brief; the child cannot read the
  parent's conversation history.
- **Working directory inherited from the parent**: The child session
  starts with the parent's currently available working directory and uses the
  same Workspace. The caller cannot specify a path; the working directory must
  be the one currently available to the parent. If the parent has no such
  directory or usable execution environment, spawn is explicitly rejected and
  does not silently run elsewhere. For isolation, the child can create a git
  worktree in the inherited directory. The parent's assignment discipline must
  still prevent file conflicts, and git is the final arbiter. Any parent without
  an authoritative working directory and usable execution environment is
  rejected; the source of the parent Session does not bypass that check.

## Messages Between Parent and Child

Three messages flow between parent and child. A terminal report goes from
child to parent automatically: when the child's first delegation ends, the
parent receives an Input that identifies the child session, its result, and
the result location. A steer goes from parent to child, and a request for help
goes from child to parent; both use `mo session followup`, and the child
learns its parent session during spawn.

A terminal report is only a pointer. The parent retrieves details from the
transcript, working directory, and git state. The Input wakes an idle parent and
starts a new Turn; it waits in order while the parent is busy. Only terminal
state is reported, so progress does not interrupt the parent.

Inspection and waiting are parent responsibilities. The parent Agent decides
when to query, when to wait for reports, and whether to verify results.
`mo session tree` shows the complete session tree and each node's state and is
the parent's inspection entry point:

```bash
mo session tree <session-id>
```

For a large tree, results are paginated. Continuous pagination fixes the tree
shape observed by the first request and neither duplicates nor omits nodes.
Sessions added or detached during pagination appear only in a new read.

## Lifecycle

- **Cascade stop**: Stopping a session stops active work throughout the subtree
  still attached below it at that time. A queued Turn ends locally; a running
  Turn ends after Runtime confirmation; both are recorded cancelled. The
  sessions remain and can be continued explicitly later. An unconfirmed stop
  result is shown accurately and can be retried. Stop, detach, and spawn can
  race; [Subagent Design](../design/subagents.md) defines how the Session
  owner arbitrates them.

```bash
mo session stop <session-id> --idempotency-key <key>
```

- **Detach is the escape hatch**: When a child session produces independent
  value and must remain, detach it from the tree first. A later parent stop no
  longer affects it. For example, detach an exploration session to hand it to
  the user.

```bash
mo session detach <session-id>
```

- **No automatic restart**: A child failure does not retry automatically. After
  a failure report, the parent decides whether to retry, select another Agent,
  or escalate to the user.
- **No depth or breadth limit**: Mohist does not reject decomposition based on
  the number of levels or sessions in a tree. Normal Agent concurrency and
  queue resource boundaries still apply, and large trees use pagination. The
  parent Agent remains responsible for decomposition and waiting.

## Scheduled Input

A caller can schedule an **input delivered only at a specified time**. It records
the text and due time now. At that time, Mohist appends it to the session as an
ordinary Input through the same acceptance and execution path as a follow-up.
The scheduler can be a user or the Agent itself. A parent can schedule a later
question for a child, and an Agent can schedule a check if it is still waiting.
This is not automatic inspection. Mohist does not decide when to remind anyone;
only an explicitly created schedule triggers.

```bash
mo session schedule create <session-id> --at 2026-08-06T14:00:00+08:00 --text "Report current progress" --idempotency-key <key>
mo session schedule list <session-id>
mo session schedule cancel <session-id> <schedule-id>
```

- **One-time absolute time**: `--at` accepts only RFC 3339 with a time-zone
  offset, such as `2026-08-06T14:00:00+08:00` or `...Z`. A time without an
  offset or in the past is rejected. There is no repeated schedule; create
  another schedule to wake again.
- **At the due time**: An idle session wakes and starts a new Turn. A busy
  session receives the Input in ordinary order. When Mohist cannot confirm
  session state, the Input remains pending delivery and delivery continues
  after confirmation recovers. Mohist never pretends that it delivered the
  Input or silently discards it.
- **Cancel**: A schedule not yet delivered can be cancelled and will not
  deliver. Cancelling an already delivered schedule has no effect on its Input.
  A schedule does not expire automatically; cancellation is its only exit.
- **Independent from lifecycle**: Stop, cascade stop, detach, Reset, and Compact
  do not delete schedules. Delivery that meets a stop in progress waits until
  stop finishes. A detached session still receives its scheduled Input at the
  due time.

The first version does not include repeated or periodic scheduling such as
cron, relative times such as "in 30 minutes," attachments, automatic launch of
a new session at the due time, or schedule display in `mo session view` or
`mo session tree`.

## Status

Capability declaration, startup awareness, `mo agent spawn`, terminal reports,
`mo session tree`, cascade stop, detach, and scheduled input are implemented.
A child always inherits the parent Workspace and working directory. Git
isolation remains an Agent and Git concern rather than a second Session Tree
Workspace lifecycle.

Temporary anonymous child sessions are intentionally excluded. A child
session identity must come from a defined Agent so that configuration has one
owner and remains visible, adjustable, and reusable in the control plane. The
spawn task brief provides runtime flexibility without a temporary role. A
future incremental capability can add runtime-customized roles if a concrete
need appears without changing this model.
