# Input And Turns

These rules govern the continuing Session created by
[Agent execution](../../agent/execution/spec.md). For missing or uncertain
Runtime state, use [Session recovery](../recovery/spec.md).

## Why Work and Conversation Are Separate

One launch creates work and a place to continue the conversation, but they have
different lifetimes. A clear work result must not close a useful conversation,
and a follow-up must not rewrite the original delegation result.

```text diagram
+--------------+    +---------+   +---------+   +---------+
| Mohist Agent |    | Input 1 +---| Input 2 +---| Input 3 +-++
+-------+------+    +---------+   +---------+   +---------+ ||
        +------------------+                                ||
                           vlaunch                          ||
               +----------------------+                     ||
               | AgentJob (delegation |                     ||
               |       result)        |                     ||
               +-----------+----------+                     ||
                           |                                ||
                           v                                ||
               +-----------------------+                    ||
               | AgentSession (record) |                    ||
               +-----------+-----------+                    ||
                     +-----+------+                         ||
                     v            v                         ||
                +--------+   +--------+                     ||
                | Turn 1 |<--| Turn 2 |<--------------------++
                +--------+   +--------+
```

AgentJob answers whether the first delegation completed, failed, was rejected,
cancelled, or blocked. AgentSession records every Input and Turn and whether it
can continue. SessionInput records acceptance and placement. AgentTurn records
Runtime processing and result. Later Turns do not modify the first AgentJob
result.

### Execution Context Privacy

Session lists and summaries identify context with Issue, Epic, Repository, and
named Workspace when available. They never expose a filesystem `workspacePath`.
Users navigate with the named Workspace. A Workflow uses the same AgentJob and
AgentSession model as every other entry point and does not advance from Session
activity. Business work that must reach Done belongs in an Issue and Workflow.

## Continuing a Session

Every accepted Follow-up gets one stable Input identity. It joins a running Turn
when Runtime steer is supported; otherwise it starts or queues a later Turn.
Neither path creates another AgentJob.

A Runtime activity event without a Turn identity cannot complete a queued
Follow-up. When an initial launch reaches terminal while a Follow-up waits,
Mohist keeps the Follow-up queued and dispatches it next. A terminal first
AgentJob does not settle a later Follow-up. Accepted Input is never changed to
rejected because execution later fails. A full queue rejects new Input before
acceptance. An uncertain response reports `unknown` and does not create another
Turn as a guess.

The Web UI and CLI show Input acceptance separately from Turn execution and
result. See [Session timeline design](../transcript/design.md).

## AgentSession Origin and Addressing

Each AgentSession has one immutable Agent Origin. A Workflow, Web, CLI,
Connection, event route, or mention is launch attribution, not another Origin
or work owner. A Workflow launch also records WorkflowRun, Stage, Task, and
Attempt attribution.

Matching Models, Prompts, Runtime configuration, or Workflow attribution do not
merge Sessions. Replacing a Runtime Session does not change Origin. A configured
Session name may continue one Session within a WorkflowRun only when Agent and
Workspace identities match. Without an explicit name, each AgentJob receives a
distinct Session.

Use the same `mo session` surface for every Origin:

- `mo session view <session-id>` and `mo session transcript <session-id>` read by
  stable Session ID.
- `mo session followup`, `compact`, `reset`, and `stop` act on the Session.
- `mo session list` discovers Sessions by Agent, Issue, or WorkflowRun.

See the [CLI Reference](../../interfaces/cli/spec.md#agent-agentjob-and-session) for exact
arguments and operation keys.
