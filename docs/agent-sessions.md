# Agents and AgentSessions

A Mohist Agent is independently configurable and usable within a Project. A
user can start it directly in the Web UI or CLI, connect the same Agent to
Slack, or let it respond to events and comment mentions. Entry points can
change, but the Agent identity, Instructions, execution configuration, Skills,
AgentJobs, and AgentSessions do not.

A third-party External Agent is a separate path. It uses the Mohist Skill and
`mo` to query, delegate to, or operate the execution layer and is not a Mohist
resource. It creates a Mohist Agent's AgentJob and AgentSession only when it
explicitly starts that Mohist Agent. See [Core Concepts](concepts.md) for the
complete product boundary.

## Product Commitments

- **An Agent works independently first**: Users can fully configure and start
  an Agent, continue its conversation, read results, and handle exceptions
  without Slack or another external connection.
- **Configuration has one owner**: The Mohist Agent owns its Instructions,
  execution backend, Model, Reasoning Effort, Variant, Skills, and concurrency
  limit. Its name, avatar, and description form the same Agent identity. The Web UI, CLI, and
  Agent Connections cannot store or override another definition.
- **An entry point does not change semantics**: A new delegation creates an
  AgentJob, AgentSession, first SessionInput, and first AgentTurn. Continuing an
  existing session creates a new SessionInput but not a second AgentJob.
- **Execution state is traceable**: An AgentJob answers whether the first launch
  succeeded. An AgentSession records what happened, the result of each later
  input, and whether it can currently continue. A Slack message or Web page is
  not the state arbiter.

## Concept Layers

- **Mohist Agent**: A predefined Agent resource reused by name within a
  Project and by Workflow tasks. Has a stable Agent ID, name, Instructions,
  configuration, Skills, and state.
- **Agent Connection**: Exposes one Mohist Agent in an external interaction
  location such as Slack. Has an independent connection lifecycle; references
  the Agent but neither owns nor copies its configuration.
- **AgentJob**: One launch execution of a Mohist Agent. Independently records
  waiting, execution, completion or failure, and the first execution result.
- **SessionInput**: One input accepted by an AgentSession. Has a stable Input
  ID; records content, attachments, source, order, and delivery state; one
  Turn can process multiple Inputs.
- **AgentTurn**: Continuous Runtime processing of an ordered set of
  SessionInputs. Has a stable Turn ID and state; is owned by an AgentSession
  and is not new top-level work.
- **AgentSession**: A continuing session recorded by Mohist. Has a stable
  Session ID; owns Inputs and Turns in order and retains context, usage,
  Activity, and the current Runtime Session.
- **Runtime Session**: The physical session maintained by an execution backend
  such as OpenCode or Pi. Identified by the execution backend; can be replaced
  by the AgentSession when necessary.

An Action is the Agent-to-Runner execution contract. It carries the accepted
Agent execution snapshot to a backend such as OpenCode or Pi, but it has no
Agent identity and owns no work lifecycle.

## One Invocation Path

Every entry point starts a real Mohist Agent through one canonical launch
boundary. This includes a Workflow task, Web, CLI, an Agent Connection, event
routing, and a comment mention. Each launch creates an AgentJob, and AgentJob is
the sole owner of execution state, result, retry, and recovery.

A Workflow Profile names the Mohist Agent used by an executable task. Workflow
supplies task input and attribution, then consumes the AgentJob result to decide
stage advancement. It does not select `mohist/opencode` or `mohist/pi`, snapshot
Agent configuration, or create anonymous Agent capability. Runtime and model
selection remain in the Agent definition.

Built-in Profiles use built-in Mohist Agents so a new Project can run without
manual Agent creation. A Project Profile may name another ready Agent. A
missing, archived, or not-ready Agent fails launch explicitly; Mohist does not
fall back to a Runtime-specific Workflow Action.

## Mohist Agent

A Mohist Agent is a first-class resource in a Project. It stores:

- A stable ID and a recognizable identity consisting of a name, avatar, and
  description
- Instructions and Agent configuration
- Skills
- A concurrency limit and `active` or `archived` state

## Configure an Agent

- **Name**: how the Agent is identified in the Project and external
  locations. Unique within the Project; renaming does not change the Agent ID.
- **Avatar**: how the Agent is recognized quickly in the Web UI, Slack, and
  execution records. Updates Mohist presentation immediately and synchronizes
  to connections that support updates.
- **Description**: when this Agent should be selected. Used only for
  discovery and selection; not included in execution Instructions.
- **Instructions**: what role the Agent has, how it works, and when it stops.
  Fixed when each new AgentJob starts.
- **Runtime**: which execution backend runs the Agent. Owned by the Agent; an
  ordinary client cannot override it for one request.
- **Model / Reasoning Effort / Variant**: which model, canonical reasoning
  effort, and true model variant the Agent uses. The Model and Variant fall
  back to the Project default execution configuration when absent. Reasoning
  Effort remains independent and, when absent, uses Runtime behavior.
- **Skills**: which capability descriptions load at startup. Fixed with the
  AgentJob; an entry point cannot add or remove them for one request.
- **Max concurrent runs**: how many executions this Agent can run at once,
  including launches and follow-ups. Applies to subsequent scheduling
  immediately; lowering it does not stop running executions, and excess work
  queues.
- **State**: whether the Agent can accept new delegations. An archived Agent
  rejects new delegations; existing Sessions remain readable and can
  continue.

Configure model providers and Runtime credentials in protected Runtime settings.
Do not put them in Instructions or copy them to an Agent or Agent Connection.
An Agent references a Runtime, Model, optional Reasoning Effort, and optional true
Variant. Reasoning Effort uses `off`, `minimal`, `low`, `medium`, `high`,
`xhigh`, or `max`; it is never encoded as a Variant. Readiness summarizes
whether those references can currently execute and directs a missing credential
to the single settings entry point. OpenCode does not support an explicit
Reasoning Effort; choose Pi or leave the effort unset for OpenCode.

### Project Default Execution Configuration

A Project can hold one default execution configuration: a Runtime, a Model,
and an optional Variant. It states what tasks in the Project run on when an
entry point does not supply an execution configuration and the Agent
definition leaves a field unset. Each execution field resolves by one
precedence rule:

1. The caller-supplied value, when an entry point accepts one;
2. The Agent definition's value;
3. The Project default.

A Runtime that resolves from no source defaults to `opencode` under the
existing rule. An explicitly malformed value is never masked by a
lower-precedence source: an unsupported Runtime or a Model outside the
`provider/model` form remains a configuration gap and blocks launch even
when a Project default is configured.

Configure the default through the Project settings surface; the route
contract is in
[External Agent API](agent-api.md#project-default-execution-configuration).
Setting a new default replaces the previous one; an invalid default is
rejected and leaves the previous default untouched. The default resolves at launch, so each AgentJob stores
the configuration it launched with and later default changes never change
an in-flight or completed execution.

With a default configured, an Agent without a Model (or with a Variant but
no Model) is no longer structurally `needs-setup`: Readiness reports `ready` or
`unknown`, and a launch dispatches with the model the default resolved. Without
a default, the gap remains `needs-setup` with its actionable repair. Removing
or changing the default re-introduces or re-resolves the gap accordingly,
but a Readiness conclusion confirmed by a completed execution is not flipped
by a default change alone.

A delegation can include context references such as an Issue, Epic, or
Repository, but context is not Agent configuration. An ordinary client can
provide only task text and context. It cannot override the execution definition
or concurrency limit. The Agent definition is fixed when a launch or Workflow
Agent task attempt starts, as are the Skills loaded for that execution. An Agent
tested in the Web UI is therefore still the same Agent after it connects to
Slack.

Name, avatar, and description form the presentation identity. Edits apply
immediately to discovery and presentation in Mohist. Agent Connections
asynchronously synchronize external identities that support updates and show
an explicit out-of-sync state. Instructions, Runtime, Model, Reasoning Effort,
Variant, and Skills form the execution definition and affect only later
AgentJobs. Each AgentJob
stores its execution snapshot at launch. Follow-ups in an existing AgentSession
continue with the configuration and context established for that session; an
Agent edit does not silently change its model or capabilities. Max concurrent
runs is the Agent's current scheduling policy. Every Session queues its next
execution under the latest value, but changing it does not change any Session's
execution definition.

A Workflow task launches the named Agent through the same boundary and fixes the
complete Agent definition when its AgentJob starts. Editing the Agent does not
change an accepted Job. A Workflow retry is a new Agent launch and therefore
uses the definition that exists when the new AgentJob is accepted.

## Readiness and Availability

An Agent's `active` or `archived` state answers only whether it accepts new
delegations. Readiness answers whether Mohist can currently confirm that the
Agent execution configuration is complete:

Readiness has three values. `ready` means Mohist confirmed that the current
definition can execute; test or launch the Agent. `needs-setup` means Mohist
confirmed a configuration gap; launch is blocked, so inspect each gap and its
repair entry point. `unknown` means Mohist cannot currently confirm whether the
definition can execute; submit and wait for validation, but do not claim that
the Agent is available.

A temporarily offline Runner or lack of capacity is Availability, not a reason
to change a `ready` Agent to `needs-setup`. Work can be accepted and queued. The
Web UI, CLI, and Agent Connections present the unified Mohist conclusion and do
not maintain separate Runtime judgment rules.

Structural gaps resolve Model and Variant by Agent definition, then Project
default. When a configured Project default resolves a missing Model or a
Variant set without a Model, those gaps do not appear and the conclusion
follows the existing history rules (`ready` or `unknown`). Definition errors — an
unsupported Runtime or a Model outside the `provider/model` form — remain gaps
regardless of any Project default.

Availability states whether a new execution can start now. After a Runner or
capacity recovers, a queued AgentJob can briefly show "waiting for scheduling"
until its next scheduling attempt starts. This is not a new configuration gap
and does not mean that the Runner is offline again.

### Configure and Test in the Web UI

In **Agents**, create or open an Agent and enter its name, avatar,
description, and Instructions. Select a Runtime; the page shows only the
Model, catalog-backed Reasoning Effort, true Variant, and credential
requirements that Runtime supports. Then select Skills and a concurrency
limit. The page shows Readiness and every gap. When Readiness is `ready`, use
**Start session** to submit a real task. You can also submit when it is
`unknown`, but the page states that the task will wait for Runner validation.
Open the AgentSession after successful creation, inspect replies and execution
facts, and use a follow-up to verify a continuing conversation. After the
Agent can complete its goal independently, configure event routing or an Agent
Connection.

### Configure and Use in the CLI

```bash
mo agent create --name explorer --description "Explore product needs" --instructions "Clarify the request, identify missing decisions, and produce actionable issues." --runtime opencode --skills mohist,mohist-explore --max-concurrent-runs 1
mo agent view explorer
mo agent launch explorer --prompt "Explore a product design for invoking a Mohist Agent from Slack"
# After response loss, retry with the key printed before launch. Do not create a new launch.
mo agent launch explorer --prompt "Explore a product design for invoking a Mohist Agent from Slack" --idempotency-key <key>
```

`agent view` shows Readiness, Availability, and configuration gaps. When the
Agent is `needs-setup`, repair each listed gap before launch. `agent launch` returns
the AgentJob ID, AgentSession ID, first Input ID, and Turn ID. Read the first
launch result and composite observation from the returned observation URL. Use
`mo session followup` to submit a new SessionInput in a continuing conversation,
and use `mo session transcript` for the complete record. Continue observing
while the state is `accepted`, `queued`, or `running`; read the result or
transcript at `terminal`; for `unknown`, read or retry with the original key.
The CLI and Web UI invoke the same product capabilities.

## Launch Entry Points

A task-first launch is available when the caller has a task but does not yet
need to configure an Agent;
[External Agent API](agent-api.md#task-first-launch) defines the route and its
replay contract. The Server derives missing definition fields, materializes
the resolved execution configuration, creates the Agent, and then uses the
canonical AgentJob and AgentSession launch pipeline.

- The Web UI starts with a task and optional context. It creates a derived
  Agent, AgentJob, AgentSession, first Input, and first Turn, then opens the
  session page.
- The CLI starts the same way with `mo agent start --prompt <task>`. It
  creates the derived Agent and returns the same AgentJob, AgentSession,
  first Input, and first Turn identities.
- Selecting or naming an existing Agent in the Web UI or CLI uses the
  unchanged definition-first launch path.
- An Agent Connection delivers the first task in a Slack direct message, an
  explicit New task, or a new root mention in a channel to the connected
  Agent without changing Agent configuration.
- Event routing creates an AgentJob and AgentSession for a matching event and
  response prompt.
- An Issue comment mention uses the comment content after `@<agent-name>` as
  the task and associates its Issue context.

A mention uses the comment body as the input and automatically includes the
Issue context. It is one-time work, suitable for a request such as "@my-agent,
supervise and advance this Issue." For continuous attention, the Agent adds the
Issue to its watch list with `mo issue watch add`. Every launch entry point
creates an AgentJob and fixes the Agent Instructions and configuration for that
work. Later Agent edits do not change work that has started.

A Mohist Agent's central role is proxy. It occupies a production-line position
that the owner could occupy and acts through the same commands and Approval
channel as a person. One Mohist Agent can have multiple AgentJobs and multiple
AgentSessions.

A Mohist Agent can also spawn child sessions for other Agents from its own
session. It can decompose work whose shape becomes clear only at runtime and
form a session tree. See [Subagents and Session Trees](subagents.md).

See [Slack](slack.md) for thread and permission rules when connecting an Agent
to Slack.

## Why Work and Conversation Are Separate

One Agent launch creates both work and a place to continue the conversation,
but those have different lifetimes. Mohist keeps them separate so that a clear
work result does not close a useful conversation, and a later follow-up does not
rewrite the result of the original delegation.

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

- **AgentJob** answers whether the first delegation completed, failed, was
  rejected, was cancelled, or became blocked. It eventually reaches a result.
- **AgentSession** answers what happened across the continuing conversation and
  whether it can accept more input. It remains after a Turn ends and has no
  completed, failed, or closed lifecycle.
- **SessionInput** records whether one input was accepted and where it belongs
  in the conversation. **AgentTurn** records one continuous period of Runtime
  processing and its result. Both remain children of the AgentSession.

The first AgentTurn supplies the AgentJob result. Later Turns do not modify that
result. A successful AgentJob therefore means that the Runtime processed the
first delegation; it does not mean that every broad goal in the conversation is
finished. The user can continue the same AgentSession after the AgentJob ends.

### Execution Context Privacy

Session lists and summaries, and any Agent history projection, identify context
with the Issue, Epic, repository, and named Workspace when those facts exist.
They never expose a filesystem `workspacePath`. That path remains an internal
launch and recovery fact; users navigate with the named Workspace rather than a
materialization location.

A Workflow uses the same AgentJob and AgentSession model as every other entry
point. AgentJob owns the execution result; WorkflowRun consumes that result and
decides advancement. AgentSession records execution evidence and does not
advance a Workflow. Business work that must reach Done belongs in an Issue and
Workflow, not in a never-ending conversation.

## Continuing a Session

Every accepted follow-up gets one stable Input identity. When the Runtime can
accept input during the current running Turn, the follow-up joins that Turn.
Otherwise, it starts or waits for a later Turn. Neither path creates another
AgentJob or changes the first launch result.

A Runtime activity event without a Turn identity can settle only a follow-up
that has already started executing. It cannot complete a queued follow-up that
has not been dispatched. When an initial launch reaches terminal while a
follow-up waits, Mohist keeps the follow-up queued and dispatches it next. An
AgentJob terminal close belongs to the launch and never settles a later
follow-up, including when that close is replayed after the follow-up starts.

Accepted input is never discarded or changed to rejected because execution
later fails. When the waiting queue is full, Mohist rejects the new input before
creating a live Input or Turn. Retrying that rejected intent with the same key
returns the same rejection; use a new key after capacity returns. When Mohist
cannot confirm whether an input or side effect was accepted, it reports Unknown
and does not create another Turn as a guess.

Session content remains in occurrence order. The Web UI and CLI show Input
acceptance separately from Turn execution and result, because "the message was
accepted" and "the Agent completed it" are different facts. See
[Session timeline design](../design/session-timeline.md) for how those facts are
presented without losing the underlying record.

## Why Unknown Fails Closed

A lost response can hide either success or failure. Automatically repeating the
request could therefore launch duplicate work, submit the same input twice, or
repeat an external side effect. Mohist treats uncertainty as a state to
reconcile, not as permission to retry with a new identity.

Launches, follow-ups, and session operations that can have side effects use a
caller-visible idempotency key. The user-visible contract is:

- Retry the same intent with the same key after response loss. Mohist returns or
  continues the original result instead of creating duplicate work.
- Reusing a key with different content is rejected. Use a new key only for a
  genuinely new intent.
- Mohist must not hide a generated replacement key from the caller. A request
  without a required key is rejected before acceptance.
- Querying the original operation does not repeat its side effect.

The Server is the authority for these facts. Entry points cannot infer success
from local logs, an HTTP response, a Runner event, or a provider response alone.
They present the same result and next action for each Activity value. `idle`
means no Turn or session operation is in progress: a follow-up can start a new
Turn, and Compact and Reset are available. `active` means a Turn is queued,
executing, or waiting for a confirmed result: keep Inputs in order and request
Stop for queued or running work. `unknown` means Mohist cannot confirm input
acceptance, a Runtime side effect, binding, or result: block new work and query
the original operation or reconcile it manually.

Stop is the single operation for ending work. For a queued Turn, it settles
locally without contacting the Runtime and records the Turn as cancelled. For a
running Turn, it requests interruption from the Runtime, and Mohist records the
Turn as cancelled only after the stop is confirmed. A lost or uncertain Stop
response leaves the Turn and Session Unknown; it never turns them into Idle by
assumption.

The target contract uses an explicit force-reset when reconciliation cannot resolve an
old Unknown. It requires the user to acknowledge that the old Runtime may still
produce side effects. It preserves the unresolved Input, Turn, and operation in
the audit record, starts a new context, and permits new work only after that new
context is established. Retry a lost force-reset response with its original
operation key.

See [External Agent API idempotency](../design/agent-api.md#normalized-fingerprint-and-idempotency)
and its [public projection](../design/agent-api.md#public-execution-projection) for direct caller
contracts, and
[Agent execution design](../design/agent-execution.md#work-lifecycle-and-session)
for transaction, dispatch, retry, and fencing details.

## Why Logical and Physical Sessions Are Separate

An AgentSession is Mohist's stable logical identity and audit record. A Runtime
Session is the physical conversation held by OpenCode, Pi, or another backend.
Keeping these identities separate lets Mohist replace lost or deliberately
reset Runtime context without losing the Session origin, transcript, working
directory, Inputs, Turns, or links from other product records.

Mohist normally reuses the current Runtime Session. A task change, retry, Model
edit, Compact, completed Turn, disconnect, or Runner restart does not replace it.
The physical session can change only at an explicit context boundary:

- **Reset** deliberately starts empty Runtime context while preserving the
  AgentSession and its recorded conversation.
- **Runtime change or rebind** replaces the physical binding on the same Runner
  only while the Session is safely idle.
- **Handoff** is the only operation that can move the Session to another Runner.
  It also requires a safely idle Session. Reconnects, timeouts, and old events
  cannot imply a handoff.
- **Confirmed-missing recovery** can replace a physical session only when the
  same Runner proves that it is absent and no accepted work or side effect is
  uncertain.
- **Force-reset** can establish a new context after unresolved work, but only
  with explicit risk acknowledgement.

A timeout, disconnect, permission error, unavailable Runner, or other
inconclusive observation does not prove that a Runtime Session is missing.
Mohist keeps the existing association, blocks new work, and asks the user to
query or reconcile the original operation. It does not automatically rebind or
replay uncertain work.

After a confirmed replacement, later work starts with empty Runtime context.
The transcript records a context boundary and retains earlier content for audit,
but Mohist does not replay that content into the new Runtime Session. Old
unresolved facts remain visible and do not masquerade as current activity.

See
[Agent execution design](../design/agent-execution.md#runtime-session-missing-recovery)
for replacement and recovery protocols and
[Shared Semantics for Agent Execution Actions](actions/README.md#shared-semantics-for-agent-execution-actions)
for execution-backend reuse boundaries.

## AgentSession Origin and Addressing

Each AgentSession has exactly one immutable Agent Origin. A Workflow, Web, CLI,
Agent Connection, event route, or mention is launch attribution for that same
Agent; it is not another Origin or work owner. A Workflow launch additionally
records WorkflowRun, stage, task, and attempt attribution.

Origin never changes. Matching Models, prompts, Runtime configuration, or
Workflow attribution do not merge Sessions, and replacing a Runtime Session
does not change Origin.

Every Origin uses the same top-level `mo session` surface:

- `mo session view <session-id>` and
  `mo session transcript <session-id>` read by stable Session ID.
- `mo session followup`, `compact`, `reset`, and `stop` act on the
  Session rather than on a separate Origin-specific resource.
- `mo session list` can discover Sessions by Agent, Issue, or WorkflowRun.

See the [CLI Reference](cli-reference.md#agent-agentjob-and-session) for exact
arguments and operation keys.

## AgentSession Operations

- **Follow-up** continues the same conversation. It creates one Input, joins a
  running Turn when supported or starts or queues a later Turn, and creates no
  AgentJob.
- **Compact** reduces Runtime context without starting over. It preserves the
  AgentSession and current Runtime Session.
- **Reset** continues from empty Runtime context. It preserves AgentSession
  identity and transcript and records the context boundary.
- **Stop** ends queued or active work for one Turn or a session tree. Queued
  Turns end locally. Executing Turns are cancelled only after Runtime
  confirmation; unconfirmed targets remain Unknown.
- **Force-reset** is the target recovery for an Unknown that cannot reconcile.
  It preserves unresolved history and starts new context only after explicit
  risk acknowledgement.

These operations change session execution, not work ownership. A follow-up does
not create another AgentJob. Compact, Reset, and force-reset do not launch the
Mohist Agent again.

## Current Scope

The unified AgentJob path is implemented for every entry point. Direct Agent
launch from Web, CLI, connections, events, mentions, and Workflow tasks all
create AgentJob. A Workflow task names a Mohist Agent through `mohist/agent`;
the launch boundary resolves the named Agent and creates a real AgentJob and
AgentSession. A Mohist Agent selects OpenCode or Pi
through its configuration, and the accepted snapshot fixes that backend to the
AgentJob. Max concurrent runs applies to launches and follow-ups. See
[Agent Event Routing](event-routing.md) for Mohist Agent event responses.

## Implementation Gaps

Automatic confirmed-missing recovery is implemented for a new Workflow-origin
input when the Session is safely idle. The owning Runner creates empty Runtime context
and replaces the missing binding without changing the AgentSession or replaying
prior input. Ambiguous or unsafe absence still blocks because it cannot prove
that an old effect did not occur.

Slack DM continuation queues behind an initial AgentJob that has not bound a
Runtime yet. If the initial Turn instead reached a retry-safe terminal failure,
Slack enters the durable Agent retry path, moves its conversation mapping to the
resolved replacement Session, and accepts the triggering message there exactly
once. This recovers a definitely failed launch; it is not permission to replay
an active or unknown effect.

Pre-execution Skill resolution is part of that retry-safe boundary. A missing
Skill fails before a Runtime Session or model turn starts, so a later ordinary
DM may resume through the durable replacement path after the Skill becomes
available. It never requires the user to start a new task.

Queued Follow-up now enters the same physical Runtime missing-binding recovery
boundary before its input is submitted. Generic AgentJob launch does not yet
enter that boundary. Reconnect reconciliation can also replace a binding for
non-idle work without the complete proof that an old effect is absent. Callers
must therefore not treat those paths as permission to replay input.

Recovery does not yet prove the active owner and absence of an earlier effect
at every boundary. This limits convergence across Runner handoff without making
confirmed-missing recovery itself an unimplemented capability.

Force-reset, Runtime rebind, and Runner handoff are target recovery boundaries,
but they have no public CLI, Web, or API operation today. A user cannot yet use
them to escape an unresolved Unknown. Current public recovery is limited to
Compact and Reset, and Reset is safe only when no old side effect remains
uncertain.

### Caller-owned operation keys

The product contract requires a caller-visible key for follow-up, Compact,
Reset, recovery, handoff, rebind, and force-reset. Compact and Reset currently
generate a hidden key. Clients cannot reliably retry those operations after a
lost response. Stop already requires a caller-visible idempotency key. For a
single Turn the key identifies that one stop intent; for a session tree, Server
derives the tree operation identity from the root Session and that key.

Agent Connection Readiness currently checks only whether the Agent has a Model
and Runtime while keeping Connection health independent. An Agent that has not
been probed defaults to Unknown. Complete Runner and Runtime executability
probing remains future work, so a real launch can still find additional gaps.

SessionInput and AgentTurn are durable child records, and launch and follow-up
return their stable IDs. The remaining gap is a uniform canonical read model:
not every entry point yet exposes acceptance, dispatch, and Turn result as
separate resumable facts after disconnection.

Slack Agent Connections, Bot identities, access policies, reactive
Configuration-token rotation, and Agent-authored reply actions are implemented.
The unified invocation interface still lacks caller-owned duplicate-request
protection on every operation and a uniform resumable read model for general
external clients. See the target contracts in
[`design/agent-api.md`](../design/agent-api.md) and
[`design/slack.md`](../design/slack.md).
