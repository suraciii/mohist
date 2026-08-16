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
  execution backend, Model, Variant, Skills, and concurrency limit. Its name,
  avatar, and description form the same Agent identity. The Web UI, CLI, and
  Agent Connections cannot store or override another definition.
- **An entry point does not change semantics**: A new delegation creates an
  AgentJob, AgentSession, first SessionInput, and first AgentTurn. Continuing an
  existing session creates a new SessionInput but not a second AgentJob.
- **Execution state is traceable**: An AgentJob answers whether the first launch
  succeeded. An AgentSession records what happened, the result of each later
  input, and whether it can currently continue. A Slack message or Web page is
  not the state arbiter.

## Concept Layers

| Concept | Definition | Identity and lifecycle |
|---|---|---|
| Inline Agent | A use of Agent capability configured and invoked directly by a Workflow | Not a resource and has no Agent ID; its configuration exists in task input |
| Agent Definition Reference | A use in which a Workflow task references a Mohist Agent definition with `uses: mohist/agent` | Not a resource and has no Agent ID; its definition is fixed when task execution starts |
| Mohist Agent | A predefined Agent resource reused by name within a Project | Has a stable Agent ID, name, Instructions, configuration, Skills, and state |
| Agent Connection | Exposes one Mohist Agent in an external interaction location such as Slack | Has an independent connection lifecycle; references the Agent but neither owns nor copies its configuration |
| AgentJob | One launch execution of a Mohist Agent | Independently records waiting, execution, completion or failure, and the first execution result |
| SessionInput | One input accepted by an AgentSession | Has a stable Input ID; records content, attachments, source, order, and delivery state; one Turn can process multiple Inputs |
| AgentTurn | Continuous Runtime processing of an ordered set of SessionInputs | Has a stable Turn ID and state; is owned by an AgentSession and is not new top-level work |
| AgentSession | A continuing session recorded by Mohist | Has a stable Session ID; owns Inputs and Turns in order and retains context, usage, Activity, and the current Runtime Session |
| Runtime Session | The physical session maintained by an execution backend such as OpenCode or Pi | Identified by the execution backend; can be replaced by the AgentSession when necessary |

An Action is not in the Agent resource layer. `mohist/opencode` describes how
one unit of work is delegated to OpenCode. It does not represent an Agent with
an identity.

## Two Invocation Paths

| Path | Agent identity | Work owner | Execution | AgentSession Origin |
|---|---|---|---|---|
| Inline Workflow invocation | No; task input selects the capability | TaskRun | An execution-backend Action (`mohist/opencode` or `mohist/pi`) | Workflow |
| Workflow Agent delegation | Yes; resolves a stored Mohist Agent | AgentJob for execution, TaskRun for Workflow completion | The AgentJob's internal execution entry point, with Workflow terminal finalization | Workflow |
| Direct Mohist Agent launch | Yes; uses a stored Mohist Agent | AgentJob | The Mohist Agent's internal execution entry point | Agent launch |

All paths can reuse the same execution-backend capability and AgentSession
model, but they do not share work ownership. `mohist/agent` is the Workflow
Agent delegation path: it freezes the Agent definition, mints one Job/Session/
Input/Turn identity, and exposes that identity on both read surfaces. The
AgentJob owns admission, Runner claim, execution, and its terminal result. The
TaskRun owns `expect`, artifacts, variables, recovery, and Workflow advancement.
The terminal crosses that boundary through typed delivery, not the Workflow
task-report endpoint.

The stable Workflow invocation status is derived from authoritative records:
admitted but unclaimed, including permit or Runner waiting, is `queued`; a
claimed or `Unknown` non-terminal AgentJob is `executing`; terminal AgentJob
states map to `completed`, `failed`, or `cancelled`; a failed terminal with a
pending or applying Workflow recovery decision is `recovering`. The status is
not a mirror stored on the handoff plan and does not parse the transcript.

The Workflow Agent delegation is a BREAKING cutover for clients that assumed
`mohist/agent` was an inline TaskRun action. Clients must use the task's
`workflowInvocation` identifiers and terminal `result`. Direct Agent launches,
inline `mohist/opencode`/`mohist/pi` tasks, runtime adapters, and external
Agent API paths retain their existing identifiers and semantics.

## Inline Agent

An Inline Agent is a use mode, not a persistent entity. A Workflow task directly
declares:

- The execution-backend Action, such as `mohist/opencode`
- The prompt for this execution
- An optional Session name and model options

Use an Inline Agent for planning, implementation, review, and repair in a
Workflow. It has no name, Instructions, Skills, or Agent ID. An event-routing
rule cannot reference it, and a `mo agent` command cannot find it.

The Workflow TaskRun owns task success, failure, and output. The Action is the
execution interface. The AgentSession stores only session content and execution
facts.

## Agent Definition Reference

A task can set `uses: mohist/agent` and provide a `name` to delegate to a
predefined Mohist Agent's Instructions and execution configuration. It is not
an Inline Agent: the Agent resource supplies the frozen execution definition.
It is also not a direct Mohist Agent launch: the Session origin remains
Workflow and the TaskRun remains the owner of Workflow completion effects. The
AgentJob owns the delegated execution itself. See the
[`mohist/agent` Action](actions/agent.md) contract.

## Mohist Agent

A Mohist Agent is a first-class resource in a Project. It stores:

- A stable ID and a recognizable identity consisting of a name, avatar, and
  description
- Instructions and Agent configuration
- Skills
- A concurrency limit and `active` or `archived` state

## Configure an Agent

| Setting | User question | Effective rule |
|---|---|---|
| Name | How is the Agent identified in the Project and external locations? | Unique within the Project; renaming does not change the Agent ID |
| Avatar | How is the Agent recognized quickly in the Web UI, Slack, and execution records? | Updates Mohist presentation immediately and synchronizes to connections that support updates |
| Description | When should this Agent be selected? | Used only for discovery and selection; not included in execution Instructions |
| Instructions | What role does the Agent have, how does it work, and when does it stop? | Fixed when each new AgentJob starts |
| Runtime | Which execution backend runs the Agent? | Owned by the Agent; an ordinary client cannot override it for one request |
| Model / Variant | Which model and reasoning level does the Agent use? | Owned by the Agent; uses the Runtime default when not configured |
| Skills | Which capability descriptions load at startup? | Fixed with the AgentJob; an entry point cannot add or remove them for one request |
| Max concurrent runs | How many executions can this Agent run at once, including launches and follow-ups? | Applies to subsequent scheduling immediately; lowering it does not stop running executions, and excess work queues |
| State | Can the Agent accept new delegations? | An archived Agent rejects new delegations; existing Sessions remain readable and can continue |

Configure model providers and Runtime credentials in protected Runtime settings.
Do not put them in Instructions or copy them to an Agent or Agent Connection.
An Agent references only a Runtime, Model, and Variant. Readiness summarizes
whether those references can currently execute and directs a missing credential
to the single settings entry point.

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
an explicit out-of-sync state. Instructions, Runtime, Model, Variant, and Skills
form the execution definition and affect only later AgentJobs. Each AgentJob
stores its execution snapshot at launch. Follow-ups in an existing AgentSession
continue with the configuration and context established for that session; an
Agent edit does not silently change its model or capabilities. Max concurrent
runs is the Agent's current scheduling policy. Every Session queues its next
execution under the latest value, but changing it does not change any Session's
execution definition.

A Workflow `mohist/agent` task also fixes the complete Agent definition when
each attempt starts. Editing the Agent does not change an already dispatched
attempt. A retry reads the definition again when it starts, so only a new retry
uses repaired Runtime, Model, Variant, Instructions, or Skills.

## Readiness and Availability

An Agent's `active` or `archived` state answers only whether it accepts new
delegations. Readiness answers whether Mohist can currently confirm that the
Agent execution configuration is complete:

| Readiness | Meaning | User action |
|---|---|---|
| Ready | Mohist confirmed that the current definition can execute | Test or launch the Agent |
| Needs setup | Mohist confirmed a configuration gap | Launch is blocked; inspect each gap and its repair entry point |
| Unknown | Mohist cannot currently confirm whether the definition can execute | Submit and wait for validation, but do not claim that the Agent is available |

A temporarily offline Runner or lack of capacity is Availability, not a reason
to change a Ready Agent to Needs setup. Work can be accepted and queued. The
Web UI, CLI, and Agent Connections present the unified Mohist conclusion and do
not maintain separate Runtime judgment rules.

Availability states whether a new execution can start now. After a Runner or
capacity recovers, a queued AgentJob can briefly show "waiting for scheduling"
until its next scheduling attempt starts. This is not a new configuration gap
and does not mean that the Runner is offline again.

### Configure and Test in the Web UI

1. In **Agents**, create or open an Agent and enter its name, avatar,
   description, and Instructions.
2. Select a Runtime. Show only the Model, Variant, and credential requirements
   that Runtime supports. Then select Skills and a concurrency limit. The page
   must show Readiness and every gap.
3. When Readiness is Ready, use **Start session** to submit a real task. You can
   also submit when it is Unknown, but the page must state that the task will
   wait for Runner validation. Open the AgentSession after successful creation.
4. In the Session, inspect replies and execution facts. Use a follow-up to
   verify a continuing conversation.
5. After the Agent can complete its goal independently, configure event routing
   or an Agent Connection.

### Configure and Use in the CLI

```bash
mo agent create --name explorer --description "Explore product needs" --instructions "Clarify the request, identify missing decisions, and produce actionable issues." --runtime opencode --skills mohist,mohist-explore --max-concurrent-runs 1
mo agent view explorer
mo agent launch explorer --prompt "Explore a product design for invoking a Mohist Agent from Slack"
# After response loss, retry with the key printed before launch. Do not create a new launch.
mo agent launch explorer --prompt "Explore a product design for invoking a Mohist Agent from Slack" --idempotency-key <key>
```

`agent view` shows Readiness, Availability, and configuration gaps. When the
Agent Needs setup, repair each listed gap before launch. `agent launch` returns
the AgentJob ID, AgentSession ID, first Input ID, and Turn ID. Read the first
launch result and composite observation from the returned observation URL. Use
`mo session followup` to submit a new SessionInput in a continuing conversation,
and use `mo session transcript` for the complete record. Continue observing
`pending`, `queued`, and `executing` states. Read the result or transcript in a
terminal state. For Unknown, read or retry with the original key. The CLI and
Web UI invoke the same product capabilities.

## Launch Entry Points

| Entry point | New delegation | Mohist behavior |
|---|---|---|
| Web UI | Select an Agent and enter a task with optional context | Creates an AgentJob, AgentSession, first Input, and first Turn, then opens the session page |
| CLI | `mo agent launch <agent>` | Creates the same AgentJob, AgentSession, first Input, and first Turn and returns their IDs |
| Agent Connection | The first task in a Slack direct message, an explicit New task, or a new root mention in a channel | Delivers the message to the connected Agent without changing Agent configuration |
| Event routing | A matching event and response prompt | Creates an AgentJob and AgentSession for the event |
| Issue comment mention | Comment content after `@<agent-name>` | Uses the comment as the task and associates its Issue context |

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
[Mohist Agent]
      |
      +-- launch --> [AgentJob: result of this delegation]
                          |
                          +--> [AgentSession: continuing record]
                                    |
                                    +-- [Input 1] --+
                                    +-- [Input 2] --+--> [Turn 1]
                                    +-- [Input 3] ------> [Turn 2]
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

A Workflow uses the same AgentSession model, but its TaskRun owns the work
result. An AgentSession records execution evidence and does not advance a
Workflow. Business work that must reach Done belongs in an Issue and Workflow,
not in a never-ending conversation.

## Continuing a Session

Every accepted follow-up gets one stable Input identity. When the Runtime can
accept input during the current running Turn, the follow-up joins that Turn.
Otherwise, it starts or waits for a later Turn. Neither path creates another
AgentJob or changes the first launch result.

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
They present the same result and next action:

| Activity | Meaning | Safe behavior |
|---|---|---|
| Idle | No Turn or session operation is in progress | A follow-up can start a new Turn; Compact and Reset are available |
| Active | A Turn is queued, executing, or waiting for a confirmed result | Keep Inputs in order; request Stop for queued or running work |
| Unknown | Mohist cannot confirm input acceptance, a Runtime side effect, binding, or result | Block new work; query the original operation or reconcile it manually |

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

Each AgentSession has exactly one Origin:

- **Workflow Origin**: An inline Workflow task creates or continues the named
  Session. A `mohist/agent` delegation keeps the Workflow origin but mints an
  independent Session/Input/Turn set for every attempt; its `session` value is
  only a logical label.
- **Agent launch Origin**: Each Mohist Agent launch creates a Session associated
  with that Agent.
- **Agent Connection Origin**: An entry point such as Slack starts a Session for
  the same Mohist Agent. The Connection does not own another copy.

Origin never changes. Matching Models, prompts, or Runtime configuration do not
merge Sessions, and replacing a Runtime Session does not change Origin.

Every Origin uses the same top-level `mo session` surface:

- `mo session view <session-id>` and
  `mo session transcript <session-id>` read by stable Session ID.
- `mo session followup`, `compact`, `reset`, and `stop` act on the
  Session rather than on a separate Origin-specific resource.
- `mo session list` can discover Sessions by Agent, Issue, or WorkflowRun.

See the [CLI Reference](cli-reference.md#agent-agentjob-and-session) for exact
arguments and operation keys.

## AgentSession Operations

| Operation | Why use it | Visible guarantee |
|---|---|---|
| Follow-up | Continue the same conversation | Creates one Input, joins a running Turn when supported or starts or queues a later Turn, and creates no AgentJob |
| Compact | Reduce Runtime context without starting over | Preserves the AgentSession and current Runtime Session |
| Reset | Continue from empty Runtime context | Preserves AgentSession identity and transcript and records the context boundary |
| Stop | End queued or active work, one Turn or a session tree | Queued Turns end locally and are recorded cancelled; executing Turns are recorded cancelled only after Runtime confirmation; unconfirmed targets remain Unknown |
| Force-reset (target) | Continue after an Unknown that cannot be reconciled | Preserves unresolved history and starts a new context only after explicit risk acknowledgement |

These operations change session execution, not work ownership. A follow-up does
not turn a TaskRun into an AgentJob. Compact, Reset, and force-reset do not launch
the Mohist Agent again.

## Current Scope

The `mohist/opencode` and `mohist/pi` Workflow Actions are implemented; see
their Action documents for configuration. A Mohist Agent selects OpenCode or Pi
through its configuration, and the snapshot fixes that backend to the AgentJob.
The Web UI and CLI can create, edit, and launch a Mohist Agent and read and
continue an AgentSession. The `mohist/agent` Action delegates new Workflow
attempts through the same AgentJob admission boundary and exposes the stable
invocation identifiers/status/result contract described in
[Two Invocation Paths](#two-invocation-paths). Max concurrent runs is enforced
for launches and follow-ups. See [Agent Event Routing](event-routing.md) for
Mohist Agent event responses.

## Implementation Gaps

Automatic confirmed-missing recovery is implemented for a new Workflow input
when the Session is safely idle. The owning Runner creates empty Runtime context
and replaces the missing binding without changing the AgentSession or replaying
prior input. Ambiguous or unsafe absence still blocks because it cannot prove
that an old effect did not occur.

AgentJob launch and idle Follow-up do not yet enter that same recovery boundary.
The initial AgentJob Turn is already queued before missing-binding recovery can
run. Reconnect reconciliation can also replace a binding for non-idle work
without the complete proof that an old effect is absent. Callers must therefore
not treat those paths as permission to replay input.

Recovery does not yet apply the complete ownership lease, effect fence,
candidate reconciliation, and cleanup contract at every boundary. This limits
cross-boundary convergence without making confirmed-missing recovery itself an
unimplemented capability.

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
