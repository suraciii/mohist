# Agents and AgentSessions

A Mohist Agent is a configurable resource in a Project. People can start it in
the Web UI or CLI, connect it to Slack, or let it respond to events and
mentions. Every entry point uses the same Agent, AgentJob, and AgentSession
model. An External Agent is separate: it uses the Mohist Skill and `mo` to
operate Mohist and is not a Mohist resource.

## Product Commitments

- Users can configure and start an Agent without Slack or another external
  connection.
- The Agent owns its identity, Instructions, Runtime, Model, Reasoning Effort,
  Variant, Skills, and concurrency limit. Entry points do not copy or override
  that definition for one request.
- A new delegation creates one AgentJob, AgentSession, first SessionInput, and
  first AgentTurn. A Follow-up adds an Input to an existing Session and does not
  create another AgentJob.
- AgentJob reports the first delegation result. AgentSession records the
  continuing conversation and whether it can accept more input.
- A Slack message or Web page never becomes the state authority.
- Mohist gives an Agent identity references and self-service guidance. The
  Agent can use `mo` to read Issue, Epic, Repository, and Session details when it
  needs them.

## Concept Layers

- **Mohist Agent**: A reusable Agent resource within a Project. It has a stable
  identity, Instructions, execution configuration, Skills, and state.
- **Agent Connection**: An external entry point for one Agent. It references the
  Agent and owns neither its configuration nor an AgentSession.
- **AgentJob**: One launch execution. It records waiting, execution, completion,
  failure, retry, and recovery for the first delegation.
- **SessionInput**: One input accepted by an AgentSession. It has stable identity,
  content, attachments, source, order, and delivery state.
- **AgentTurn**: One continuous Runtime processing period for ordered Inputs. It
  is owned by an AgentSession and is not top-level work.
- **AgentSession**: The continuing logical Session and audit record. It owns
  Inputs and Turns in order, context, usage, Activity, and current Runtime
  Binding. It has no completed or failed lifecycle.
- **Runtime Session**: The physical conversation maintained by OpenCode, Pi, or
  another backend. It can be replaced without changing AgentSession identity.

An Action is the Agent-to-Runner execution contract. It carries the accepted
Agent snapshot to a backend but has no Agent identity and owns no work lifecycle.

## One Invocation Path

Every entry point starts a real Mohist Agent through one launch boundary. This
includes Workflow, Web, CLI, Agent Connections, event routing, and comment
mentions. Each launch creates an AgentJob, and AgentJob owns execution state,
result, retry, and recovery.

Workflow names the Agent and supplies task input and attribution. It consumes the
AgentJob result to decide Stage advancement. It does not select a Runtime, copy
Agent configuration, or create anonymous Agent capability.

Built-in Profiles use built-in Mohist Agents. A Project Profile may name another
ready Agent. A missing, archived, or not-ready Agent fails launch explicitly;
Mohist does not fall back to a Runtime-specific Workflow Action.

## Mohist Agent

A Mohist Agent is a first-class Project resource. It stores:

- A stable ID and a presentation identity of name, avatar, and description.
- Instructions and execution configuration.
- Skills.
- A concurrency limit and `active` or `archived` state.

## Configure an Agent

- **Name** identifies the Agent within its Project and external locations.
  Renaming does not change the Agent ID.
- **Avatar** identifies the Agent in the Web UI, Slack, and execution records.
  Mohist updates its presentation immediately and asynchronously synchronizes
  external identities that support updates. An out-of-sync state is visible.
- **Description** helps users select the Agent. It is not execution
  Instructions.
- **Instructions** define the Agent's role, behavior, and stopping conditions.
  They are fixed when an AgentJob starts.
- **Runtime** selects the execution backend and belongs to the Agent.
- **Model, Reasoning Effort, and Variant** select model behavior. Model and
  Variant use Project defaults when absent. Reasoning Effort is independent and
  uses Runtime behavior when absent.
- **Skills** load at AgentJob startup and cannot be added or removed for one
  request.
- **Max concurrent runs** limits launches and follow-ups. Lowering the limit
  does not stop running work; excess work queues.
- **State** controls new delegations. An archived Agent rejects new work while
  existing Sessions remain readable and may continue.

Runtime credentials belong in protected Runtime settings. They do not belong in
Instructions, Agent records, or Agent Connections. Reasoning Effort uses `off`,
`minimal`, `low`, `medium`, `high`, `xhigh`, or `max`; it is never encoded as a
Variant. OpenCode does not support explicit Reasoning Effort. Choose Pi or leave
it unset for OpenCode.

An ordinary launch accepts task text and context references. Context is not Agent
configuration. The Agent definition is fixed when the AgentJob starts, as are
its Skills and Workspace identity. Later Agent edits affect later AgentJobs only.
Follow-ups in an existing Session keep the Session's established configuration.

### Project Default Execution Configuration

A Project can hold one default Runtime, Model, and optional Variant. It applies
when an entry point does not supply an accepted value and the Agent definition
leaves that field unset. Resolution order is:

1. An accepted caller value, where the entry point allows one.
2. The Agent definition.
3. The Project default.

A Runtime with no value defaults to `opencode`. An explicitly malformed Runtime
or Model remains a configuration gap; a lower-precedence value cannot hide it.

Changing the default affects later launches. Each AgentJob stores the resolved
configuration at launch. An Agent without a Model can be ready when the Project
default supplies one. Removing that default can restore `needs-setup`. A
Readiness conclusion confirmed by a completed execution is not changed by a
default edit alone.

Configure the default through Project settings. The route contract is in
[External Agent API](agent-api.md#project-default-execution-configuration).
An invalid default is rejected without changing the previous value.

## Readiness and Availability

Agent lifecycle and execution readiness are separate:

- `active` or `archived` says whether the Agent accepts new delegations.
- `ready` means Mohist confirmed that its execution configuration can run.
- `needs-setup` means Mohist found a configuration gap and provides its repair.
- `unknown` means Mohist cannot confirm execution readiness.

A temporarily offline or full Runner is Availability, not a Readiness failure.
Work may be accepted and queued. Entry points present one Mohist conclusion and
do not maintain separate Runtime rules.

### Configure and Test in the Web UI

In **Agents**, create or open an Agent and enter its identity and Instructions.
Select a Runtime, then choose only the Model, catalog-backed Reasoning Effort,
Variant, and Skills that it supports. Set a concurrency limit. The page shows
Readiness and each repair gap. When Readiness is `ready`, use **Start session**
to submit a task. You may submit when it is `unknown`, but the task waits for
Runner validation. After a successful launch, open the AgentSession to inspect
replies and send a follow-up.

### Configure and Use in the CLI

```bash
mo agent create --name explorer --description "Explore product needs" --instructions "Clarify the request, identify missing decisions, and produce actionable issues." --runtime opencode --skills mohist,mohist-explore --max-concurrent-runs 1
mo agent view explorer
mo agent launch explorer --prompt "Explore a product design for invoking a Mohist Agent from Slack"
# After response loss, retry with the key printed before launch. Do not create a new launch.
mo agent launch explorer --prompt "Explore a product design for invoking a Mohist Agent from Slack" --idempotency-key <key>
```

`agent view` shows Readiness, Availability, and configuration gaps. `agent
launch` returns AgentJob, AgentSession, first Input, and Turn IDs. Use
`mo session followup` for a continuing Session and `mo session transcript` for
its record. Observe `accepted`, `queued`, and `running`; read the result at
`terminal`; query or retry the original key when state is `unknown`.

## Launch Entry Points

A task-first launch is available when the caller has a task but does not yet
need to configure an Agent. The [External Agent API](agent-api.md#task-first-launch)
defines its route and replay contract. The Server derives missing definition
fields, creates the Agent, and uses the same AgentJob and AgentSession launch
path.

- Web and CLI task-first launches create a derived Agent and return the same
  Job, Session, Input, and Turn identities.
- An Agent Connection sends a first task through a Slack DM, explicit New task,
  or channel root mention.
- Event routing starts an AgentJob and AgentSession for a matching event.
- A comment mention uses the comment after `@<agent-name>` as task input and
  associates the Issue context.

Selecting or naming an existing Agent in the Web UI or CLI uses the same
unchanged definition-first launch path. The CLI also supports
`mo agent start --prompt <task>` for task-first launch.

A mention is one-time work. For continuous attention, the Agent adds the Issue
to its watch list with `mo issue watch add`. Every entry point fixes the Agent
snapshot for that work. A Mohist Agent may also spawn child Sessions; see
[Subagents and Session Trees](subagents.md).

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
result. See [Session timeline design](../design/session-timeline.md).

## Why Unknown Fails Closed

A lost response can hide success or failure. Repeating a request can duplicate
work or repeat an external side effect. Mohist treats `unknown` as a state to
reconcile, not as permission to retry with a new identity.

- Retry a lost response with the same caller key. Mohist returns or continues
  the original result.
- Reusing a key with different content is rejected. Use a new key for a new
  intent.
- Requests that require a key are rejected before acceptance when it is missing.
- Querying an operation never repeats its side effect.

The Server is the authority. `idle` permits a new Turn, Compact, or Reset;
`active` means work is queued, executing, or awaiting confirmation; `unknown`
blocks new work until the original operation is queried or reconciled.

Stop is the only operation for ending work. A queued Turn is cancelled locally.
A running Turn is cancelled only after Runtime confirmation. An uncertain Stop
leaves the Turn and Session `unknown`.

Force-reset is the explicit escape when an old `unknown` cannot reconcile. It
requires risk acknowledgement, preserves unresolved history, starts a new
context, and accepts new work only after that context is established.

See [External Agent API idempotency](../design/agent-api.md#normalized-fingerprint-and-idempotency)
and [Agent execution design](../design/agent-execution.md#work-lifecycle-and-session)
for detailed identity, fencing, and projection contracts.

## Why Logical and Physical Sessions Are Separate

An AgentSession is Mohist's stable logical identity and audit record. A Runtime
Session is the physical conversation held by an execution backend. Separating
them lets Mohist replace lost or reset Runtime context without losing Session
identity, transcript, working directory, Inputs, Turns, or product links.

Mohist normally reuses the current Runtime Session. A physical Session changes
only at an explicit context boundary:

- **Reset** starts empty Runtime context while preserving AgentSession.
- **Runtime change or rebind** replaces the physical Binding on the same Runner
  while the Session is safely idle.
- **Handoff** moves the Session to another Runner and is the only operation that
  changes `runnerId`.
- **Confirmed-missing recovery** replaces a physical Session only after the same
  Runner proves it is absent and no accepted work or side effect is uncertain.
- **Force-reset** establishes a new context after unresolved work with explicit
  risk acknowledgement.

A timeout, disconnect, permission error, or unavailable Runner does not prove
that a Runtime Session is missing. Mohist keeps the association, blocks new
work, and asks the user to query or reconcile the original operation. It does
not automatically rebind or replay uncertain work.

After replacement, later work starts with empty Runtime context. The transcript
retains earlier content for audit but Mohist does not replay it into the new
Runtime Session. Old unresolved facts remain visible and do not become current
activity.

See [Agent execution design](../design/agent-execution.md#runtime-session-missing-recovery)
and [Action Contracts](actions/README.md#shared-semantics-for-agent-execution-actions).

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

See the [CLI Reference](cli-reference.md#agent-agentjob-and-session) for exact
arguments and operation keys.

## AgentSession Operations

- **Follow-up** continues the conversation and creates no AgentJob.
- **Compact** reduces Runtime context without changing AgentSession or its
  current Runtime Session.
- **Reset** starts empty Runtime context and records a context boundary.
- **Stop** ends queued or active work for one Turn or a Session tree. Unconfirmed
  targets remain `unknown`.
- **Force-reset** starts new context after an unresolved `unknown` with explicit
  risk acknowledgement.

These operations change Session execution, not work ownership.

## Current Scope

Every entry point uses the unified AgentJob path. A Workflow task names a
Mohist Agent through `mohist/agent`; the Agent definition selects OpenCode or Pi
and the accepted snapshot fixes that backend to the AgentJob. Max concurrent
runs applies to launches and Follow-ups. See [Agent Event Routing](event-routing.md)
for Agent responses to matching events.

## Implementation Gaps

- Confirmed-missing recovery is not uniform for safely idle AgentJob Input and
  idle Follow-up. Non-idle reconnect reconciliation can replace a Binding
  without proving that an earlier effect is absent.
- Force-reset, Runtime rebind, and Runner handoff have no public CLI, Web, or
  API operation. Public recovery remains limited to Compact and Reset.
- Compact and Reset currently generate hidden operation keys, so clients cannot
  reliably retry them after a lost response. The product contract requires
  caller-visible keys for these and other Session operations.
- Agent Connection Readiness checks only Model and Runtime. Complete Runner and
  Runtime executability probing remains unavailable, so a launch can find more
  gaps.
- Not every entry point exposes acceptance, dispatch, and Turn result as
  separate resumable facts after disconnection.
- The unified invocation interface lacks caller-owned duplicate-request
  protection on every operation and a uniform resumable read model for general
  external clients. See [`design/agent-api.md`](../design/agent-api.md).

---

Implementation source: `packages/server/src/Mohist.Server/Agent/` and
`packages/server/src/Mohist.Server/Sessions/`.
