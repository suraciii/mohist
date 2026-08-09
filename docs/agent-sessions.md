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
| Direct Workflow invocation | No; uses an Inline Agent or Agent Definition Reference | TaskRun | An execution-backend Action (`mohist/opencode`, `mohist/pi`) or `mohist/agent` | Workflow |
| Mohist Agent launch | Yes; uses a stored Mohist Agent | AgentJob | The Mohist Agent's internal execution entry point | Agent launch |

The paths can use the same execution-backend capability and AgentSession model,
but they do not share Agent identity or work lifecycle. A Workflow invokes
OpenCode or Pi through an execution-backend Action. An AgentJob executes a
Mohist Agent, reusing only the underlying execution-backend capability; it does
not invoke a Workflow Action in reverse.

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

A task can instead set `uses: mohist/agent` and provide a `name` to use a
predefined Mohist Agent's Instructions and execution configuration. This is not
an Inline Agent because the Instructions and configuration come from the Agent
resource rather than task input. It is also not a Mohist Agent launch because
it creates no AgentJob. The TaskRun owns success or failure, and the
AgentSession still has a Workflow Origin. See the
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

## AgentJob and AgentSession

An AgentJob, SessionInput, AgentTurn, and AgentSession can all exist after a
launch converges successfully, but they have different responsibilities:

| | AgentJob | SessionInput | AgentTurn | AgentSession |
|---|---|---|---|---|
| Question answered | Did this launch work succeed? | Was this input accepted, queued, or delivered to the Runtime? | How far did this processing period advance, and what was its result? | What happened in this session, and can it accept more input now? |
| Owns | Launch scheduling, success or failure, and work result | Input content, order, source, and delivery state | Input set, execution state, and corresponding reply | Input and Turn order, context, usage, Activity, and current Runtime Session |
| Lifecycle | One launch job that eventually completes, is rejected, fails, is cancelled, or becomes blocked | One input is accepted or explicitly rejected; dispatch of an accepted Input can be temporarily blocked and later reaches a terminal dispatch state | One continuous execution that eventually completes, fails, is cancelled, becomes blocked, or is temporarily unknown | Persists and can accept multiple Inputs |
| Parent concept | Mohist Agent work | Child record of an AgentSession | Child record of an AgentSession | Session record |

The corresponding Workflow work owner is a TaskRun, not an AgentJob. A TaskRun
or AgentJob decides the work result. An AgentSession records only execution
facts and neither advances the Workflow nor decides the AgentJob.

The first AgentTurn is associated with the AgentJob. Later AgentTurns do not
modify the AgentJob. AgentJob `completed` means that the Runtime processed the
launch work successfully. It does not close the conversation or guarantee
delivery of a broad goal described informally by the user. An Agent can ask a
question in its reply after the AgentJob completes and the AgentSession returns
to idle; the user then continues through a follow-up.

Each follow-up creates a new SessionInput with a stable ID after determining the
canonical `turnRelation`. Use `steer` to reuse the current Turn only when that
Turn is known to be `running`, the current Runtime Binding explicitly supports
steer, and no operation is pending. This path creates only one SessionInput and
a durable `SessionOperationRead(kind=steer)` effect committed in the same
transaction. It creates no second Turn, dispatch attempt, or queue entry and is
not limited by `queuedTurnCount`. Its `operationId` is the caller-provided
follow-up `requestId`, which provides the fence, retry identity, and
response-loss reconciliation identity.

A follow-up initially returns `accepted` only after the Input, operation, and
replayable effect commit together. The effect is `pending` before Runtime
confirmation. After response loss, it can continue to report accepted only if
the same identity remains replayable; a confirmed successful operation also
reports accepted consistently. If the result cannot be confirmed or replay is
unsafe, return `unknown` and create no new Input. A definite rejection or
bounded failure returns a `terminal` effect but does not rewrite an accepted
Input.

Use `new-turn` when the Runtime does not support steer or the current Turn is
not running. Only this path checks the queue limit and creates a Turn and
dispatch record. If the current state is `outcome_pending` or `unknown`, or an
operation is pending, reject explicitly and provide a query action for the
original Turn. Do not convert an unknown fact into a new Turn. Neither accepted
path creates a new AgentJob or rewrites the first launch result.

An Agent must create or advance an Issue and Workflow for business work that
needs continuous tracking to Done. Do not use a never-ending chat Job in place
of the execution layer. To start different work that needs an independent
launch record, launch again to get a new AgentJob and AgentSession.

## Session Activity

The AgentSession structure and user mental model are similar to sessions in
OpenCode and Pi. The AgentSession continuously retains messages and shows
whether it has a nonterminal Turn. Turn state distinguishes queued work from
running work.

SessionInput and AgentTurn both have stable identities. Each accepted Input
keeps one Input ID and is associated with exactly one Turn ID; retrying the same
request must not create a second pair of IDs. A request rejected because the
queue is full has no live Input or Turn ID, but it has a durable request
fingerprint, `reason`, and `nextAction` tombstone. An ordinary follow-up uses
`new-turn`. It can use `steer` to join the current running Turn only when the
Runtime explicitly supports steer. A steer Input also exposes
`steerOperationId`, `steerStatus`, `steerRetryAllowed`, and `steerNextAction`.
The Turn ID of an existing Input cannot change.

Input acceptance is an independent fact with value `accepted`, `rejected`, or
`unknown`. A steer effect separately has state `pending`, `accepted`, `unknown`,
or `terminal`. `accepted` means that the Input and durable effect are confirmed
or remain replayable. A pending effect does not mean that the Runtime finished.
`unknown` must block admission, and retry with the same identity is allowed only
while the same operation can still be replayed safely. `terminal` does not
change an accepted Input to rejected.

When the queue is full, persist a definitive rejection tombstone before
rejecting new input. A response-loss retry with the same `requestId` and
fingerprint always returns the same rejection. A changed payload returns
`idempotency_key_reused`; only a new `requestId` can retry later. After an Input
is accepted, a later dispatch failure does not delete it, change its ID, or mark
it rejected. Turn execution state is `queued`, `running`, `outcome_pending`,
`terminal`, or `unknown`; a Turn has no `idle` state.

Session Activity is `idle`, `active`, or `unknown`. Admission uses the single
`admission=ready|blocked` field with canonical `reason` and `nextAction`. The
current ContextGeneration is `idle` with `admission=ready` only when it has no
nonterminal Turn, pending side effect, or incomplete operation. A `queued`,
`running`, or `outcome_pending` Turn makes Activity `active` and blocks
admission. Activity is `unknown` when Input acceptance, a Runtime side effect,
the Runtime Binding, or the final result cannot be confirmed. Admission must
then remain blocked. Mohist cannot treat the Session as safely idle or use new
input to replay automatically.

- **Active Turn**: The current Turn can be queued or executing in the Runtime.
  A follow-up creates SessionInputs in order. It joins the current Turn when the
  backend supports that operation; otherwise it waits for a later Turn. New
  input is not accepted after the waiting queue reaches its boundary, and
  accepted input is never discarded. A queued Turn can be cancelled. After the
  Runtime starts, the user can request that the current Turn stop.
- **Idle**: No Turn is being processed. A follow-up creates a SessionInput and
  new Turn. Compact and Reset are available.
- **Unknown**: Mohist cannot currently confirm that the Runtime stopped or that
  an input was accepted. Until reconciliation finishes, Mohist does not treat
  the Session as safely idle or automatically redeliver the input.

After a Turn completes, fails, or stops, its AgentSession returns to idle when
no later Turn is queued. The result remains in the corresponding TaskRun,
AgentJob, or AgentTurn. The AgentSession is not marked completed, failed, or
closed and needs no `closed` lifecycle.

AgentSession content is shown continuously in occurrence order. A SessionInput
provides a stable association for each input, and an AgentTurn provides state
for the actual processing. Both are found and operated only through their
owning Session. Neither has a top-level list or independent management entry
point.

## Session Facts, Operations, and Context Boundaries

The Server is the canonical read model for AgentJobs, AgentSessions,
SessionInputs, and AgentTurns. The Web UI, CLI, and Agent Connections adapt
these facts. They cannot infer state independently from local logs, HTTP status,
Runner events, or provider responses. Every read result includes the Server
`revision` and `observedAt`.

The canonical AgentJob read result is `AgentJobLaunchRead`. It exposes at least
`jobId`, `launchRequestId`, `launchRequestFingerprint`, `launchOperationId`,
`status`, `outcome`, `reason`, `nextAction`, `workspace`, `workspaceReason`,
`target`, `targetReason`, and the current Session, Input, and Turn mappings.
`workspace` has type `{ projectId, workspaceId, path }`, and `target` is an
existing `ResourceKey`. An accepted launch must have non-null values for both
and null corresponding reasons. A value that is unresolved or not established
can be null only when its corresponding reason is non-null. A reservation ID
cannot masquerade as a live mapping.

A Session uses canonical `AgentSessionRead` and explicitly exposes
`admission=ready|blocked`, `reason`, `nextAction`, Activity, the Runtime Binding,
and unresolved targets. An Input uses `SessionInputRead`, and a Turn uses
`TurnResultRead`. A client cannot merge these facts into one state.

### Launch Identity and Response Loss

The caller supplies `launchRequestId` and a `launchRequestFingerprint` of the
complete envelope. When the AgentJob transaction first prepares, it looks up
`(projectId, agentId, launchRequestId)`. Response loss with the same fingerprint
returns the original operation or rejection. A changed payload returns
`idempotency_key_reused`, and only a new request ID can create a new launch. The
first request alone creates the unique `launchOperationId`, Job, internal
reservation, and durable accept-session command. A reservation is not an
accessible resource.

The Session transaction atomically commits only its Session, Input, Turn,
request map, dispatch record, and durable accept or reject event and outbox. It
cannot update the AgentJob in the same transaction. The launch coordinator uses
`launchOperationId` as its unique identity to consume the Session result, then
materializes the three live mappings or durable rejection in a separate
AgentJob transaction.

After response loss, the client first uses `launchRequestId` to find the
original operation, then queries or retries the original `launchOperationId`.
The Server must return the same IDs and must not create a second launch. After a
coordinator restart, it scans pending commands and consumes the same command
after claim or takeover. If Session acceptance succeeds but the Job write-back
fails, the Job temporarily keeps `null + mapping_pending` mappings until the
same operation writes them successfully. A definite rejection or unrecoverable
failure is a durable terminal outcome with stable `reason` and `nextAction`.
Temporary unavailability or an unconfirmed side effect remains `unknown` and
requires a query or manual reconciliation of the original operation.

### Operation Query

Compact, Reset, recovery, force-reset, handoff, rebind, and stop must use a
caller-provided `operationId`. A steer follow-up uses the caller-provided
`requestId` as the same operation identity. This ID is also the query and
response-loss retry identity. A query can read a current or historical
operation and returns the complete fields of the single canonical
`SessionOperationRead`, including an explicitly nullable `reason`, and the same
phase, outcome, Runtime Binding, context mapping, and `nextAction`. An operation
query must not cause another side effect, increment ContextGeneration, create a
candidate, or generate a new operation. Without an `operationId`, the Server
rejects the call and does not generate a replacement key that the client cannot
see.

The operation projection is part of the canonical read model. It must return
all fields required by that operation type and explicit null values. A Job,
Session, Input, or Turn can reference the same `operationId` or embed the same
projection. It cannot invent a reduced schema that contains only state or only
a mapping.

### Compact, Reset, and Force-reset

- **Compact** runs at a safely idle boundary and preserves the AgentSession,
  current Runtime Session, and ContextGeneration. On success, it persists a
  ContextBoundary and operation result, and later input continues in that
  generation.
- **Reset** creates a new physical Session without the old Runtime context at a
  safely idle boundary. It preserves the AgentSession, transcript, Input, and
  Turn identities, increments ContextGeneration, and uses the same `operationId`
  for query or retry.
- **Force-reset** is accepted only when an old Input, Turn, dispatch attempt,
  Runtime side effect, or operation still has an unknown result and blocks
  ordinary operations. It uses a new `operationId` and requires the current
  revision, expected ContextGeneration, complete expected Runtime Binding, and
  explicit confirmation that the old Runtime can still have side effects.
  `BeginForceReset` first looks up an existing operation by
  `sessionId + operationId`. When the existing kind, complete request
  fingerprint, expected revision, generation, and Runtime Binding match, it
  returns the original canonical operation even after response loss. A wrong
  kind, target, fingerprint, revision, generation, or Runtime Binding is
  explicitly rejected and creates no second context. A single collector
  collects these targets in the same Session transaction and adds any
  ActiveOperation to the same `supersededTargets` array. Each target is a
  canonical `UnresolvedTargetRead` with `targetKind`, stable `targetId`,
  `requestId`, `contextGeneration`, explicitly null `originalOperationId` when
  no source is known, complete or null `expectedBinding`, `nextAction`, and
  `supersededByOperationId`. Unknown facts require a target even when no
  ActiveOperation exists.

  The collector, `supersededTargets`, `unresolvedPrevious`, supersede marker on
  the old operation, complete fence on the new operation, and
  `admission=blocked` commit in one atomic transaction first. No new Input or
  Turn is accepted before the new candidate Runtime Binding and ContextBoundary
  commit atomically. The completion transaction increments ContextGeneration,
  writes the boundary, changes admission to ready, and then permits new input.
  Old targets and operations remain queryable by `targetId` or `operationId`.
  After response loss, reuse the same force-reset `operationId`; do not create a
  second context.

When candidate `getByKey` for a force-reset returns `definitely-rejected`, do
not treat the candidate's existence or absence as an unknown Runtime Binding.
For both a lookup after create response loss and same-key reconciliation after
a Server restart or repeated request, retain `candidateState=none`,
`candidateBinding=null`, and stable
`reason=force_reset_candidate_rejected`. Before the deadline, the outcome is
`pending`, `nextAction` is `retry_same_force_reset_candidate`, and the same
candidate key remains in use. At the deadline, the outcome is `blocked` and
`nextAction` is `inspect_force_reset_operation`. Neither branch performs
cleanup, reads an unconfirmed Runtime Binding, or constructs a CAS. Only
response loss or a generic unknown enters `candidateState=unknown` and requires
a query or manual reconciliation.

### ContextGeneration and Unresolved History

`ContextGeneration` starts at 1 and identifies the current logical context. It
is not the claim generation of an operation fence. Ordinary Compact does not
increment it. Reset, Runtime change, missing recovery, force-reset, handoff, and
rebind start a new logical context and increment it at the same Session
boundary. Each Input and Turn retains the generation in which it was created
and cannot move to a new context.

Current Activity is calculated only from the current generation. Pending
Inputs, Turns, side effects, and operation results from an older generation are
not combined with current `queued`, `running`, or `outcome_pending` state. They
are exposed through `unresolvedPrevious`, which contains at least the old
`operationId`, old ContextGeneration, outcome, and `nextAction` and can include
an unresolved count. An old Unknown stops blocking new Input and Turns in the
current generation only after force-reset is confirmed, the old operation is
superseded, and the new context and Runtime Binding boundary commits.

### Handoff, Rebind, and Bounded Dispatch Failure

- **handoff** is the only explicit operation that can change the Runner. Runner
  reconnect, timeout, or an old event cannot cause an implicit handoff.
- **rebind** can replace a Runtime Binding or physical Runtime Session only on
  the same Runner. It cannot use unknown facts to migrate across Runners.
- Both operations require an `operationId`, current revision, expected Runtime
  Binding, and bounded deadline. They are accepted only when the current
  generation is `idle` with no pending side effect. When the current generation
  is `active`, `outcome_pending`, or `unknown`, query the original operation
  first and choose force-reset only if it remains unknown. Success increments
  ContextGeneration, and events from the old Runtime Binding cannot change the
  current session.

After a Server restart, steer operations are scanned by
`operationId=requestId`. A pending or response-loss state first queries the same
effect and then claims or takes it over under the complete fence. Retry only
when the adapter can replay with the same identity. Otherwise retain
`steerStatus=unknown`, `steerRetryAllowed=false`, `admission=blocked`, and
`query_same_steer_or_force_reset`. A repeated follow-up with the same request
returns only the original Input and operation projection. It cannot create a
second effect or report a state as accepted without a replay path.

The Server-to-Runner steer effect uses the same `operationId=requestId` and
queryable `effectId={sessionId, operationId}`. Its request fixes the target
Session, Input, Turn, complete Runtime Binding and fence, and accepted original
text. The Runner handles it only through `apply`, `query` of that effect, or
`replay` of that effect. Repeating the effect identity does not create a second
provider effect. After restart, the Server queries first and replays only while
the complete fence and deadline remain valid and
`steerRetryAllowed=true`. Only `ProviderAccepted` means that the provider
accepted the effect. Product state cannot translate response loss, unknown, or
a stale fence into provider accepted.

If the target Turn is terminal before the steer call, or stop or force-reset
makes it inadmissible, the Server settles steer as stable terminal `rejected` in
the same fenced Session transaction. If the change occurs after adapter, query,
or replay starts, it settles as terminal `blocked` because the provider result
can no longer be decided. Both paths release ActiveOperation, set
`steerRetryAllowed=false` with stable `reason` and `nextAction`, preserve the
original Input and Turn IDs and accepted Input, stop retries, and never report
provider accepted. Repeated requests and restarts return only this durable
result.

Dispatch retry after Input acceptance requires canonical
`dispatchOperationId`, `dispatchAttemptCount`, fixed `dispatchDeadline`,
`dispatchAttemptId`, `dispatchLastResult`, `dispatchRetryId`, `retryAllowed`, and
complete `dispatchFence`. `dispatchRetryId` is the durable identity shared by
the outbox command, timer, due signal, and coordinator claim. State changes must
also persist `dispatchRetryOwnerId`, `dispatchRetryClaimGeneration`,
`dispatchRetryLeaseUntil`, and the session, operation, owner, owner fence,
claim, revision, attempt, retry, deadline, expected Runtime Binding, and
candidate Runtime Binding in `TurnDispatchRead/DispatchRetryWork`. After a
Server restart, the coordinator repairs work and claims or takes over an
expired lease only when `dispatchRetryId != null`, `retryAllowed=true`, the due
time is valid, and state permits it. A repeated signal is consumed only once.

Dispatch state and Turn state must stay synchronized:
`queued|retrying|blocked -> Turn.status=queued`,
`dispatched -> queued|running|outcome_pending`,
`unknown -> Turn.status=unknown`, and
`terminal -> Turn.status=terminal`. Querying the same attempt returns
accepted-before-start to `dispatched + queued` and accepted-and-started to
`dispatched + running`, while a definite terminal result writes the canonical
Turn result. Response loss creates no new Input, Turn, or attempt.

At the attempt-count or deadline limit, the Server can atomically write the
single terminal `dispatchStatus=terminal`, Turn `status=terminal`, Turn
`outcome=blocked`, stable `blockedReason`, and actionable `nextAction` only when
the last result of a durable `dispatchAttemptId` is `definitely-rejected`, the
attempt or deadline is at its boundary, the Input remains accepted, and its
Input and Turn IDs remain unchanged. With no attempt, an undelivered outbox,
response loss, an unknown result, or no definitely-rejected evidence, retain
only `dispatchStatus=unknown`, Turn `status=unknown`, Turn `reason` and
`nextAction`, and `query_same_attempt_or_manual_reconcile`; do not write
terminal blocked. Temporary `blocked` means only definitely-rejected with a
valid retry due. It is not terminal and cannot be awakened through
`nextAction` alone.

Retain the canonical Turn `result` only for `outcome=completed`. For `failed`,
`cancelled`, or `blocked`, persist `result=null` even if the Runtime returns an
attached payload, while retaining `reason` and `nextAction`.

When `retainUnknownWithoutRetry` clears retry identity, it must persist
`retryAllowed=false`, `dispatchRetryId=null`, `dispatchRetryDueAt=null`, no due
time, lease, or owner, and the unknown Turn and dispatch with actionable
`nextAction`. Restart and repeated reconciliation can retain only unknown; they
cannot wake work automatically from a null identity.

Every dispatch claim or takeover, enqueue, query, reschedule, and blocked,
terminal, or unknown write must carry the complete `DispatchFenceToken`:
session, operation, owner, owner fence, claim generation, revision, attempt and
retry IDs, lease and deadline, expected Runtime Binding, candidate Runtime
Binding, and `bindingAtEffect`. A late result from Owner A after Owner B takes
over or completes can return only `stale_operation_fence`.

The coordinator must check `now >= dispatchDeadline` before
`claimDueOrTakeOver`; it cannot first create an expired lease and then let the
fence fail. Under the current dispatch-record fence, this check atomically
increments revision and cancels retry work. If the current attempt has durable
`dispatchLastResult=definitely-rejected`, it writes the single terminal
`dispatchStatus=terminal`, `Turn.status=terminal`, and
`Turn.outcome=blocked`. If there is no attempt, the outbox was not delivered, or
the result is `unknown`, it writes `dispatchStatus=unknown`,
`Turn.status=unknown`, and Turn `reason` and `nextAction`, retains the same
`dispatchAttemptId`, and requires a query or manual reconciliation. Both paths
clear the retry lease and fence and write new record and Session revisions.

**Stop** uses the single `SessionOperationRead.kind=stop`. `mo session cancel`
still cancels a queued or active Turn. The caller must provide a stable
`operationId`, expected revision and Runtime Binding, and bounded deadline. The
BeginStop operation row durably stores the target Turn, request fingerprint,
expected revision and generation, complete FenceToken, owner and claim
generation, lease, `reason`, and `nextAction`. Its idempotency order is:

```text
BeginStop(sessionId, operationId, turnId, fingerprint, expectedRevision,
          expectedContextGeneration, expectedBinding):
  atomically:
    existing = read operationId
    if existing != null:
      require existing.kind == stop
      require existing.targetTurnId == turnId
      require existing.requestFingerprint == fingerprint
      require existing.expectedRevision == expectedRevision
      require existing.expectedContextGeneration == expectedContextGeneration
      require existing.expectedBinding == expectedBinding
      return full canonical operation/result projection
    require expected revision/binding and expectedContextGeneration match
    require current Turn == turnId and Turn is not terminal
    create stop operation
    if Turn is queued:
      cancel the same dispatch retry work
      persist dispatchStatus=terminal, dispatchLastResult=none, retryAllowed=false,
        dispatchRetryId=null, dispatchRetryKind=none, dispatchRetryDueAt=null,
        dispatchRetryState=none, dispatchRetryOwnerId=null,
        dispatchRetryClaimGeneration=null, dispatchRetryLeaseUntil=null,
        dispatchFence=null
      persist Turn.status=terminal, outcome=cancelled,
        reason=stop_requested, nextAction=inspect_turn
      complete the operation in the same Session transaction
  if Turn is running:
    recheck the complete pre-token before Runtime.stop and after the response
    persist cancelled only when the same fence still matches
```

The existing-operation lookup precedes the non-terminal Turn check, so response loss after a
successful queued cancel or running stop returns the original canonical projection even when the
Turn is already terminal. Wrong operation kind, target, fingerprint, revision, or binding is
explicitly rejected. A running Turn whose `Runtime.stop` response is lost is updated in the same
fenced Session transaction to `dispatchStatus=unknown`, `dispatchLastResult=unknown`,
`Turn.status=unknown`, `Turn.outcome=null`, `Turn.reason=stop_result_unknown`, while retaining the
original `dispatchAttemptId`; no new attempt is created. Query first uses the same stop operation and
attempt, and a bounded retry reuses that operation's provider idempotency identity. If still unknown
at the deadline, operation and Turn remain unknown with query/manual nextAction, never cancelled or
idle.

## AgentSession Origin

Each AgentSession has exactly one Origin:

- **Workflow Origin**: Addressed by `WorkflowRun + session name`; a task with
  the same name can continue the context.
- **Agent launch Origin**: Created for each Mohist Agent launch and associated
  with that Agent ID.
- **Agent Connection Origin**: Started by an Agent Connection such as Slack and
  associated with that Agent ID. It is still a session of the same Mohist Agent,
  not the connection's own session copy.

Origin does not change during the Session lifecycle. Matching Model, prompt,
and execution-backend configuration does not merge two Sessions. Replacing the
current Runtime Session does not change AgentSession Origin.

For every Origin, the CLI addresses the top-level `mo session` surface:

- `mo session view <session-id>` and
  `mo session transcript <session-id>` read by stable Session ID. There are no
  separate commands by Origin.
- `mo session followup`, `compact`, `reset`, and `cancel` also accept only a
  Session ID. `cancel` additionally requires `--turn-id`, `--operation-id`, the
  current revision and Runtime Binding, and a bounded deadline.
- `mo session list` filters by one of `--agent <agent>`, `--issue <number>`, or
  `--run <run-id>`. Origin is only a discovery condition. `--agent` lists the
  Agent's sessions created by direct launch, Agent Connection, or another
  supported entry point.
- `mo session cancel` cancels the current queued or active AgentTurn through the
  canonical stop operation. A queued Turn becomes cancelled and clears durable
  dispatch retry in the Session transaction. An active Turn first performs a
  fenced `Runtime.stop`. If it is the launch's first Turn, the AgentJob ends
  with failure category `cancelled`; cancelling a later Turn does not modify the
  original AgentJob. When the stop result is unknown, the operation and Turn
  remain `unknown` and provide a query or manual-check `nextAction`.

## Current Runtime Session and Missing Recovery

An AgentSession ID is the stable Mohist identity. An OpenCode Session or Pi
Session is the current physical session in the execution backend. An
AgentSession stores only the current association and no physical Session
history.

Ordinarily, every later input reuses the current Runtime Session. A task change,
retry, Model change, Compact, execution completion, or Runner restart cannot
replace it. A new physical Session can come only from user Reset, an explicit
execution-backend change, or this limited confirmed-missing recovery: a probe
on the same Runner explicitly returns `definitely-missing`, the current
generation has `activity=idle`, there is no unknown Input, Turn, dispatch, or
Runtime effect, and the Session has `admission=ready`. Only then can recovery
create a candidate, swap the Runtime Binding through the complete
expected-binding CAS, and use the post fence returned by CAS to complete the
boundary and result write.

A disconnect, timeout, Runner restart, unavailable result, permission error,
non-404 result, or any result that does not prove the physical Session is absent
enters only `ObservationUnknown` and retains the original Runtime Binding. Even
after a probe explicitly reports missing, recovery is limited to observation
and query and enters `RecoveryObservationOnly` when the current generation is
`running`, `outcome_pending`, or `unknown`, or when an old side effect can have
occurred. The Session retains its Runtime Binding and Turn, sets
`admission=blocked`, and provides
`nextAction=query_runtime_or_force_reset`. It cannot automatically rebind or
replay. A new generation can become ready only after the force-reset collector,
candidate, CAS, and ContextBoundary complete in the same atomic order.

Every Runtime Binding CAS uses the single
`compareAndSwapBinding(preToken)`. The pre-token must match the expected Runtime
Binding, revision, owner and lease, operation, and candidate. CAS atomically
swaps the Runtime Binding, increments revision and BindingEpoch, and returns
`postFence/currentBinding=candidate`. Later effects, results, and completion can
use only the post fence; old pre-token and post-token owners fail closed. A
replacement does not change AgentSession ID, Origin, working directory, or
recorded session content. The new Session starts with empty context, the
conversation records "Context reset," and old messages are not replayed.

Candidate identity is the complete pair `(operation.candidateKey, operation.candidateBinding)`;
there is no separate adopted-candidate field on `BindingTuple`. The create result keeps the
distinction between `response_lost` and generic `unknown`. On `response_lost`, the same fenced
operation first calls `getByKey` with `candidateBinding=null`; only a second response loss or
generic unknown persists `candidateState=unknown`, `candidateBinding=null`, `admission=blocked`,
and `query_same_candidate_or_manual`. An explicit absent result leaves the same operation pending
with `retry_same_force_reset_candidate`. A ready candidate must have the exact key, a complete
binding matching target runner/runtime, and `bindingEpoch=expected+1`; only after that pair is
persisted as `candidateState=created` and reloaded may CAS use it. A complete mismatched candidate
goes to an independent cleanup fence without CAS with reason
`force_reset_candidate_identity_mismatch`; an incomplete candidate remains unknown with
`operator_reconcile_candidate`. Restart and duplicate requests use the same operation/key lookup
and never create a second candidate, context, or CAS. Candidate cleanup carries the same pair in
its operation read and uses an independent cleanup fence. Every attempt, phase, identity or
`cleanup-pending` write increments revision and is reloaded before `getByKey` or `discardCandidate`;
a changed current binding creates a new bounded cleanup fence, and an adopted/current candidate or
an expired/stale cleanup fails closed without discard.

See
[Shared Semantics for Agent Execution Actions](actions/README.md#shared-semantics-for-agent-execution-actions)
for reuse invariants, automatic recovery boundaries, and concurrency rules.

## AgentSession Operations

AgentSessions with Workflow and Agent launch Origins use the same session
operations:

- **Follow-up**: Appends user input to the current session. It joins the current
  execution when one is running and starts a new execution when idle. It creates
  no Mohist Agent or AgentJob. Steer during execution creates a durable steer
  operation and effect committed in the same transaction as the Input. OpenCode
  `promptAsync` and Pi `session.steer` are adapter channels only.
- **Compact**: Asks the current execution backend to compress context while
  preserving the AgentSession and current Runtime Session.
- **Reset**: Creates a new physical Session without the old Runtime context
  while idle, preserving AgentSession identity and existing conversation
  content.

These operations change the session, not work ownership. A follow-up does not
turn a TaskRun into an AgentJob. Compact and Reset do not launch the Mohist Agent
again.

## Current Scope

The `mohist/opencode` and `mohist/pi` Workflow Actions are implemented; see
their Action documents for configuration. A Mohist Agent selects OpenCode or Pi
through its configuration, and the snapshot fixes that backend to the AgentJob.
The Web UI and CLI can create, edit, and launch a Mohist Agent and read and
continue an AgentSession. The `mohist/agent` contract is defined but not
implemented. See [Agent Event Routing](event-routing.md) for Mohist Agent event
responses.

## Implementation Gaps

Automatic reconstruction and rebinding after a missing Runtime Session are not
fully implemented. Some current execution paths still fail and require user
Reset. An implementation Issue must be created from this spec.

### Implementation gap: caller key is required

The product contract requires a caller-provided follow-up `requestId` and a
caller-provided `operationId` for Compact, Reset, recovery, handoff, rebind,
stop, and force-reset. A missing or empty key must be rejected before
acceptance, and the Server must not generate a replacement key hidden from the
client. Current routes and Grains still contain paths that generate a hidden
key when the caller key is absent. This is future implementation work, not an
exception to the product contract or evidence that implementation is complete.
The migration boundary is the canonical Server admission and operation layer.
Every entry point first passes the caller key, and that layer validates it as
nonempty before writing any operation, Input, Turn, or external effect. This gap
must remain visible until migration is complete.

Max concurrent runs does not yet enforce concurrency.

Agent Connection Readiness provides minimal configuration derivation. It shows
Needs setup when AgentConfig lacks a Model or Runtime while keeping Connection
health independent. It shows Ready when both are present, and an Agent that has
not been probed defaults to Unknown. Complete Runner and Runtime executability
probing remains future work, so a real launch can still find additional gaps.

SessionInput and AgentTurn are not fully implemented as stable child records of
a Session. Existing launch and follow-up results, transcripts, and live updates
cannot yet answer separately which input was accepted and which Runtime Turn is
processing.

Agent Connections, Slack Bots, connection permissions, and connection state are
not implemented. The current invocation interface also lacks the authentication,
duplicate-request protection, and resumable execution events required for safe
external clients. See the target contracts in
[`design/agent-api.md`](../design/agent-api.md) and
[`design/slack.md`](../design/slack.md).
